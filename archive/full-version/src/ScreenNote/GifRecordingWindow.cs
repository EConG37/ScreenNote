using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace ScreenNote;

internal sealed class GifRecordingWindow : Window
{
    private readonly Drawing.Rectangle _region;
    private readonly string _dataDirectory;
    private readonly StackPanel _content = new();
    private readonly TextBlock _heading = new() { Text = "录制选区为 GIF", FontSize = 21, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16), LineHeight = 22 };
    private readonly Button _primary = new() { Content = "开始录制", MinWidth = 110 };
    private readonly CheckBox _smallGif = new() { Content = "录制后压缩为微信小图（目标小于 1 MB）", IsChecked = true, Margin = new Thickness(0, 0, 0, 12) };
    private string _compressionNote = "";
    private CancellationTokenSource? _stop;
    private Task? _operation;
    private bool _running, _recording, _discard, _closeWhenStopped, _excluded;
    private GifRecordingResult? _result;

    internal GifRecordingWindow(Drawing.Rectangle region, string dataDirectory)
    {
        _region = region; _dataDirectory = dataDirectory;
        Title = "拾屏 · 录制 GIF"; Icon = AppIcon.WindowImage;
        Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;
        Background = Brushes.White;
        var size = GifRecorder.OutputSize(region.Size);
        _status.Text = $"已选 {region.Width} × {region.Height} 像素\n生成 {size.Width} × {size.Height} · 10 帧/秒 · 包含鼠标\n倒计时 3 秒后开始，最长 60 秒或约 50 MB。";
        _content.Children.Add(_heading); _content.Children.Add(_status);
        _content.Children.Add(_smallGif);
        if (TryFindResource("PrimaryButton") is Style primaryStyle) _primary.Style = primaryStyle;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var compressExisting = new Button { Content = "压缩已有 GIF", Margin = new Thickness(0, 0, 8, 0) };
        compressExisting.Click += async (_, _) =>
        {
            if (_running) return;
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "GIF 动图|*.gif", Title = "选择要压缩的 GIF" };
            if (picker.ShowDialog(this) == true) { _operation = CompressExistingAsync(picker.FileName); await _operation; }
        };
        buttons.Children.Add(compressExisting); buttons.Children.Add(cancel); buttons.Children.Add(_primary); _content.Children.Add(buttons);
        Content = new Border { Background = Brushes.White, Padding = new Thickness(24), Child = _content };
        _primary.Click += async (_, _) =>
        {
            if (_running) { _primary.IsEnabled = false; _stop?.Cancel(); return; }
            _operation = StartAsync(); await _operation;
        };
        SourceInitialized += (_, _) => _excluded = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            && SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_recording) { _primary.IsEnabled = false; _stop?.Cancel(); }
            else Close();
            e.Handled = true;
        };
        Closing += OnClosing;
        Closed += (_, _) => _stop?.Dispose();
    }

    private async Task StartAsync()
    {
        if (!_excluded)
        {
            _status.Text = "系统未能将控制条从录制画面中排除。请在 Windows 10 2004 或更新版本的本地桌面中重试。";
            return;
        }
        _stop?.Dispose(); _stop = new CancellationTokenSource();
        _running = true; _discard = false; _primary.Content = "取消倒计时";
        try
        {
            for (var seconds = 3; seconds > 0; seconds--)
            {
                _heading.Text = $"{seconds} 秒后开始录制";
                _status.Text = $"仅录制已选的 {_region.Width} × {_region.Height} 区域。\n准备好要演示的窗口，控制条不会录入画面。";
                await Task.Delay(1000, _stop.Token);
            }
            _recording = true; _heading.Text = "● 正在录制";
            _primary.Content = "停止并复制";
            var progress = new Progress<GifRecordingProgress>(value =>
            {
                if (_recording) _status.Text = $"{value.Elapsed:mm\\:ss} / 01:00  ·  {value.Bytes / 1048576d:F1} MB\n点击「停止并复制」结束。控制条内按 Esc 也可停止。";
            });
            _status.Text = "正在录制选区…";
            _result = await GifRecorder.RecordAsync(_region, _dataDirectory, _stop.Token, progress);
            _recording = false;
            if (_discard) { File.Delete(_result.Path); _result = null; return; }
            if (_smallGif.IsChecked == true)
            {
                _stop.Dispose(); _stop = new CancellationTokenSource();
                await CompressResultAsync(_result.Path);
            }
            if (_discard) return;
            ShowResult();
            await CopyResultAsync();
        }
        catch (OperationCanceledException) { _heading.Text = "录制已取消"; _status.Text = "没有生成 GIF，可以重新开始。"; }
        catch (Exception ex)
        {
            _heading.Text = _result == null ? "录制未完成" : "GIF 已保留";
            _status.Text = ex.Message;
        }
        finally
        {
            _recording = false; _running = false;
            _primary.IsEnabled = true; _primary.Content = "开始录制";
            if (_closeWhenStopped) Close();
        }
    }

    private async Task CompressResultAsync(string path)
    {
        _heading.Text = "正在压缩 GIF"; _primary.Content = "停止压缩"; _primary.IsEnabled = true;
        try
        {
            var compressed = await GifCompressor.CompressAsync(path, Path.Combine(_dataDirectory, "recordings"), _stop!.Token,
                new Progress<string>(message => { if (_running) _status.Text = message; }));
            _compressionNote = $"原文件 {compressed.OriginalBytes / 1048576d:F2} MB → 小图 {compressed.Bytes / 1048576d:F2} MB";
            if (!compressed.MetTarget) _compressionNote += "，仍高于目标，可缩短录制后再试";
            _result = new GifRecordingResult(compressed.Path, compressed.Duration, compressed.Frames, compressed.Bytes, "");
        }
        catch (OperationCanceledException)
        {
            _compressionNote = "已停止压缩，原 GIF 已保留。";
            if (_result == null) { _heading.Text = "压缩已取消"; _status.Text = _compressionNote; }
        }
        catch (Exception ex) when (_result != null) { _compressionNote = "压缩未完成，原 GIF 已保留：" + ex.Message; }
    }

    private async Task CompressExistingAsync(string path)
    {
        _running = true; _discard = false; _stop?.Dispose(); _stop = new CancellationTokenSource();
        try
        {
            await CompressResultAsync(path);
            if (_discard || _result == null) return;
            ShowResult(); await CopyResultAsync();
        }
        catch (Exception ex) { _heading.Text = "压缩未完成"; _status.Text = ex.Message; }
        finally { _running = false; _primary.IsEnabled = true; _primary.Content = "开始录制"; if (_closeWhenStopped) Close(); }
    }

    private void ShowResult()
    {
        var result = _result!;
        _heading.Text = "GIF 已生成"; _content.Children.Clear();
        _content.Children.Add(_heading); _content.Children.Add(_status);
        var thumbnail = new Image { Height = 160, Stretch = Stretch.Uniform, Cursor = Cursors.Hand, ToolTip = "按住拖入聊天窗口发送 GIF" };
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(result.Path); image.EndInit(); image.Freeze(); thumbnail.Source = image;
        Point? dragStart = null;
        thumbnail.MouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(thumbnail);
        thumbnail.MouseLeftButtonUp += (_, _) => dragStart = null;
        thumbnail.MouseMove += (_, e) =>
        {
            if (dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(thumbnail) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragStart = null;
            try { DragDrop.DoDragDrop(thumbnail, GifClipboard.CreateData(result.Path), DragDropEffects.Copy); }
            catch (Exception ex) { _status.Text = "拖拽未完成：" + ex.Message; }
        };
        _content.Children.Add(thumbnail);
        _content.Children.Add(new TextBlock { Text = "首帧预览 · 拖入聊天窗口可发送完整动图", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 7, 0, 14), HorizontalAlignment = HorizontalAlignment.Center });
        var actions = new WrapPanel();
        void Add(string title, Action action)
        {
            var button = new Button { Content = title, Margin = new Thickness(0, 0, 7, 8), Padding = new Thickness(11, 7, 11, 7) };
            button.Click += (_, _) => { try { action(); } catch (Exception ex) { _status.Text = ex.Message; } };
            actions.Children.Add(button);
        }
        var copy = new Button { Content = "复制 GIF", Margin = new Thickness(0, 0, 7, 8) };
        copy.Click += async (_, _) => { copy.IsEnabled = false; try { await CopyResultAsync(); } finally { copy.IsEnabled = true; } };
        actions.Children.Add(copy);
        Add("查看动画", () => Process.Start(new ProcessStartInfo(result.Path) { UseShellExecute = true }));
        var compress = new Button { Content = "压缩 GIF", Margin = new Thickness(0, 0, 7, 8) };
        compress.Click += async (_, _) =>
        {
            if (_running) { _stop?.Cancel(); return; }
            compress.Content = "停止压缩";
            try { _operation = CompressExistingAsync(result.Path); await _operation; }
            finally { compress.Content = "压缩 GIF"; }
        };
        actions.Children.Add(compress);
        Add("另存为", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "GIF 动图|*.gif", FileName = Path.GetFileName(result.Path), AddExtension = true, DefaultExt = ".gif" };
            if (dialog.ShowDialog(this) == true && !string.Equals(Path.GetFullPath(dialog.FileName), result.Path, StringComparison.OrdinalIgnoreCase))
            { File.Copy(result.Path, dialog.FileName, overwrite: true); _status.Text = "GIF 已另存到：" + dialog.FileName; }
        });
        Add("打开文件夹", () => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", result.Path }, UseShellExecute = true }));
        Add("关闭", Close);
        _content.Children.Add(actions);
    }

    private async Task CopyResultAsync()
    {
        var result = _result!;
        var details = $"{result.Duration.TotalSeconds:F1} 秒 · {result.Frames} 帧 · {result.Bytes / 1048576d:F1} MB";
        if (_compressionNote.Length > 0) details += "\n" + _compressionNote;
        if (result.StopReason.Length > 0) details += " · " + result.StopReason;
        try
        {
            await GifClipboard.CopyAsync(result.Path);
            _status.Text = details + "\n已复制 GIF，可在聊天窗口按 Ctrl+V。\n微信是否直接展示动画，以当前客户端实测为准。";
        }
        catch (Exception ex) { _status.Text = details + "\nGIF 已保存，复制未完成：" + ex.Message; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        e.Cancel = true; _discard = true; _closeWhenStopped = true; _stop?.Cancel();
    }

    internal async Task DiscardAndCloseAsync()
    {
        if (!_running) { Close(); return; }
        _discard = true; _closeWhenStopped = true; _stop?.Cancel();
        if (_operation != null) await _operation;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}
