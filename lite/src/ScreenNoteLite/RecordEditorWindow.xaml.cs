using System;
using System.Collections.Generic;
using System.IO;
using IOPath = System.IO.Path;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ScreenNote;

public partial class RecordEditorWindow : Window
{
    private readonly RecordSource _source;
    private readonly List<Rectangle> _shades = new();
    private readonly List<Rectangle> _handles = new();
    private Rectangle _border = null!;
    private Int32Rect? _crop;
    private Point? _dragFrom;
    private bool _dragging;
    private double _scale = 1;
    private Rectangle _trimSelect = null!;
    private Rectangle _trimLeftBar = null!;
    private Rectangle _trimRightBar = null!;
    private Rectangle _trimShadeLeft = null!;
    private Rectangle _trimShadeRight = null!;
    private int _trimDrag;
    private double _trimGrabX;
    private TimeSpan _trimStartAtGrab;
    private TimeSpan _trimEndAtGrab;
    private TimeSpan _start;
    private TimeSpan _end;
    private bool _ready;
    private bool _closed;
    private bool _exporting;

    internal string? ResultPath { get; private set; }
    internal bool CopiedToClipboard { get; private set; }

    internal RecordEditorWindow(string path)
    {
        InitializeComponent();
        _source = RecordSource.Open(path);
        _start = TimeSpan.Zero;
        _end = _source.Duration;
        BuildOverlay();
        Subtitle.Text = IOPath.GetFileName(path) + " · " + Clock(_source.Duration) + " · " + _source.Size.Width + " × " + _source.Size.Height;
        PreviewImage.Source = _source.Poster;
        if (_source.Mode == RecordMode.Gif) GifChoice.IsChecked = true; else VideoChoice.IsChecked = true;
        BuildTimeline();
        StartSlider.Maximum = Math.Max(0.1, _source.Duration.TotalSeconds);
        EndSlider.Maximum = StartSlider.Maximum;
        EndSlider.Value = StartSlider.Maximum;
        _ready = true;
        UpdateTrim();
        RefreshFormat();
        Layout();
        Loaded += async (_, _) => { InitializePlayback(); await LoadFilmstripAsync(); };
        Closing += (_, e) => e.Cancel |= _exporting;
    }

    private async Task LoadFilmstripAsync()
    {
        try
        {
            var frames = await _source.LoadThumbnailsAsync();
            if (_closed) return;
            foreach (var thumbnail in frames)
            {
                FilmStrip.Children.Add(new Border
                {
                    Width = 66,
                    Height = 64,
                    Margin = new Thickness(0),
                    BorderThickness = new Thickness(0, 0, 1, 0),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                    ClipToBounds = true,
                    Background = new SolidColorBrush(Color.FromRgb(0xD2, 0xDC, 0xEF)),
                    Child = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill },
                });
            }
            LayoutFilmstrip();
        }
        catch { /* 缩略图加载失败不影响剪辑 */ }
    }

    private void BuildOverlay()
    {
        for (var index = 0; index < 4; index++)
        {
            var shade = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x73, 0x27, 0x33, 0x50)), IsHitTestVisible = false };
            _shades.Add(shade);
            Overlay.Children.Add(shade);
        }
        _border = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        Overlay.Children.Add(_border);
        for (var index = 0; index < 4; index++)
        {
            var handle = new Rectangle
            {
                Width = 11,
                Height = 11,
                RadiusX = 3,
                RadiusY = 3,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0xD8, 0xDF, 0xEF)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            _handles.Add(handle);
            Overlay.Children.Add(handle);
        }
    }

    private void Layout()
    {
        if (_source?.Poster == null || _border == null) return;
        var stage = Stage;
        var width = Math.Max(1, stage.ActualWidth);
        var height = Math.Max(1, stage.ActualHeight);
        _scale = Math.Min(width / _source.Size.Width, height / _source.Size.Height);
        if (_scale <= 0) _scale = 1;
        var displayWidth = _source.Size.Width * _scale;
        var displayHeight = _source.Size.Height * _scale;
        PreviewImage.Width = displayWidth;
        PreviewImage.Height = displayHeight;
        VideoPreview.Width = displayWidth;
        VideoPreview.Height = displayHeight;
        VideoPreview.HorizontalAlignment = HorizontalAlignment.Center;
        VideoPreview.VerticalAlignment = VerticalAlignment.Center;
        Overlay.Width = displayWidth;
        Overlay.Height = displayHeight;
        PreviewImage.HorizontalAlignment = HorizontalAlignment.Center;
        PreviewImage.VerticalAlignment = VerticalAlignment.Center;
        Overlay.HorizontalAlignment = HorizontalAlignment.Center;
        Overlay.VerticalAlignment = VerticalAlignment.Center;
        RenderCrop();
    }

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Stage.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, Stage.ActualWidth), Math.Max(0, Stage.ActualHeight)), 21, 21);
        Layout();
    }

    private Int32Rect EffectiveCrop()
    {
        if (_crop is { Width: > 1, Height: > 1 } crop)
        {
            var x = Math.Clamp(crop.X, 0, _source.Size.Width - 2);
            var y = Math.Clamp(crop.Y, 0, _source.Size.Height - 2);
            return new Int32Rect(x, y,
                Math.Clamp(crop.Width, 2, _source.Size.Width - x),
                Math.Clamp(crop.Height, 2, _source.Size.Height - y));
        }
        return new Int32Rect(0, 0, _source.Size.Width, _source.Size.Height);
    }

    private void RenderCrop()
    {
        var crop = EffectiveCrop();
        var left = crop.X * _scale;
        var top = crop.Y * _scale;
        var width = crop.Width * _scale;
        var height = crop.Height * _scale;
        var totalWidth = _source.Size.Width * _scale;
        var totalHeight = _source.Size.Height * _scale;

        Place(_shades[0], 0, 0, totalWidth, top);
        Place(_shades[1], 0, top + height, totalWidth, Math.Max(0, totalHeight - top - height));
        Place(_shades[2], 0, top, left, height);
        Place(_shades[3], left + width, top, Math.Max(0, totalWidth - left - width), height);

        Place(_border, left, top, width, height);
        Place(_handles[0], left - 5, top - 5, 11, 11);
        Place(_handles[1], left + width - 6, top - 5, 11, 11);
        Place(_handles[2], left - 5, top + height - 6, 11, 11);
        Place(_handles[3], left + width - 6, top + height - 6, 11, 11);

        CropText.Text = crop.Width == _source.Size.Width && crop.Height == _source.Size.Height
            ? "原始 " + crop.Width + " × " + crop.Height
            : crop.Width + " × " + crop.Height;
        UpdateEstimate();
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_ready) return;
        _dragFrom = e.GetPosition(Overlay);
        BeginCropDrag(_dragFrom.Value);
        _dragging = true;
        Overlay.CaptureMouse();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _dragFrom is not Point start) return;
        var current = e.GetPosition(Overlay);
        _crop = CropFromDrag(start, current);
        RenderCrop();
    }

    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Overlay.ReleaseMouseCapture();
        var crop = EffectiveCrop();
        if (crop.Width < 8 || crop.Height < 8) _crop = null;
        else _crop = crop;
        RenderCrop();
    }

    private Int32Rect ToSourceRect(Point a, Point b)
    {
        var x1 = Math.Clamp(a.X / _scale, 0, _source.Size.Width);
        var y1 = Math.Clamp(a.Y / _scale, 0, _source.Size.Height);
        var x2 = Math.Clamp(b.X / _scale, 0, _source.Size.Width);
        var y2 = Math.Clamp(b.Y / _scale, 0, _source.Size.Height);
        if (_cropRatio is double ratio)
        {
            var signX = x2 >= x1 ? 1 : -1;
            var signY = y2 >= y1 ? 1 : -1;
            var width = Math.Abs(x2 - x1);
            var height = Math.Abs(y2 - y1);
            width = Math.Max(width, height * ratio);
            var maxWidth = signX > 0 ? _source.Size.Width - x1 : x1;
            var maxHeight = signY > 0 ? _source.Size.Height - y1 : y1;
            width = Math.Min(width, Math.Min(maxWidth, maxHeight * ratio));
            x2 = x1 + signX * width;
            y2 = y1 + signY * width / ratio;
        }
        var x = (int)Math.Round(Math.Min(x1, x2));
        var y = (int)Math.Round(Math.Min(y1, y2));
        return new Int32Rect(x, y, Math.Max(1, (int)Math.Round(Math.Abs(x2 - x1))), Math.Max(1, (int)Math.Round(Math.Abs(y2 - y1))));
    }

    private void ResetCrop_Click(object sender, RoutedEventArgs e)
    {
        _cropRatio = null;
        RatioOriginal.IsChecked = true;
        _crop = null;
        RenderCrop();
        StatusText.Text = "在画面里拖拽框出要保留的区域。";
    }

    private void ClearCrop_Click(object sender, RoutedEventArgs e)
    {
        _cropRatio = null;
        RatioOriginal.IsChecked = true;
        _crop = null;
        RenderCrop();
        StatusText.Text = "已恢复为整个画面。";
    }



    private void Trim_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        var start = TimeSpan.FromSeconds(StartSlider.Value);
        var end = TimeSpan.FromSeconds(EndSlider.Value);
        if (end - start < TimeSpan.FromMilliseconds(200))
        {
            if (sender == StartSlider) end = start + TimeSpan.FromMilliseconds(200);
            else start = end - TimeSpan.FromMilliseconds(200);
            if (start < TimeSpan.Zero) { start = TimeSpan.Zero; end = TimeSpan.FromMilliseconds(200); }
            if (end > _source.Duration) { end = _source.Duration; start = end - TimeSpan.FromMilliseconds(200); }
        }
        _start = _source.Clamp(start);
        _end = _source.Clamp(end);
        UpdateTrim();
        PausePreview();
        SeekPreview(sender == EndSlider ? _end : _start);
    }

    private void UpdateTrim()
    {
        StartText.Text = PreviewClock(_start);
        EndText.Text = PreviewClock(_end);
        KeepText.Text = (_end - _start).TotalSeconds.ToString("0.0") + " 秒";
        LayoutTimeline();
        UpdateEstimate();
        UpdatePreviewRange();
    }

    // The film strip is a real timeline: drag either end to trim, drag the middle to shift the window.
    private void BuildTimeline()
    {
        _trimShadeLeft = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0xAC, 0xE9, 0xEF, 0xFA)), IsHitTestVisible = false };
        _trimShadeRight = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0xAC, 0xE9, 0xEF, 0xFA)), IsHitTestVisible = false };
        _trimSelect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x59, 0x65, 0xF2)),
            StrokeThickness = 3,
            RadiusX = 9,
            RadiusY = 9,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        _trimLeftBar = MakeHandle();
        _trimRightBar = MakeHandle();
        foreach (var element in new FrameworkElement[] { _trimShadeLeft, _trimShadeRight, _trimSelect, _trimLeftBar, _trimRightBar })
            TrimCanvas.Children.Add(element);
        for (var index = 0; index < 4; index++)
        {
            var mark = new Rectangle { Fill = Brushes.White, RadiusX = 1, RadiusY = 1, IsHitTestVisible = false };
            _trimGripMarks.Add(mark);
            TrimCanvas.Children.Add(mark);
        }
        _timelinePlayhead = new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(0x59, 0x65, 0xF2)), IsHitTestVisible = false };
        _timelineHead = new System.Windows.Shapes.Path { Fill = _timelinePlayhead.Fill, Data = Geometry.Parse("M0,0 L10,0 5,6 Z"), IsHitTestVisible = false };
        TrimCanvas.Children.Add(_timelinePlayhead);
        TrimCanvas.Children.Add(_timelineHead);
    }

    private static Rectangle MakeHandle() => new()
    {
        Width = 17,
        Fill = new SolidColorBrush(Color.FromRgb(0x59, 0x65, 0xF2)),
        RadiusX = 7,
        RadiusY = 7,
        Cursor = Cursors.SizeWE,
        IsHitTestVisible = false,
    };

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        LayoutTimeline();
        LayoutFilmstrip();
    }

    private void LayoutTimeline()
    {
        if (_trimSelect == null) return;
        var width = TrimCanvas.ActualWidth;
        var height = TrimCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var total = Math.Max(1, _source.Duration.TotalMilliseconds);
        var left = Math.Clamp(_start.TotalMilliseconds / total, 0, 1) * width;
        var right = Math.Clamp(_end.TotalMilliseconds / total, 0, 1) * width;
        if (right - left < 4) right = left + 4;
        Place(_trimShadeLeft, 0, 0, left, height);
        Place(_trimShadeRight, right, 0, Math.Max(0, width - right), height);
        Place(_trimSelect, left, 0, right - left, height);
        var leftHandle = Math.Clamp(left - 8.5, 0, Math.Max(0, width - 17));
        var rightHandle = Math.Clamp(right - 8.5, 0, Math.Max(0, width - 17));
        Place(_trimLeftBar, leftHandle, 0, 17, height);
        Place(_trimRightBar, rightHandle, 0, 17, height);
        for (var index = 0; index < _trimGripMarks.Count; index++)
            Place(_trimGripMarks[index], (index < 2 ? leftHandle : rightHandle) + 5 + index % 2 * 5, height / 2 - 8, 2, 16);
        UpdateTimelineCursor();
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private void Trim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_ready) return;
        var width = TrimCanvas.ActualWidth;
        if (width <= 4) return;
        var x = e.GetPosition(TrimCanvas).X;
        var total = Math.Max(1, _source.Duration.TotalMilliseconds);
        var left = _start.TotalMilliseconds / total * width;
        var right = _end.TotalMilliseconds / total * width;
        _trimGrabX = x;
        _trimStartAtGrab = _start;
        _trimEndAtGrab = _end;
        PausePreview();
        _trimDrag = Math.Abs(x - left) <= 14 ? 1
            : Math.Abs(x - right) <= 14 ? 2
            : x > left && x < right ? 3 : 0;
        if (_trimDrag == 0)
        {
            var at = _source.Clamp(TimeSpan.FromMilliseconds(Math.Clamp(x / width, 0, 1) * total));
            if (at < _start) { _start = at; _trimDrag = 1; }
            else { _end = at; _trimDrag = 2; }
            SyncSliders();
            UpdateTrim();
            _trimStartAtGrab = _start;
            _trimEndAtGrab = _end;
            _trimGrabX = x;
        }
        TrimCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void Trim_MouseMove(object sender, MouseEventArgs e)
    {
        if (_trimDrag == 0) return;
        var width = TrimCanvas.ActualWidth;
        if (width <= 4) return;
        var total = Math.Max(1, _source.Duration.TotalMilliseconds);
        var delta = TimeSpan.FromMilliseconds((e.GetPosition(TrimCanvas).X - _trimGrabX) / width * total);
        var minimum = TimeSpan.FromMilliseconds(200);
        if (_trimDrag == 1)
        {
            var start = _trimStartAtGrab + delta;
            _start = _source.Clamp(start);
            if (_end - _start < minimum) _start = _end - minimum;
            if (_start < TimeSpan.Zero) _start = TimeSpan.Zero;
        }
        else if (_trimDrag == 2)
        {
            _end = _source.Clamp(_trimEndAtGrab + delta);
            if (_end - _start < minimum) _end = _start + minimum;
            if (_end > _source.Duration) _end = _source.Duration;
        }
        else
        {
            var length = _trimEndAtGrab - _trimStartAtGrab;
            var start = _trimStartAtGrab + delta;
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            if (start + length > _source.Duration) start = _source.Duration - length;
            _start = start;
            _end = start + length;
        }
        SyncSliders();
        UpdateTrim();
        SeekPreview(_trimDrag == 2 ? _end : _start);
    }

    private void Trim_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_trimDrag == 0) return;
        _trimDrag = 0;
        TrimCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SyncSliders()
    {
        _ready = false;
        StartSlider.Value = Math.Clamp(_start.TotalSeconds, 0, StartSlider.Maximum);
        EndSlider.Value = Math.Clamp(_end.TotalSeconds, 0, EndSlider.Maximum);
        _ready = true;
    }

    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        RefreshFormat();
    }

    private void RefreshFormat()
    {
        var gif = GifChoice.IsChecked == true;
        GifPanel.Visibility = gif ? Visibility.Visible : Visibility.Collapsed;
        VideoPanel.Visibility = gif ? Visibility.Collapsed : Visibility.Visible;
        UpdateEstimate();
    }

    private void Settings_Changed(object sender, SelectionChangedEventArgs e) => UpdateEstimate();

    private static int IntTag(ComboBox box, int fallback) =>
        box.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var value) ? value : fallback;

    private int SelectedSpeed()
    {
        var speed = IntTag(SpeedChoice, 1);
        return speed is 2 or 4 or 8 ? speed : 1;
    }

    private void UpdateEstimate()
    {
        if (!_ready) return;
        var crop = EffectiveCrop();
        var gif = GifChoice.IsChecked == true;
        var speed = SelectedSpeed();
        var duration = (_end - _start).TotalSeconds / speed;
        var timing = speed + "× · " + duration.ToString("0.0#") + " 秒";
        if (gif)
        {
            var edge = Math.Min(1440, Math.Max(crop.Width, crop.Height));
            var size = Fit(crop, edge);
            var target = IntTag(GifTarget, 10 * 1024 * 1024);
            var safe = GifSafeSeconds(target, IntTag(GifFps, 15));
            var advice = target > 0 && duration > safe ? " · 超过约 " + safe + " 秒，压缩可能失败" : "";
            EstimateText.Text = "GIF " + size.Width + " × " + size.Height + " · " + IntTag(GifFps, 15) + " 帧/秒 · "
                + timing + " · " + (target > 0 ? "自动压到 " + target / (1024 * 1024) + " MB 内" + advice : "不压缩");
        }
        else
        {
            EstimateText.Text = "MP4 " + crop.Width + " × " + crop.Height + " · " + IntTag(VideoFps, 30)
                + " 帧/秒 · " + timing + " · H.264";
        }
    }

    private static System.Drawing.Size Fit(Int32Rect crop, int maximumEdge)
    {
        var scale = Math.Min(1.0, (double)maximumEdge / Math.Max(crop.Width, crop.Height));
        var width = Math.Max(2, (int)Math.Round(crop.Width * scale));
        var height = Math.Max(2, (int)Math.Round(crop.Height * scale));
        return new System.Drawing.Size(width & ~1, height & ~1);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var gif = GifChoice.IsChecked == true;
        var folder = IOPath.GetDirectoryName(_source.Path) ?? App.DataDirectory;
        var path = IOPath.Combine(folder, "拾屏-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-"
            + Guid.NewGuid().ToString("N")[..8] + (gif ? ".gif" : ".mp4"));
        if (!await ExportToAsync(path)) return;
        DialogResult = true;
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var gif = GifChoice.IsChecked == true;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出录制",
            Filter = gif ? "GIF 动图 (*.gif)|*.gif" : "MP4 视频 (*.mp4)|*.mp4",
            FileName = "拾屏-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (gif ? ".gif" : ".mp4"),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!await ExportToAsync(dialog.FileName)) return;
        DialogResult = true;
    }

    private async Task<bool> ExportToAsync(string path)
    {
        PausePreview();
        PlaybackControls.IsEnabled = false;
        SetCropEditingEnabled(false);
        var gif = GifChoice.IsChecked == true;
        var start = _start;
        var end = _end;
        var crop = EffectiveCrop();
        var fps = gif ? IntTag(GifFps, 15) : IntTag(VideoFps, 30);
        var target = gif ? IntTag(GifTarget, 10 * 1024 * 1024) : 0;
        var speed = SelectedSpeed();

        _exporting = true;
        SaveButton.IsEnabled = false;
        SaveAsButton.IsEnabled = false;
        GifChoice.IsEnabled = false;
        VideoChoice.IsEnabled = false;
        StatusText.Text = "正在导出…";
        var progress = new Progress<string>(text => StatusText.Text = text);
        long size;
        try
        {
            size = await Task.Run(() => gif
                ? ExportGif(_source.Path, path, start, end, crop, fps, target, speed, progress)
                : ExportVideo(_source.Path, path, start, end, crop, fps, speed, progress));
        }
        catch (Exception error)
        {
            StatusText.Text = "导出失败：" + error.Message;
            MessageBox.Show(this, "无法保存文件。\n\n" + path + "\n\n" + error.Message,
                "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            _exporting = false;
            PlaybackControls.IsEnabled = true;
            SetCropEditingEnabled(true);
            SaveButton.IsEnabled = true;
            SaveAsButton.IsEnabled = true;
            GifChoice.IsEnabled = true;
            VideoChoice.IsEnabled = true;
        }

        ResultPath = path;
        CopiedToClipboard = TryCopyFile(path);
        var detail = CopiedToClipboard ? "文件已保存并复制到剪贴板，可直接粘贴。" : "文件已保存，复制到剪贴板失败。";
        StatusText.Text = detail;
        MessageBox.Show(this, detail + "\n\n" + path + "\n" + (size / 1024.0 / 1024.0).ToString("0.0") + " MB",
            "导出成功", MessageBoxButton.OK, CopiedToClipboard ? MessageBoxImage.Information : MessageBoxImage.Warning);
        return true;
    }

    private static bool TryCopyFile(string path)
    {
        try
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.FileDrop, new[] { path }), true);
            return true;
        }
        catch { return false; }
    }

    private static long ExportGif(string sourcePath, string path, TimeSpan start, TimeSpan end, Int32Rect crop,
        int fps, long targetBytes, int speed, IProgress<string>? progress)
    {
        ValidateSpeed(speed);
        var steps = BuildGifSteps(fps);
        var outputSeconds = Math.Max(0.001, ToOutputTicks((end - start).Ticks, speed) / (double)TimeSpan.TicksPerSecond);
        (int Edge, int Colors, int Fps)? measured = null;
        var measuredBytes = 0L;
        var shown = 0;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            // Skip past steps that would still predict over the target; the last step always runs as the floor.
            if (measured is { } last && targetBytes > 0 && index < steps.Count - 1
                && PredictGifBytes(last, measuredBytes, step, crop) > targetBytes) continue;
            var size = Fit(crop, step.Edge);
            var partial = path + ".partial";
            try
            {
                shown++;
                var speedText = speed > 1 ? speed + "×，" : "";
                var fpsText = step.Fps < fps ? " · " + step.Fps + " 帧/秒" : "";
                progress?.Report(targetBytes > 0
                    ? "正在导出 GIF（" + speedText + "第 " + shown + " 档：" + size.Width + " × " + size.Height + fpsText + "）…"
                    : speed > 1 ? "正在导出 GIF（" + speed + "×）…" : "正在导出 GIF…");
                var length = WriteGif(sourcePath, partial, start, end, crop, step, speed, targetBytes > 0 ? 8 : 0);
                if (targetBytes <= 0 || length <= targetBytes)
                {
                    File.Move(partial, path, true);
                    return length;
                }
                measured = step;
                measuredBytes = length;
                File.Delete(partial);
            }
            catch
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                throw;
            }
        }
        if (targetBytes <= 0 || measured is not { } floor) throw new InvalidOperationException("GIF 导出失败。");
        var finalSize = Fit(crop, floor.Edge);
        if (outputSeconds < 2)
            throw new InvalidOperationException("已自动降到最低档（" + finalSize.Width + " × " + finalSize.Height
                + " · " + floor.Fps + " 帧/秒）仍超过目标体积。建议改选更大的体积目标（如公众号 20 MB）或选择不压缩。");
        var perSecond = Math.Max(1.0, measuredBytes / outputSeconds);
        var suggested = Math.Max(1, (int)Math.Round(targetBytes / perSecond));
        throw new InvalidOperationException("已自动降到最低档（" + finalSize.Width + " × " + finalSize.Height
            + " · " + floor.Fps + " 帧/秒）仍超过目标体积。按当前画面复杂度，建议把保留时长缩短到约 " + suggested
            + " 秒以内，或改选更大的体积目标 / 不压缩。");
    }

    // Quality ladder: cut frame rate before resolution so text stays readable while bytes drop.
    private static List<(int Edge, int Colors, int Fps)> BuildGifSteps(int fps)
    {
        fps = Math.Max(1, fps);
        var rates = new List<int> { fps };
        foreach (var scale in new[] { 2.0 / 3, 0.5, 1.0 / 3 })
        {
            var reduced = Math.Max(4, (int)Math.Round(fps * scale));
            if (reduced < rates[^1]) rates.Add(reduced);
        }
        var qualities = new (int Edge, int Colors)[] { (1440, 256), (1080, 160), (840, 128), (640, 96), (480, 64) };
        var steps = new List<(int Edge, int Colors, int Fps)>();
        var rateIndex = 0;
        for (var index = 0; index < qualities.Length; index++)
        {
            var (edge, colors) = qualities[index];
            steps.Add((edge, colors, rates[Math.Min(rateIndex, rates.Count - 1)]));
            if (index >= 1 && rateIndex < rates.Count - 1)
            {
                rateIndex++;
                steps.Add((edge, colors, rates[rateIndex]));
            }
        }
        return steps;
    }

    private static long PredictGifBytes((int Edge, int Colors, int Fps) measured, long measuredBytes,
        (int Edge, int Colors, int Fps) step, Int32Rect crop)
    {
        var measuredSize = Fit(crop, measured.Edge);
        var stepSize = Fit(crop, step.Edge);
        var measuredArea = (double)measuredSize.Width * measuredSize.Height;
        var stepArea = (double)stepSize.Width * stepSize.Height;
        if (measuredArea <= 0 || stepArea <= 0) return measuredBytes;
        var predicted = measuredBytes * Math.Pow(stepArea / measuredArea, 0.85) * step.Fps / Math.Max(1, measured.Fps);
        return Math.Max(1, (long)Math.Ceiling(predicted));
    }

    // One ladder attempt: delta-encoded frames so unchanged screen areas cost almost nothing.
    private static long WriteGif(string sourcePath, string partial, TimeSpan start, TimeSpan end, Int32Rect crop,
        (int Edge, int Colors, int Fps) step, int speed, int tolerance)
    {
        var size = Fit(crop, step.Edge);
        var stride = size.Width * 4;
        var fullPatch = new Int32Rect(0, 0, size.Width, size.Height);
        using (var source = RecordSource.Open(sourcePath))
        using (var stream = File.Create(partial))
        {
            using var writer = new AnimatedGifWriter(stream, size.Width, size.Height);
            var nextSlot = 0L;
            var interval = Math.Max(1, TimeSpan.TicksPerSecond / Math.Max(1, step.Fps) * speed);
            var total = ToOutputTicks((end - start).Ticks, speed);
            BitmapSource? pendingImage = null;
            var pendingPatch = fullPatch;
            var pendingPixels = new byte[stride * size.Height];
            var currentPixels = new byte[stride * size.Height];
            var hasPendingPixels = false;
            var writtenCentiseconds = 0;
            void FlushPending(long until)
            {
                if (pendingImage == null) return;
                var target = (int)Math.Round(until * 100.0 / TimeSpan.TicksPerSecond);
                var delay = Math.Max(1, target - writtenCentiseconds);
                writer.Append(new CroppedBitmap(pendingImage, pendingPatch), delay, step.Colors,
                    pendingPatch.X, pendingPatch.Y);
                writtenCentiseconds += delay;
            }
            source.Read(start, end, (frame, relative) =>
            {
                if (relative.Ticks < nextSlot) return true;
                nextSlot += interval;
                var outputTime = ToOutputTicks(relative.Ticks, speed);
                var current = Resize(Crop(frame, crop), size.Width, size.Height);
                current.CopyPixels(currentPixels, stride, 0);
                var difference = hasPendingPixels
                    ? Difference(pendingPixels, currentPixels, size.Width, size.Height, tolerance)
                    : fullPatch;
                if (hasPendingPixels && difference.IsEmpty) return true; // Identical frame: delay merges into the pending patch.
                FlushPending(outputTime);
                pendingImage = current;
                pendingPatch = difference;
                (pendingPixels, currentPixels) = (currentPixels, pendingPixels);
                hasPendingPixels = true;
                return true;
            });
            if (pendingImage == null) throw new InvalidOperationException("选定的时间段里没有可导出的画面。");
            FlushPending(total);
            writer.Dispose();
        }
        return new FileInfo(partial).Length;
    }

    // Bounding box of pixels that changed more than the tolerance; empty means the frame repeats the previous one.
    private static Int32Rect Difference(byte[] previous, byte[] current, int width, int height, int tolerance)
    {
        var left = width;
        var right = -1;
        var top = height;
        var bottom = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var index = row + x * 4;
                if (Math.Abs(previous[index] - current[index]) <= tolerance
                    && Math.Abs(previous[index + 1] - current[index + 1]) <= tolerance
                    && Math.Abs(previous[index + 2] - current[index + 2]) <= tolerance) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (top > y) top = y;
                bottom = y;
            }
        }
        return right < 0 ? Int32Rect.Empty : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    // Conservative "may fail" hint: ~350 KB/s of busy screen content after the ladder has done its work.
    private static int GifSafeSeconds(long targetBytes, int fps) => Math.Max(5,
        (int)Math.Round(targetBytes / 350_000.0 * 15.0 / Math.Max(1, fps)));

    private static long ExportVideo(string sourcePath, string path, TimeSpan start, TimeSpan end, Int32Rect crop,
        int fps, int speed, IProgress<string>? progress)
    {
        ValidateSpeed(speed);
        var even = new Int32Rect(crop.X & ~1, crop.Y & ~1, crop.Width & ~1, crop.Height & ~1);
        if (even.Width < 2 || even.Height < 2) throw new InvalidOperationException("裁剪区域太小，无法输出视频。");
        var partial = path + ".partial";
        try
        {
            progress?.Report("正在导出视频（" + (speed > 1 ? speed + "×，" : "") + even.Width + " × " + even.Height + "）…");
            using var source = RecordSource.Open(sourcePath);
            using var audio = Mp4AudioReader.OpenOptional(sourcePath);
            using var writer = new Mp4Writer(partial, even.Width, even.Height, Math.Max(1, fps), audio?.SampleRate ?? 0);
            using var audioFrames = audio?.Read(start, end).GetEnumerator();
            var hasAudio = audioFrames?.MoveNext() == true;
            void WriteAudioUntil(long until)
            {
                while (hasAudio && audioFrames!.Current.Time <= until)
                {
                    var chunk = AccelerateAudio(audioFrames.Current, audio!.SampleRate, speed);
                    writer.WriteAudioPcm(chunk.Pcm, chunk.Time, chunk.Duration);
                    hasAudio = audioFrames.MoveNext();
                }
            }
            BitmapSource? pending = null;
            var pendingTime = 0L;
            var total = (end - start).Ticks;
            var nextSlot = 0L;
            var interval = Math.Max(1, TimeSpan.TicksPerSecond / Math.Max(1, fps) * speed);
            source.Read(start, end, (frame, relative) =>
            {
                if (relative.Ticks < nextSlot) return true;
                nextSlot += interval;
                var frameTime = pending == null ? 0 : relative.Ticks;
                if (pending != null && relative.Ticks > pendingTime)
                {
                    var from = ToOutputTicks(pendingTime, speed);
                    var to = ToOutputTicks(frameTime, speed);
                    writer.WriteVideoFrame(pending, from, Math.Max(1, to - from));
                }
                pending = Crop(frame, even);
                pendingTime = frameTime;
                WriteAudioUntil(pendingTime);
                return true;
            });
            if (pending == null) throw new InvalidOperationException("选定的时间段里没有可导出的画面。");
            var pendingOutputTime = ToOutputTicks(pendingTime, speed);
            writer.WriteVideoFrame(pending, pendingOutputTime, Math.Max(1, ToOutputTicks(total, speed) - pendingOutputTime));
            WriteAudioUntil(total);
            writer.Complete();
            writer.Dispose();
            var length = new FileInfo(partial).Length;
            File.Move(partial, path, true);
            return length;
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
    }

    private static void ValidateSpeed(int speed)
    {
        if (speed is not (1 or 2 or 4 or 8))
            throw new ArgumentOutOfRangeException(nameof(speed), "导出倍速仅支持 1×、2×、4× 和 8×。");
    }

    private static long ToOutputTicks(long sourceTicks, int speed) => (sourceTicks + speed / 2) / speed;

    private static AudioChunk AccelerateAudio(AudioChunk chunk, int sampleRate, int speed)
    {
        if (speed == 1) return chunk;
        // Resample both channels on the source timeline so chunk rounding cannot accumulate A/V drift.
        var inputFrames = chunk.Pcm.Length / 4;
        var sourceStart = (long)Math.Round(chunk.Time * (double)sampleRate / TimeSpan.TicksPerSecond);
        var outputStart = (sourceStart + speed - 1) / speed;
        var outputEnd = (sourceStart + inputFrames + speed - 1) / speed;
        var outputFrames = (int)Math.Max(0, outputEnd - outputStart);
        var pcm = new byte[outputFrames * 4];
        for (var index = 0; index < outputFrames; index++)
        {
            var sourceIndex = (outputStart + index) * (long)speed - sourceStart;
            Buffer.BlockCopy(chunk.Pcm, (int)sourceIndex * 4, pcm, index * 4, 4);
        }
        var time = outputStart * TimeSpan.TicksPerSecond / sampleRate;
        var end = outputEnd * TimeSpan.TicksPerSecond / sampleRate;
        return new AudioChunk(pcm, time, Math.Max(outputFrames > 0 ? 1 : 0, end - time));
    }

    private static BitmapSource Crop(BitmapSource image, Int32Rect crop)
    {
        if (crop.X == 0 && crop.Y == 0 && crop.Width == image.PixelWidth && crop.Height == image.PixelHeight) return image;
        var cropped = new CroppedBitmap(image, crop);
        cropped.Freeze();
        return cropped;
    }

    private static BitmapSource Resize(BitmapSource image, int width, int height)
    {
        if (image.PixelWidth == width && image.PixelHeight == height) return image;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            context.DrawImage(image, new Rect(0, 0, width, height));
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static string Clock(TimeSpan value)
    {
        var total = Math.Max(0, (int)value.TotalSeconds);
        return total / 60 + ":" + (total % 60).ToString("00");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Space && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.ButtonBase
                 and not System.Windows.Controls.Primitives.TextBoxBase and not ComboBox)
        {
            e.Handled = true;
            TogglePreview();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        ClosePlayback();
        _source.Dispose();
        base.OnClosed(e);
    }
}
