using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Drawing = System.Drawing;

namespace ScreenNote;

internal sealed record RecordingOutcome(RecordMode Mode, string Path, TimeSpan Duration, int Frames, long Bytes, string StopReason);

public partial class RecordingWindow : Window
{
    // WDA_EXCLUDEFROMCAPTURE keeps the control bar out of the frames on Windows 10 2004 and newer.
    private const uint CaptureExcluded = 0x11;
    private const uint CaptureMonitored = 0x01;
    private const int CountdownSeconds = 3;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);

    private readonly RecordOptions _options;
    private readonly Drawing.Rectangle _region;
    private readonly string _dataDirectory;
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _ticker;
    private bool _recording;
    private bool _discard;
    private bool _closed;
    private TimeSpan _elapsed;

    internal RecordingOutcome? Outcome { get; private set; }

    internal RecordingWindow(RecordOptions options, Drawing.Rectangle region, string dataDirectory)
    {
        InitializeComponent();
        _options = options;
        _region = region;
        _dataDirectory = dataDirectory;
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _ticker.Tick += (_, _) => UpdateClock();
        HeadingText.Text = CountdownSeconds + " 秒后开始录制";
        BigText.Text = CountdownSeconds.ToString();
        RegionText.Text = "区域 " + region.Width + " × " + region.Height + " · " + options.Summary();
        Loaded += Window_Loaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        if (!SetWindowDisplayAffinity(handle, CaptureExcluded) && SetWindowDisplayAffinity(handle, CaptureMonitored))
        {
            FootNote.Text = "当前系统版本无法隐藏控制条，请把它拖到录制区域外";
        }
    }

    private void PlaceNearRegion()
    {
        var left = _region.Right - ActualWidth - 20;
        var top = _region.Bottom - ActualHeight - 20;
        Left = Math.Max(_region.Left + 8, left);
        Top = Math.Max(_region.Top + 8, top);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PlaceNearRegion();
        for (var remaining = CountdownSeconds; remaining > 0; remaining--)
        {
            if (_closed) return;
            BigText.Text = remaining.ToString();
            await Task.Delay(1000);
            if (_closed) return;
        }
        await RecordAsync();
    }

    private async Task RecordAsync()
    {
        _recording = true;
        _discard = false;
        _elapsed = TimeSpan.Zero;
        LiveDot.Visibility = Visibility.Visible;
        HeadingText.Text = _options.IsVideo ? "正在录制视频" : "正在录制 GIF";
        Progress.Value = 0;
        StatusText.Text = "按 Esc 或「停止并保存」结束录制，控制条不会出现在画面里。";
        PrimaryButton.Content = "停止并保存";
        CancelButton.Content = "放弃";
        _ticker.Start();

        var hud = new Progress<(TimeSpan Elapsed, int Frames, long Bytes)>(value =>
        {
            _elapsed = value.Elapsed;
            UpdateClock();
        });

        try
        {
            if (_options.IsVideo)
            {
                var result = await VideoRecorder.RecordAsync(_region, _options, _dataDirectory, _stop.Token,
                    new Progress<VideoRecordingProgress>(p => ((IProgress<(TimeSpan, int, long)>)hud).Report((p.Elapsed, p.Frames, p.Bytes))));
                Outcome = new RecordingOutcome(RecordMode.Video, result.Path, result.Duration, result.Frames, result.Bytes, result.StopReason);
            }
            else
            {
                var result = await GifRecorder.RecordAsync(_region, _options, _dataDirectory, _stop.Token,
                    new Progress<GifRecordingProgress>(p => ((IProgress<(TimeSpan, int, long)>)hud).Report((p.Elapsed, p.Frames, p.Bytes))));
                Outcome = new RecordingOutcome(RecordMode.Gif, result.Path, result.Duration, result.Frames, result.Bytes, result.StopReason);
            }
            if (_discard && Outcome != null)
            {
                TryDelete(Outcome.Path);
                Outcome = null;
            }
            Close();
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the first frame was captured: nothing was written.
            Close();
        }
        catch (Exception error)
        {
            _recording = false;
            _ticker.Stop();
            LiveDot.Visibility = Visibility.Collapsed;
            HeadingText.Text = _options.IsVideo ? "视频录制失败" : "GIF 录制失败";
            BigText.Text = "—";
            Progress.Value = 0;
            StatusText.Text = error.Message + "\n可换一个区域重试；若系统缺少媒体组件，请改用 GIF。";
            PrimaryButton.Content = "关闭";
            CancelButton.Content = "取消";
            PrimaryButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
    }

    private void UpdateClock()
    {
        var total = (int)_elapsed.TotalSeconds;
        BigText.Text = total / 60 + ":" + (total % 60).ToString("00");
        Progress.Value = Math.Min(1, _elapsed.TotalSeconds / Math.Max(1, _options.MaximumDuration.TotalSeconds));
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (!_recording)
        {
            Close();
            return;
        }
        _stop.Cancel();
        PrimaryButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        PrimaryButton.Content = "正在保存…";
        HeadingText.Text = _options.IsVideo ? "正在保存视频…" : "正在保存 GIF…";
        StatusText.Text = "正在把录到的画面写进文件，请稍候。";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_recording)
        {
            Close();
            return;
        }
        _discard = true;
        _stop.Cancel();
        PrimaryButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在放弃本次录制…";
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel_Click(this, new RoutedEventArgs());
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _ticker.Stop();
        _stop.Cancel();
        base.OnClosed(e);
    }
}