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
    private TimeSpan _start;
    private TimeSpan _end;
    private Int32Rect? _crop;
    private Point? _dragFrom;
    private bool _dragging;
    private double _scale = 1;
    private bool _ready;

    internal string? ResultPath { get; private set; }

    internal RecordEditorWindow(string path)
    {
        InitializeComponent();
        _source = RecordSource.Open(path);
        _start = TimeSpan.Zero;
        _end = _source.Duration;
        BuildOverlay();
        Subtitle.Text = IOPath.GetFileName(path) + " · " + Clock(_source.Duration) + " · " + _source.Size.Width + " × " + _source.Size.Height;
        PreviewImage.Source = _source.Poster;
        foreach (var thumbnail in _source.Thumbnails)
        {
            FilmStrip.Children.Add(new Border
            {
                Width = 66,
                Height = 44,
                Margin = new Thickness(0, 0, 4, 0),
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xED)),
                Child = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill },
            });
        }
        if (_source.Mode == RecordMode.Gif) GifChoice.IsChecked = true; else VideoChoice.IsChecked = true;
        StartSlider.Maximum = Math.Max(0.1, _source.Duration.TotalSeconds);
        EndSlider.Maximum = StartSlider.Maximum;
        EndSlider.Value = StartSlider.Maximum;
        _ready = true;
        UpdateTrim();
        RefreshFormat();
        Layout();
    }

    private void BuildOverlay()
    {
        for (var index = 0; index < 4; index++)
        {
            var shade = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0x12, 0x14, 0x11)), IsHitTestVisible = false };
            _shades.Add(shade);
            Overlay.Children.Add(shade);
        }
        _border = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection(new[] { 4.0, 3.0 }),
            IsHitTestVisible = false,
        };
        Overlay.Children.Add(_border);
        for (var index = 0; index < 4; index++)
        {
            var handle = new Rectangle
            {
                Width = 9,
                Height = 9,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x20, 0x23, 0x1F)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            _handles.Add(handle);
            Overlay.Children.Add(handle);
        }
    }

    private void Layout()
    {
        if (_source.Poster == null) return;
        var stage = Stage;
        var width = Math.Max(1, stage.ActualWidth);
        var height = Math.Max(1, stage.ActualHeight);
        _scale = Math.Min(width / _source.Size.Width, height / _source.Size.Height);
        if (_scale <= 0) _scale = 1;
        var displayWidth = _source.Size.Width * _scale;
        var displayHeight = _source.Size.Height * _scale;
        PreviewImage.Width = displayWidth;
        PreviewImage.Height = displayHeight;
        Overlay.Width = displayWidth;
        Overlay.Height = displayHeight;
        PreviewImage.HorizontalAlignment = HorizontalAlignment.Center;
        PreviewImage.VerticalAlignment = VerticalAlignment.Center;
        Overlay.HorizontalAlignment = HorizontalAlignment.Center;
        Overlay.VerticalAlignment = VerticalAlignment.Center;
        RenderCrop();
    }

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => Layout();

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
        Place(_handles[0], left - 4, top - 4, 9, 9);
        Place(_handles[1], left + width - 5, top - 4, 9, 9);
        Place(_handles[2], left - 4, top + height - 5, 9, 9);
        Place(_handles[3], left + width - 5, top + height - 5, 9, 9);

        CropText.Text = crop.Width == _source.Size.Width && crop.Height == _source.Size.Height
            ? "全画面 " + crop.Width + " × " + crop.Height
            : "保留 " + crop.Width + " × " + crop.Height + "（原 " + _source.Size.Width + " × " + _source.Size.Height + "）";
        UpdateEstimate();
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(Overlay);
        _dragging = true;
        Overlay.CaptureMouse();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _dragFrom is not Point start) return;
        var current = e.GetPosition(Overlay);
        _crop = ToSourceRect(start, current);
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
        var x = (int)Math.Round(Math.Min(x1, x2));
        var y = (int)Math.Round(Math.Min(y1, y2));
        return new Int32Rect(x, y, Math.Max(1, (int)Math.Round(Math.Abs(x2 - x1))), Math.Max(1, (int)Math.Round(Math.Abs(y2 - y1))));
    }

    private void ResetCrop_Click(object sender, RoutedEventArgs e)
    {
        _crop = null;
        RenderCrop();
        StatusText.Text = "在画面里拖拽框出要保留的区域。";
    }

    private void ClearCrop_Click(object sender, RoutedEventArgs e)
    {
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
    }

    private void UpdateTrim()
    {
        StartText.Text = Clock(_start);
        EndText.Text = Clock(_end);
        KeepText.Text = "保留 " + Clock(_end - _start) + "（原片 " + Clock(_source.Duration) + "）";
        UpdateEstimate();
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

    private void UpdateEstimate()
    {
        if (!_ready) return;
        var crop = EffectiveCrop();
        var gif = GifChoice.IsChecked == true;
        if (gif)
        {
            var edge = Math.Min(1440, Math.Max(crop.Width, crop.Height));
            var size = Fit(crop, edge);
            var target = IntTag(GifTarget, 10 * 1024 * 1024);
            EstimateText.Text = "GIF " + size.Width + " × " + size.Height + " · " + IntTag(GifFps, 15) + " 帧/秒 · "
                + (target > 0 ? "自动压到 " + target / (1024 * 1024) + " MB 内" : "不压缩");
        }
        else
        {
            EstimateText.Text = "MP4 " + crop.Width + " × " + crop.Height + " · " + IntTag(VideoFps, 30) + " 帧/秒 · H.264";
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
        var start = _start;
        var end = _end;
        var crop = EffectiveCrop();
        var fps = gif ? IntTag(GifFps, 15) : IntTag(VideoFps, 30);
        var target = gif ? IntTag(GifTarget, 10 * 1024 * 1024) : 0;
        var folder = IOPath.GetDirectoryName(_source.Path) ?? App.DataDirectory;
        var path = IOPath.Combine(folder, "拾屏-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-"
            + Guid.NewGuid().ToString("N")[..8] + (gif ? ".gif" : ".mp4"));

        SaveButton.IsEnabled = false;
        GifChoice.IsEnabled = false;
        VideoChoice.IsEnabled = false;
        StatusText.Text = "正在导出…";
        var progress = new Progress<string>(text => StatusText.Text = text);
        try
        {
            var size = await Task.Run(() => gif
                ? ExportGif(_source.Path, path, start, end, crop, fps, target, progress)
                : ExportVideo(_source.Path, path, start, end, crop, fps, progress));
            ResultPath = path;
            StatusText.Text = "已保存 " + IOPath.GetFileName(path) + " · " + (size / 1024.0 / 1024.0).ToString("0.0") + " MB";
            DialogResult = true;
        }
        catch (Exception error)
        {
            SaveButton.IsEnabled = true;
            GifChoice.IsEnabled = true;
            VideoChoice.IsEnabled = true;
            StatusText.Text = "导出失败：" + error.Message;
        }
    }

    private static long ExportGif(string sourcePath, string path, TimeSpan start, TimeSpan end, Int32Rect crop,
        int fps, long targetBytes, IProgress<string>? progress)
    {
        var steps = new (int Edge, int Colors)[] { (1440, 256), (1080, 160), (840, 128), (640, 96), (480, 64) };
        var attempt = 0;
        foreach (var step in steps)
        {
            attempt++;
            var size = Fit(crop, step.Edge);
            var partial = path + ".partial";
            try
            {
                progress?.Report(targetBytes > 0
                    ? "正在导出 GIF（第 " + attempt + " 档：" + size.Width + " × " + size.Height + "）…"
                    : "正在导出 GIF…");
                using (var source = RecordSource.Open(sourcePath))
                using (var stream = File.Create(partial))
                {
                    using var writer = new AnimatedGifWriter(stream, size.Width, size.Height);
                    var delay = Math.Max(2, (int)Math.Round(100.0 / Math.Max(1, fps)));
                    var nextSlot = 0L;
                    var interval = TimeSpan.TicksPerSecond / Math.Max(1, fps);
                    source.Read(start, end, (frame, relative) =>
                    {
                        if (relative.Ticks < nextSlot) return true;
                        nextSlot += interval;
                        writer.Append(Resize(Crop(frame, crop), size.Width, size.Height), delay, step.Colors);
                        return true;
                    });
                    if (writer.FrameCount == 0) throw new InvalidOperationException("选定的时间段里没有可导出的画面。");
                }
                var length = new FileInfo(partial).Length;
                if (targetBytes <= 0 || length <= targetBytes || attempt == steps.Length)
                {
                    File.Move(partial, path, true);
                    return length;
                }
                File.Delete(partial);
            }
            catch
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                throw;
            }
        }
        throw new InvalidOperationException("GIF 导出失败。");
    }

    private static long ExportVideo(string sourcePath, string path, TimeSpan start, TimeSpan end, Int32Rect crop,
        int fps, IProgress<string>? progress)
    {
        var even = new Int32Rect(crop.X & ~1, crop.Y & ~1, crop.Width & ~1, crop.Height & ~1);
        if (even.Width < 2 || even.Height < 2) throw new InvalidOperationException("裁剪区域太小，无法输出视频。");
        var partial = path + ".partial";
        try
        {
            progress?.Report("正在导出视频（" + even.Width + " × " + even.Height + "）…");
            using var source = RecordSource.Open(sourcePath);
            using var writer = new Mp4Writer(partial, even.Width, even.Height, Math.Max(1, fps));
            BitmapSource? pending = null;
            var pendingTime = 0L;
            var total = (end - start).Ticks;
            source.Read(start, end, (frame, relative) =>
            {
                if (pending != null && relative.Ticks > pendingTime)
                {
                    writer.WriteVideoFrame(pending, pendingTime, relative.Ticks - pendingTime);
                }
                pending = Crop(frame, even);
                pendingTime = relative.Ticks;
                return true;
            });
            if (pending == null) throw new InvalidOperationException("选定的时间段里没有可导出的画面。");
            writer.WriteVideoFrame(pending, pendingTime, Math.Max(1, total - pendingTime));
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
    }

    protected override void OnClosed(EventArgs e)
    {
        _source.Dispose();
        base.OnClosed(e);
    }
}