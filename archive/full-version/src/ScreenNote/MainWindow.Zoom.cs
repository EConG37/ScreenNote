using System;
using System.Windows;
using System.Windows.Input;

namespace ScreenNote;

public partial class MainWindow
{
    private double? _zoomOverride;
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeEditorZoom(1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeEditorZoom(0.8);
    private void ViewImage_Click(object sender, RoutedEventArgs e)
    {
        if (Surface.Source == null) return;
        Surface.ClearSelection();
        new ImageViewerWindow(Surface.Export()) { Owner = this }.ShowDialog();
    }
    private void ImageZoom_Wheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        ChangeEditorZoom(e.Delta > 0 ? 1.25 : 0.8); e.Handled = true;
    }
    private void ChangeEditorZoom(double multiplier)
    {
        if (Surface.Source == null) return;
        var factor = ImageView.Width / Surface.Source.PixelWidth;
        if (!double.IsFinite(factor) || factor <= 0) factor = 1;
        var x = (ImageScroll.HorizontalOffset + ImageScroll.ViewportWidth / 2) / factor;
        var y = (ImageScroll.VerticalOffset + ImageScroll.ViewportHeight / 2) / factor;
        _zoomOverride = Math.Clamp(factor * multiplier, 0.05, 8);
        UpdateZoom(); ImageScroll.UpdateLayout();
        ImageScroll.ScrollToHorizontalOffset(x * _zoomOverride.Value - ImageScroll.ViewportWidth / 2);
        ImageScroll.ScrollToVerticalOffset(y * _zoomOverride.Value - ImageScroll.ViewportHeight / 2);
    }
}
