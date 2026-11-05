using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ScreenNote;

public partial class RecordEditorWindow
{
    private double? _cropRatio;
    private int _cropDragMode;
    private Int32Rect _cropAtGrab;
    private readonly List<Rectangle> _trimGripMarks = new();
    private Rectangle _timelinePlayhead = null!;
    private Path _timelineHead = null!;

    private void Titlebar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) Maximize_Click(sender, e);
        else DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void PictureNav_Checked(object sender, RoutedEventArgs e)
    {
        if (_ready) CropButton.Focus();
    }

    private void SegmentNav_Checked(object sender, RoutedEventArgs e)
    {
        if (_ready) PlaybackSlider.Focus();
    }

    private void OutputNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (GifChoice.IsChecked == true) GifChoice.Focus(); else VideoChoice.Focus();
    }

    private void RatioOriginal_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ClearCrop_Click(sender, e);
    }

    private void RatioWide_Checked(object sender, RoutedEventArgs e) => ApplyCropRatio(16.0 / 9.0);
    private void RatioSquare_Checked(object sender, RoutedEventArgs e) => ApplyCropRatio(1.0);

    private void ApplyCropRatio(double ratio)
    {
        if (!_ready) return;
        _cropRatio = ratio;
        var current = EffectiveCrop();
        var width = current.Width;
        var height = (int)Math.Round(width / ratio);
        if (height > current.Height)
        {
            height = current.Height;
            width = (int)Math.Round(height * ratio);
        }
        width = Math.Clamp(width, 2, current.Width);
        height = Math.Clamp(height, 2, current.Height);
        _crop = new Int32Rect(current.X + (current.Width - width) / 2,
            current.Y + (current.Height - height) / 2, width, height);
        RenderCrop();
        StatusText.Text = "已按比例裁剪，可拖动边角调整或拖动画面内的选区。";
    }

    private void BeginCropDrag(Point point)
    {
        _cropAtGrab = EffectiveCrop();
        _cropDragMode = 0;
        if (_crop == null) return;
        var left = _cropAtGrab.X * _scale;
        var top = _cropAtGrab.Y * _scale;
        var right = left + _cropAtGrab.Width * _scale;
        var bottom = top + _cropAtGrab.Height * _scale;
        if (Math.Abs(point.X - left) <= 13 && Math.Abs(point.Y - top) <= 13) _cropDragMode = 2;
        else if (Math.Abs(point.X - right) <= 13 && Math.Abs(point.Y - top) <= 13) _cropDragMode = 3;
        else if (Math.Abs(point.X - left) <= 13 && Math.Abs(point.Y - bottom) <= 13) _cropDragMode = 4;
        else if (Math.Abs(point.X - right) <= 13 && Math.Abs(point.Y - bottom) <= 13) _cropDragMode = 5;
        else if (point.X > left && point.X < right && point.Y > top && point.Y < bottom) _cropDragMode = 1;
    }

    private Int32Rect CropFromDrag(Point start, Point current)
    {
        if (_cropDragMode == 1)
        {
            var x = _cropAtGrab.X + (int)Math.Round((current.X - start.X) / _scale);
            var y = _cropAtGrab.Y + (int)Math.Round((current.Y - start.Y) / _scale);
            return new Int32Rect(Math.Clamp(x, 0, _source.Size.Width - _cropAtGrab.Width),
                Math.Clamp(y, 0, _source.Size.Height - _cropAtGrab.Height), _cropAtGrab.Width, _cropAtGrab.Height);
        }
        if (_cropDragMode >= 2)
        {
            var x = _cropDragMode is 2 or 4 ? _cropAtGrab.X + _cropAtGrab.Width : _cropAtGrab.X;
            var y = _cropDragMode is 2 or 3 ? _cropAtGrab.Y + _cropAtGrab.Height : _cropAtGrab.Y;
            return ToSourceRect(new Point(x * _scale, y * _scale), current);
        }
        return ToSourceRect(start, current);
    }

    private void LayoutFilmstrip()
    {
        if (FilmStrip == null || FilmStrip.Children.Count == 0) return;
        var width = Math.Max(1, FilmStrip.ActualWidth) / FilmStrip.Children.Count;
        foreach (FrameworkElement frame in FilmStrip.Children)
        {
            frame.Width = width;
            frame.Height = Math.Max(1, FilmStrip.ActualHeight);
        }
    }

    private void UpdateTimelineCursor()
    {
        if (_timelinePlayhead == null || _source == null) return;
        var width = TrimCanvas.ActualWidth;
        var height = TrimCanvas.ActualHeight;
        if (width < 10 || height < 10) return;
        var x = Math.Clamp(_previewPosition.TotalMilliseconds / Math.Max(1, _source.Duration.TotalMilliseconds), 0, 1) * width;
        x = Math.Clamp(x, 5, width - 5);
        Place(_timelinePlayhead, x - 1, 6, 2, height - 6);
        Canvas.SetLeft(_timelineHead, x - 5);
        Canvas.SetTop(_timelineHead, 0);
    }

    private void UpdatePlayAppearance(string? status = null)
    {
        if (PlayPauseButton == null || PlayPauseGlyph == null) return;
        var label = status ?? (_previewPlaying ? "暂停" : "播放");
        PlayPauseGlyph.Kind = _previewPlaying ? "pause" : "play";
        PlayPauseButton.ToolTip = label + " · 空格";
        AutomationProperties.SetName(PlayPauseButton, label);
    }

    private void SetCropEditingEnabled(bool enabled)
    {
        CropTools.IsEnabled = enabled;
        Overlay.IsEnabled = enabled;
        PictureNav.IsEnabled = enabled;
        SegmentNav.IsEnabled = enabled;
        OutputNav.IsEnabled = enabled;
    }
}
