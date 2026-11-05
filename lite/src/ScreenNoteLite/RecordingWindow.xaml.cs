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

    private enum RecState { Ready, Recording, Paused, Saving }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);

    private readonly RecordOptions _options;
    private readonly Drawing.Rectangle _region;
    private readonly string _dataDirectory;
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _ticker;
    private RecState _state = RecState.Ready;
    private volatile bool _paused;
    private bool _discard;
    private bool _recordingActive;
    private TimeSpan _elapsed;

    internal RecordingOutcome? Outcome { get; private set; }

    internal RecordingWindow(RecordOptions options, Drawing.Rectangle region, string dataDirectory)
    {
        InitializeComponent();
        StudioUi.Label(StartButton, "play", "开始录制");
        StudioUi.Label(CancelButton, "close", "放弃");
        _options = options;
        _region = region;
        _dataDirectory = dataDirectory;
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _ticker.Tick += (_, _) => UpdateClock();
        RegionText.Text = region.Width + " × " + region.Height + " · " + options.Format;
        ApplyState(RecState.Ready);
        Loaded += Window_Loaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        if (!SetWindowDisplayAffinity(handle, CaptureExcluded) && SetWindowDisplayAffinity(handle, CaptureMonitored))
        {
            FootNote.Text = "当前系统版本无法隐藏控制条，请把它拖到录制区域外";
            FootNote.Visibility = Visibility.Visible;
        }
    }

    private void PlaceNearRegion()
    {
        var left = _region.Right - ActualWidth - 24;
        var top = _region.Bottom - ActualHeight - 24;
        Left = Math.Max(_region.Left + 8, left);
        Top = Math.Max(_region.Top + 8, top);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => PlaceNearRegion();

    private void ApplyState(RecState state)
    {
        _state = state;
        switch (state)
        {
            case RecState.Ready:
                HeadingText.Text = "准备录制";
                LiveDot.Visibility = Visibility.Collapsed;
                StartButton.Visibility = Visibility.Visible;
                PauseButton.Visibility = Visibility.Collapsed;
                PrimaryButton.IsEnabled = false;
                PrimaryButton.Visibility = Visibility.Collapsed;
                StudioUi.Label(PrimaryButton, "save", "停止并保存");
                ClockText.Text = "0:00";
                Progress.Value = 0;
                break;
            case RecState.Recording:
                HeadingText.Text = _options.IsVideo ? "正在录制视频" : "正在录制 GIF";
                LiveDot.Visibility = Visibility.Visible;
                LiveDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC4, 0x45, 0x3A));
                StartButton.Visibility = Visibility.Collapsed;
                PauseButton.Visibility = Visibility.Visible;
                StudioUi.Label(PauseButton, "pause", "暂停");
                PauseButton.IsEnabled = true;
                PrimaryButton.IsEnabled = true;
                PrimaryButton.Visibility = Visibility.Visible;
                StudioUi.Label(PrimaryButton, "save", "停止并保存");
                break;
            case RecState.Paused:
                HeadingText.Text = "已暂停";
                LiveDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x59, 0x65, 0xF2));
                StudioUi.Label(PauseButton, "play", "继续");
                PauseButton.IsEnabled = true;
                PrimaryButton.IsEnabled = true;
                break;
            case RecState.Saving:
                HeadingText.Text = _options.IsVideo ? "正在保存视频…" : "正在保存 GIF…";
                LiveDot.Visibility = Visibility.Collapsed;
                StartButton.IsEnabled = false;
                PauseButton.IsEnabled = false;
                PrimaryButton.IsEnabled = false;
                StudioUi.Label(PrimaryButton, "save", "正在保存…");
                break;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_state is not RecState.Ready and not RecState.Paused) return;
        await RecordAsync();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_state is not RecState.Recording and not RecState.Paused) return;
        _paused = !_paused;
        ApplyState(_paused ? RecState.Paused : RecState.Recording);
    }

    private async Task RecordAsync()
    {
        _discard = false;
        _paused = false;
        _elapsed = TimeSpan.Zero;

        string dataDirectory;
        try
        {
            var resolved = StorageLocationService.ResolveRecordingDirectory(_dataDirectory);
            dataDirectory = resolved.DataDirectory;
            if (resolved.UsedFallback)
            {
                FootNote.Text = "原资料文件夹不可写，本次录制保存在：" + dataDirectory;
                FootNote.Visibility = Visibility.Visible;
            }
        }
        catch (Exception error)
        {
            HeadingText.Text = "无法开始录制";
            FootNote.Text = error.Message;
            FootNote.Visibility = Visibility.Visible;
            StudioUi.Label(CancelButton, "close", "关闭");
            StartButton.Visibility = Visibility.Collapsed;
            PauseButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Visibility = Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            return;
        }

        ApplyState(RecState.Recording);
        _recordingActive = true;
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
                var result = await VideoRecorder.RecordAsync(_region, _options, dataDirectory, _stop.Token,
                    new Progress<VideoRecordingProgress>(p => ((IProgress<(TimeSpan, int, long)>)hud).Report((p.Elapsed, p.Frames, p.Bytes))),
                    () => _paused);
                Outcome = new RecordingOutcome(RecordMode.Video, result.Path, result.Duration, result.Frames, result.Bytes, result.StopReason);
            }
            else
            {
                var result = await GifRecorder.RecordAsync(_region, _options, dataDirectory, _stop.Token,
                    new Progress<GifRecordingProgress>(p => ((IProgress<(TimeSpan, int, long)>)hud).Report((p.Elapsed, p.Frames, p.Bytes))),
                    () => _paused);
                Outcome = new RecordingOutcome(RecordMode.Gif, result.Path, result.Duration, result.Frames, result.Bytes, result.StopReason);
            }
            if (_discard && Outcome != null)
            {
                TryDelete(Outcome.Path);
                Outcome = null;
            }
            _recordingActive = false;
            Close();
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the first frame was captured: nothing was written.
            _recordingActive = false;
            Close();
        }
        catch (Exception error)
        {
            _recordingActive = false;
            _ticker.Stop();
            ApplyState(RecState.Ready);
            HeadingText.Text = _options.IsVideo ? "视频录制失败" : "GIF 录制失败";
            FootNote.Text = error.Message;
            FootNote.Visibility = Visibility.Visible;
            StudioUi.Label(CancelButton, "close", "关闭");
            StartButton.Visibility = Visibility.Collapsed;
            PauseButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Visibility = Visibility.Collapsed;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
    }

    private void UpdateClock()
    {
        var total = (int)_elapsed.TotalSeconds;
        ClockText.Text = total / 60 + ":" + (total % 60).ToString("00");
        Progress.Value = Math.Min(1, _elapsed.TotalSeconds / Math.Max(1, _options.MaximumDuration.TotalSeconds));
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_state != RecState.Recording && _state != RecState.Paused) { Close(); return; }
        _paused = false;
        _stop.Cancel();
        ApplyState(RecState.Saving);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_state != RecState.Recording && _state != RecState.Paused)
        {
            _discard = true;
            Close();
            return;
        }
        _paused = false;
        _discard = true;
        _stop.Cancel();
        ApplyState(RecState.Saving);
        HeadingText.Text = "正在放弃…";
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
        else if (e.Key == Key.Space && _state is RecState.Recording or RecState.Paused)
        {
            e.Handled = true;
            Pause_Click(this, new RoutedEventArgs());
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _ticker.Stop();
        _stop.Cancel();
        base.OnClosed(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_recordingActive)
        {
            e.Cancel = true;
            if (_state is RecState.Recording or RecState.Paused) Cancel_Click(this, new RoutedEventArgs());
        }
        base.OnClosing(e);
    }
}
