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
using Drawing = System.Drawing;

namespace ScreenNote;

internal sealed class VideoRecordingWindow : Window
{
    private readonly Drawing.Rectangle _region;
    private readonly string _dataDirectory;
    private readonly StackPanel _content = new();
    private readonly TextBlock _heading = new() { Text = "录制选区为视频", FontSize = 21, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16), LineHeight = 22 };
    private readonly Button _primary = new() { Content = "开始录制", MinWidth = 110 };
    private readonly CheckBox _systemSound = new() { Content = "录制系统声音", IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
    private readonly CheckBox _microphone = new() { Content = "录制麦克风", IsChecked = false, Margin = new Thickness(0, 0, 0, 12) };
    private CancellationTokenSource? _stop;
    private Task? _operation;
    private bool _running, _recording, _discard, _closeWhenStopped, _excluded;
    private VideoRecordingResult? _result;

    internal VideoRecordingWindow(Drawing.Rectangle region, string dataDirectory)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        _region = region; _dataDirectory = dataDirectory;
        Title = "拾屏 精简版 · 录制视频"; Icon = AppIcon.WindowImage;
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;
        Background = StudioUi.Brush("Canvas");
        StudioUi.Label(_primary, "play", "开始录制");
        _status.Foreground = StudioUi.Brush("Muted"); _status.FontSize = 13;
        var size = VideoRecorder.OutputSize(region.Size);
        _status.Text = $"已选 {region.Width} × {region.Height} 像素\n生成 {size.Width} × {size.Height} · 30 帧/秒 · 包含鼠标\n倒计时 3 秒后开始，最长 5 分钟。";
        _content.Children.Add(_heading); _content.Children.Add(_status);
        if (!WasapiAudio.SystemSoundAvailable) { _systemSound.IsChecked = false; _systemSound.IsEnabled = false; _systemSound.Content = "录制系统声音（未找到播放设备）"; }
        if (!WasapiAudio.MicrophoneAvailable) { _microphone.IsEnabled = false; _microphone.Content = "录制麦克风（未找到麦克风）"; }
        _content.Children.Add(_systemSound); _content.Children.Add(_microphone);
        if (TryFindResource("PrimaryButton") is Style primaryStyle) _primary.Style = primaryStyle;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 8, 0) };
        StudioUi.Label(cancel, "close", "取消");
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel); buttons.Children.Add(_primary); _content.Children.Add(buttons);
        var surface = StudioUi.Panel(_content, new Thickness(24)); surface.Margin = new Thickness(16); Content = surface;
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
        _running = true; _discard = false; StudioUi.Label(_primary, StudioUi.ActionIcon("取消倒计时"), "取消倒计时");
        _systemSound.IsEnabled = false; _microphone.IsEnabled = false;
        try
        {
            for (var seconds = 3; seconds > 0; seconds--)
            {
                _heading.Text = $"{seconds} 秒后开始录制";
                _status.Text = $"仅录制已选的 {_region.Width} × {_region.Height} 区域。\n准备好要演示的窗口，控制条不会录入画面。";
                await Task.Delay(1000, _stop.Token);
            }
            _recording = true; _heading.Text = "● 正在录制";
            StudioUi.Label(_primary, StudioUi.ActionIcon("停止并复制"), "停止并复制");
            var progress = new Progress<VideoRecordingProgress>(value =>
            {
                if (_recording) _status.Text = $"{value.Elapsed:mm\\:ss} / 05:00  ·  {value.Bytes / 1048576d:F1} MB\n点击「停止并复制」结束。控制条内按 Esc 也可停止。";
            });
            _status.Text = "正在录制选区…";
            _result = await VideoRecorder.RecordAsync(_region, _dataDirectory,
                _systemSound.IsChecked == true, _microphone.IsChecked == true, _stop.Token, progress);
            _recording = false;
            if (_discard) { File.Delete(_result.Path); _result = null; return; }
            ShowResult();
            await CopyResultAsync();
        }
        catch (OperationCanceledException) { _heading.Text = "录制已取消"; _status.Text = "没有生成视频，可以重新开始。"; }
        catch (Exception ex)
        {
            _heading.Text = _result == null ? "录制未完成" : "视频已保留";
            _status.Text = ex.Message;
        }
        finally
        {
            _recording = false; _running = false;
            _primary.IsEnabled = true; StudioUi.Label(_primary, StudioUi.ActionIcon("开始录制"), "开始录制");
            _systemSound.IsEnabled = WasapiAudio.SystemSoundAvailable; _microphone.IsEnabled = WasapiAudio.MicrophoneAvailable;
            if (_closeWhenStopped) Close();
        }
    }

    private void ShowResult()
    {
        var result = _result!;
        _heading.Text = "视频已生成"; _content.Children.Clear();
        _content.Children.Add(_heading); _content.Children.Add(_status);
        if (result.PosterFrame != null)
        {
            var thumbnail = new Image { Height = 160, Stretch = Stretch.Uniform, Cursor = Cursors.Hand, ToolTip = "按住拖入聊天窗口发送视频", Source = result.PosterFrame };
            Point? dragStart = null;
            thumbnail.MouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(thumbnail);
            thumbnail.MouseLeftButtonUp += (_, _) => dragStart = null;
            thumbnail.MouseMove += (_, e) =>
            {
                if (dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
                var delta = e.GetPosition(thumbnail) - start;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragStart = null;
                try { DragDrop.DoDragDrop(thumbnail, GifClipboard.CreateData(result.Path, "视频"), DragDropEffects.Copy); }
                catch (Exception ex) { _status.Text = "拖拽未完成：" + ex.Message; }
            };
            _content.Children.Add(thumbnail);
            _content.Children.Add(new TextBlock { Text = "首帧预览 · 拖入聊天窗口可发送视频文件", Foreground = StudioUi.Brush("Muted"), FontSize = 13, Margin = new Thickness(0, 7, 0, 14), HorizontalAlignment = HorizontalAlignment.Center });
        }
        var actions = new WrapPanel();
        void Add(string title, Action action)
        {
            var button = new Button { Content = title, Margin = new Thickness(0, 0, 7, 8), Padding = new Thickness(11, 7, 11, 7) };
            StudioUi.Label(button, StudioUi.ActionIcon(title), title);
            button.Click += (_, _) => { try { action(); } catch (Exception ex) { _status.Text = ex.Message; } };
            actions.Children.Add(button);
        }
        var copy = new Button { Content = "复制视频", Margin = new Thickness(0, 0, 7, 8) };
        StudioUi.Label(copy, "copy", "复制视频"); copy.Style = (Style)FindResource("PrimaryButton");
        copy.Click += async (_, _) => { copy.IsEnabled = false; try { await CopyResultAsync(); } finally { copy.IsEnabled = true; } };
        actions.Children.Add(copy);
        Add("播放视频", () => Process.Start(new ProcessStartInfo(result.Path) { UseShellExecute = true }));
        Add("另存为", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "MP4 视频|*.mp4", FileName = Path.GetFileName(result.Path), AddExtension = true, DefaultExt = ".mp4" };
            if (dialog.ShowDialog(this) == true && !string.Equals(Path.GetFullPath(dialog.FileName), result.Path, StringComparison.OrdinalIgnoreCase))
            { File.Copy(result.Path, dialog.FileName, overwrite: true); _status.Text = "视频已另存到：" + dialog.FileName; }
        });
        Add("打开文件夹", () => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", result.Path }, UseShellExecute = true }));
        Add("关闭", Close);
        _content.Children.Add(actions);
    }

    private async Task CopyResultAsync()
    {
        var result = _result!;
        var details = $"{result.Duration.TotalSeconds:F1} 秒 · {result.Frames} 帧 · {result.Bytes / 1048576d:F1} MB";
        if (result.StopReason.Length > 0) details += " · " + result.StopReason;
        if (result.AudioNote.Length > 0) details += "\n" + result.AudioNote;
        try
        {
            await GifClipboard.CopyAsync(result.Path, "视频");
            _status.Text = details + "\n已复制视频，可在聊天窗口按 Ctrl+V 粘贴为文件。";
        }
        catch (Exception ex) { _status.Text = details + "\n视频已保存，复制未完成：" + ex.Message; }
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
