using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;

namespace ScreenNote;

internal sealed class ScrollCaptureWindow : Window
{
    private readonly Drawing.Rectangle _region;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 23, Margin = new Thickness(0, 12, 0, 18) };
    private readonly Button _start = new() { Content = "开始采集" };
    private readonly Button _finish = new() { Content = "完成并复制", IsEnabled = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(65) };
    private ScrollWheelGate? _wheel;
    private bool _gap;
    private ScrollStitcher? _stitcher;
    private Task? _sampling;
    private bool _active, _closed, _excluded, _finishing;
    internal BitmapSource? CapturedImage { get; private set; }
    internal Func<BitmapSource>? FrameSource { get; set; }

    internal ScrollCaptureWindow(Drawing.Rectangle region)
    {
        _region = region;
        Title = "拾屏 精简版 · 滚动截图"; Icon = AppIcon.WindowImage; Width = 430;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "向下滚动，拼成一张长图", FontSize = 20, FontWeight = FontWeights.SemiBold });
        _status.Text = $"选区 {_region.Width} × {_region.Height}\n点击开始后，在选区内向下滚动。快速滚轮会自动分成小步，采集后再继续。\n尽量只框选正文，避开悬浮按钮和固定页脚。";
        panel.Children.Add(_status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        _start.Click += async (_, _) => await StartAsync();
        _finish.Click += async (_, _) => await FinishAsync();
        if (TryFindResource("PrimaryButton") is Style style) _finish.Style = style;
        buttons.Children.Add(cancel); buttons.Children.Add(_start); buttons.Children.Add(_finish); panel.Children.Add(buttons);
        Content = new Border { Background = Brushes.White, Padding = new Thickness(24), Child = panel };
        SourceInitialized += (_, _) => _excluded = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11);
        _timer.Tick += async (_, _) =>
        {
            if (!_active || _sampling is { IsCompleted: false }) return;
            _sampling = SampleAsync(); await _sampling;
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && _active) { e.Handled = true; await FinishAsync(); }
        };
        Closing += OnClosing;
    }

    private BitmapSource Capture() => FrameSource?.Invoke() ?? CaptureWindow.CaptureDesktop(_region);
    private async Task StartAsync()
    {
        if (_active) return;
        if (!_excluded && FrameSource == null) { _status.Text = "系统未能隐藏控制条，请在 Windows 10 2004 或更新的本地桌面重试。"; return; }
        _start.IsEnabled = false;
        try
        {
            var first = await Task.Run(Capture);
            if (_closed) return;
            _stitcher = new ScrollStitcher(first);
            if (FrameSource == null) _wheel = new ScrollWheelGate(_region);
            _active = true;
            _finish.IsEnabled = true; _start.Visibility = Visibility.Collapsed;
            _status.Text = "已采集第一屏。把鼠标放到选区内向下滚动，快速滚轮会自动匀速采集。\n滚动结束后再点击完成。";
            _timer.Start();
        }
        catch (Exception ex) { if (!_closed) { _status.Text = ex.Message; _start.IsEnabled = true; } }
    }

    private async Task SampleAsync()
    {
        try
        {
            if (_wheel?.ReplayStep() == true) await Task.Delay(55);
            if (_closed || !_active) return;
            var result = await Task.Run(() => { var frame = Capture(); return _active ? _stitcher!.Append(frame) : StitchResult.Unchanged; });
            if (_closed || !_active) return;
            var details = $"已拼接 {_stitcher!.Captures} 屏 · {_stitcher.Width} × {_stitcher.Height} 像素\n";
            _gap = result == StitchResult.NoOverlap;
            if (_gap) _wheel?.Clear();
            _status.Text = details + (_gap ? "页面跳过了可拼接范围。已暂停后续滚动，请向上回到上一屏，找到重叠后再继续；缺段时不会直接导出。"
                : result == StitchResult.LimitReached ? "已达到长图大小上限，请点击完成。" : _wheel?.Pending != 0 && _wheel != null ? "正在分步处理快速滚动，请将鼠标保持在选区内。" : "已接入最新内容。继续向下滚动，或点击「完成并复制」。");
            if (result == StitchResult.LimitReached) { _timer.Stop(); _active = false; _wheel?.Dispose(); _wheel = null; }
        }
        catch (Exception ex) { if (!_closed) { _timer.Stop(); _active = false; _wheel?.Dispose(); _wheel = null; _status.Text = "采集已停止：" + ex.Message; } }
    }

    private async Task FinishAsync()
    {
        if (_stitcher == null || _finishing) return;
        _finishing = true;
        // Finish is a boundary: stop accepting new wheel gestures and sample the
        // settled page. Unreplayed gestures have not moved the page or lost content.
        _wheel?.Dispose(); _wheel = null; _timer.Stop();
        _finish.IsEnabled = false;
        if (_sampling != null) await _sampling;
        _active = false;
        if (_closed) return;
        try
        {
            // One final sample includes a scroll that settled just before Finish was clicked.
            var frame = await Task.Run(Capture);
            if (_closed) return;
            var result = _stitcher.Append(frame);
            if (result == StitchResult.NoOverlap)
            {
                _gap = true; _finishing = false; _finish.IsEnabled = true;
                _active = true;
                if (FrameSource == null) _wheel = new ScrollWheelGate(_region);
                _timer.Start();
                _status.Text = "当前页面与已采集内容不连续。请向上回到上一屏再继续，避免导出缺段长图。";
                return;
            }
            CapturedImage = _stitcher.Export(); Close();
        }
        catch (Exception ex) { _status.Text = "长图未完成：" + ex.Message; _finishing = false; _finish.IsEnabled = true; }
    }

    private void OnClosing(object? sender, CancelEventArgs e) { _closed = true; _active = false; _timer.Stop(); _wheel?.Dispose(); _wheel = null; }
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}
