using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenNote;

internal sealed class ImageViewerWindow : Window
{
    private readonly Image _image;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };
    private readonly TextBlock _scale = new() { Width = 64, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly BitmapSource _source;
    private double _factor = 1;
    private bool _fit = true;
    private Point? _pan;
    private double _startX, _startY;
    internal ImageViewerWindow(BitmapSource source)
    {
        _source = source; Title = "拾屏 精简版 · 放大查看"; Icon = AppIcon.WindowImage;
        Width = 1200; Height = 850; WindowState = WindowState.Maximized;
        _image = new Image { Source = source, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(233, 235, 231)) };
        var toolbar = new DockPanel { Background = Brushes.White, LastChildFill = false, Margin = new Thickness(0, 0, 0, 1) };
        var close = MakeButton("返回（Esc）", Close); DockPanel.SetDock(close, Dock.Right); toolbar.Children.Add(close);
        toolbar.Children.Add(MakeButton("−", () => Zoom(_factor / 1.25))); toolbar.Children.Add(_scale);
        toolbar.Children.Add(MakeButton("＋", () => Zoom(_factor * 1.25)));
        toolbar.Children.Add(MakeButton("适应窗口", () => { _fit = true; ResizeImage(); }));
        toolbar.Children.Add(MakeButton("原图 100%", () => Zoom(1)));
        toolbar.Children.Add(new TextBlock { Text = "滚轮缩放 · 拖动查看", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) });
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        _scroll.Content = _image; root.Children.Add(_scroll); Content = root;
        _scroll.SizeChanged += (_, _) => { if (_fit) ResizeImage(); };
        _scroll.PreviewMouseWheel += (_, e) => { Zoom(_factor * (e.Delta > 0 ? 1.25 : 0.8), e.GetPosition(_scroll)); e.Handled = true; };
        _scroll.PreviewMouseLeftButtonDown += (_, e) => { _pan = e.GetPosition(_scroll); _startX = _scroll.HorizontalOffset; _startY = _scroll.VerticalOffset; _scroll.CaptureMouse(); _scroll.Cursor = Cursors.SizeAll; e.Handled = true; };
        _scroll.MouseMove += (_, e) => { if (_pan is Point start) { var point = e.GetPosition(_scroll); _scroll.ScrollToHorizontalOffset(_startX + start.X - point.X); _scroll.ScrollToVerticalOffset(_startY + start.Y - point.Y); } };
        _scroll.MouseLeftButtonUp += (_, _) => { _pan = null; _scroll.ReleaseMouseCapture(); _scroll.Cursor = Cursors.Arrow; };
        _scroll.LostMouseCapture += (_, _) => _pan = null;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } else if (e.Key is Key.Add or Key.OemPlus) Zoom(_factor * 1.25); else if (e.Key is Key.Subtract or Key.OemMinus) Zoom(_factor / 1.25); };
        Loaded += (_, _) => ResizeImage();
    }
    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(5, 8, 5, 8), MinWidth = 36 };
        button.Click += (_, _) => action(); return button;
    }
    private void Zoom(double factor, Point? anchor = null)
    {
        var old = _factor;
        var point = anchor ?? new Point(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 2);
        var x = (_scroll.HorizontalOffset + point.X) / old; var y = (_scroll.VerticalOffset + point.Y) / old;
        _fit = false; _factor = Math.Clamp(factor, 0.05, 8); ResizeImage(); _scroll.UpdateLayout();
        _scroll.ScrollToHorizontalOffset(x * _factor - point.X); _scroll.ScrollToVerticalOffset(y * _factor - point.Y);
    }
    private void ResizeImage()
    {
        if (_fit) _factor = Math.Min(Math.Max(50, _scroll.ActualWidth - 40) / _source.PixelWidth, Math.Max(50, _scroll.ActualHeight - 40) / _source.PixelHeight);
        _image.Width = _source.PixelWidth * _factor; _image.Height = _source.PixelHeight * _factor;
        _scale.Text = $"{_factor:P0}";
    }
}
