using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenNote;

internal sealed class SubtitleCropControl : FrameworkElement
{
    private BitmapSource? _source;
    private SubtitleCrop _crop = new(0.8, 1);
    private Rect _imageRect;
    private int _drag;
    private double _anchor;
    private SubtitleCrop _original;
    internal event Action<SubtitleCrop>? CropChanged;
    internal BitmapSource? Source { get => _source; set { _source = value; InvalidateVisual(); } }
    internal SubtitleCrop Crop { get => _crop; set { _crop = value; InvalidateVisual(); } }
    internal SubtitleCropControl()
    {
        Height = 180; Cursor = Cursors.SizeNS; Focusable = true;
        ToolTip = "拖动上下边界调整台词范围，拖动中间移动整个范围";
        MouseLeftButtonDown += (_, e) =>
        {
            if (!IsEnabled || _source == null || _imageRect.Height <= 0) return;
            var p = e.GetPosition(this);
            if (!_imageRect.Contains(p)) return;
            var top = _imageRect.Top + _crop.Top * _imageRect.Height;
            var bottom = _imageRect.Top + _crop.Bottom * _imageRect.Height;
            _drag = Math.Abs(p.Y - top) <= 9 ? 1 : Math.Abs(p.Y - bottom) <= 9 ? 2 : p.Y > top && p.Y < bottom ? 3 : Math.Abs(p.Y - top) < Math.Abs(p.Y - bottom) ? 1 : 2;
            _anchor = p.Y / _imageRect.Height; _original = _crop; CaptureMouse(); e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (_drag == 0) return;
            var position = Math.Clamp((e.GetPosition(this).Y - _imageRect.Top) / _imageRect.Height, 0, 1);
            var delta = e.GetPosition(this).Y / _imageRect.Height - _anchor;
            _crop = _drag switch { 1 => new(Math.Min(position, _original.Bottom - 0.03), _original.Bottom), 2 => new(_original.Top, Math.Max(position, _original.Top + 0.03)), _ => new(_original.Top + Math.Clamp(delta, -_original.Top, 1 - _original.Bottom), _original.Bottom + Math.Clamp(delta, -_original.Top, 1 - _original.Bottom)) };
            InvalidateVisual(); CropChanged?.Invoke(_crop); e.Handled = true;
        };
        MouseLeftButtonUp += (_, _) => { _drag = 0; ReleaseMouseCapture(); };
        LostMouseCapture += (_, _) => _drag = 0;
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(237, 239, 235)), null, new Rect(RenderSize));
        if (_source == null) return;
        var factor = Math.Min(Math.Max(1, ActualWidth - 12) / _source.PixelWidth, Math.Max(1, ActualHeight - 12) / _source.PixelHeight);
        var width = _source.PixelWidth * factor; var height = _source.PixelHeight * factor;
        _imageRect = new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
        dc.DrawImage(_source, _imageRect);
        if (!IsEnabled) return;
        var band = new Rect(_imageRect.X, _imageRect.Y + height * _crop.Top, width, height * (_crop.Bottom - _crop.Top));
        var shade = new SolidColorBrush(Color.FromArgb(155, 0, 0, 0));
        dc.DrawRectangle(shade, null, new Rect(_imageRect.Left, _imageRect.Top, width, Math.Max(0, band.Top - _imageRect.Top)));
        dc.DrawRectangle(shade, null, new Rect(_imageRect.Left, band.Bottom, width, Math.Max(0, _imageRect.Bottom - band.Bottom)));
        dc.DrawRectangle(null, new Pen(Brushes.MediumSeaGreen, 2), band);
        foreach (var y in new[] { band.Top, band.Bottom }) dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.SeaGreen, 1), new Rect(band.X + width / 2 - 17, y - 4, 34, 8), 3, 3);
    }
}
