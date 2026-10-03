using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ScreenNote;

public enum CaptureAction { None, Copy, Edit, SendAi, Ocr, Translate, Summary }

public sealed class CaptureWindow : Window
{
    private readonly BitmapSource _desktop;
    private readonly Drawing.Rectangle _bounds;
    private readonly Canvas _canvas = new();
    private readonly Border _shade = new() { Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Shapes.Rectangle _selection = new() { Stroke = new SolidColorBrush(Color.FromRgb(37, 99, 235)), StrokeThickness = 2, Fill = Brushes.Transparent };
    private readonly TextBlock _label = new() { Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(220, 32, 34, 38)), Padding = new Thickness(10, 6, 10, 6) };
    private readonly Border _instructions = new() { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(221, 223, 227)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 12, 18, 12) };
    private readonly Border _actions = new() { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(221, 223, 227)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(5), Visibility = Visibility.Collapsed };
    private readonly List<Shapes.Rectangle> _handles = [];
    private readonly AnnotationSurface _focusSurface = new() { Tool = AnnotationTool.HighlightBox, InkColor = Color.FromRgb(75, 125, 238) };
    private readonly Viewbox _focusView = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly WrapPanel _actionPanel = new();
    private readonly Dictionary<CaptureAction, Button> _actionButtons = new();
    private Button _focusButton = null!;
    private bool _focusMode;
    private Int32Rect _focusCrop;
    private Point _anchor;
    private Point _moveOrigin;
    private Rect _rect;
    private Rect _rectBeforeMove;
    private bool _dragging, _moving;
    private ResizeEdges _resizeEdges;
    public BitmapSource? CapturedImage { get; private set; }
    public BitmapSource? OriginalCapturedImage { get; private set; }
    public string CapturedDocumentJson { get; private set; } = "";
    public CaptureAction Action { get; private set; }
    public string SelectedAiTarget { get; set; } = "deepseek";
    public Size DesktopPixelSize => new(_desktop.PixelWidth, _desktop.PixelHeight);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    public CaptureWindow() : this(Forms.SystemInformation.VirtualScreen) { }

    private CaptureWindow(Drawing.Rectangle bounds) : this(CaptureDesktop(bounds), bounds) { }

    internal static BitmapSource CaptureDesktop(Drawing.Rectangle bounds)
    {
        using var screenshot = new Drawing.Bitmap(bounds.Width, bounds.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(screenshot)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, Drawing.CopyPixelOperation.SourceCopy);
        using var stream = new MemoryStream();
        screenshot.Save(stream, ImageFormat.Png); stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }

    public CaptureWindow(BitmapSource desktop, Drawing.Rectangle? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        _desktop = desktop;
        _bounds = bounds ?? new Drawing.Rectangle(0, 0, desktop.PixelWidth, desktop.PixelHeight);
        if (_bounds.Width <= 0 || _bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds), "截图窗口范围必须大于零。");
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; Background = Brushes.Black; Cursor = Cursors.Cross;
        var grid = new Grid();
        grid.Children.Add(new Image { Source = _desktop, Stretch = Stretch.Fill });
        grid.Children.Add(_shade);
        grid.Children.Add(_canvas);
        _focusView.Child = _focusSurface; _canvas.Children.Add(_focusView);
        _selection.Visibility = Visibility.Collapsed; _label.Visibility = Visibility.Collapsed;
        _canvas.Children.Add(_selection);
        for (var index = 0; index < 8; index++)
        {
            var handle = new Shapes.Rectangle { Width = 8, Height = 8, Fill = Brushes.White, Stroke = _selection.Stroke, StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            _handles.Add(handle); _canvas.Children.Add(handle);
        }
        _canvas.Children.Add(_label);
        _instructions.Child = new TextBlock { Text = "拖动框选  ·  拖动边缘调整大小  ·  Enter 复制  ·  Ctrl+E 编辑  ·  Esc 取消", Foreground = new SolidColorBrush(Color.FromRgb(80, 84, 93)), FontSize = 14 };
        _canvas.Children.Add(_instructions);
        var actionPanel = _actionPanel;
        var blue = new SolidColorBrush(Color.FromRgb(32, 35, 31));
        var copy = new Button { Content = "复制截屏", Background = blue, BorderBrush = blue, Foreground = Brushes.White, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(2), MinWidth = 100, ToolTip = "Enter 或双击选区：复制截屏" };
        if (TryFindResource("PrimaryButton") is Style primaryStyle) copy.Style = primaryStyle;
        var edit = new Button { Content = "编辑截屏", Background = Brushes.White, BorderBrush = _actions.BorderBrush, Foreground = new SolidColorBrush(Color.FromRgb(37, 40, 46)), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(2), MinWidth = 100, ToolTip = "Ctrl+E：编辑截屏" };
        var cancel = new Button { Content = "取消", Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(115, 119, 128)), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(2), MinWidth = 64, ToolTip = "Esc：取消" };
        copy.Click += (_, e) => { Complete(CaptureAction.Copy); e.Handled = true; };
        edit.Click += (_, e) => { Complete(CaptureAction.Edit); e.Handled = true; };
        cancel.Click += (_, e) => { Cancel(); e.Handled = true; };
        actionPanel.Children.Add(copy); actionPanel.Children.Add(edit);
        _actionButtons.Add(CaptureAction.Copy, copy); _actionButtons.Add(CaptureAction.Edit, edit);
        var send = CreateAction("发送给 AI", "send", "选择 DeepSeek、豆包或 ChatGPT；内容仅放入网页草稿");
        ((StackPanel)send.Content).Children.Add(new TextBlock { Text = "⌄", Margin = new Thickness(7, -2, 0, 0), Foreground = send.Foreground, VerticalAlignment = VerticalAlignment.Center });
        var destinations = new ContextMenu { PlacementTarget = send, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (TryFindResource("CaptureDestinationMenu") is Style menuStyle) destinations.Style = menuStyle;
        foreach (var (id, name) in new[] { ("deepseek", "DeepSeek"), ("doubao", "豆包"), ("chatgpt", "ChatGPT") })
        {
            var item = new MenuItem { Header = "发送给 " + name, Tag = id };
            if (TryFindResource("CaptureDestinationItem") is Style itemStyle) item.Style = itemStyle;
            item.Click += (_, e) => { SelectedAiTarget = id; Complete(CaptureAction.SendAi); e.Handled = true; };
            destinations.Items.Add(item);
        }
        send.ContextMenu = destinations;
        destinations.Opened += (_, _) => { foreach (MenuItem item in destinations.Items) item.IsChecked = Equals(item.Tag, SelectedAiTarget); };
        send.Click += (_, e) => { destinations.IsOpen = true; e.Handled = true; };
        _actionButtons.Add(CaptureAction.SendAi, send); actionPanel.Children.Add(send);
        AddAction("图片 OCR", "ocr", CaptureAction.Ocr, "识别文字并直接打开文字编辑区");
        _focusButton = CreateAction("高亮选框", "focus", "点击后，在截图选区内拖动绘制聚焦框；Ctrl+Z 撤销");
        _focusButton.Click += (_, e) => { ToggleFocus(); e.Handled = true; };
        actionPanel.Children.Add(_focusButton);
        AddAction("翻译", "translate", CaptureAction.Translate, "本地中英互译，在软件内查看并复制译文");
        AddAction("总结", "summary", CaptureAction.Summary, "带上截图与总结提示词，打开所选 AI 网页");
        actionPanel.Children.Add(cancel);
        _actions.Child = actionPanel; _canvas.Children.Add(_actions);
        Content = grid;
        SourceInitialized += (_, _) =>
        {
            if (!SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x0040))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将截图选区窗口覆盖到完整桌面，请重试。");
        };
        Loaded += (_, _) => { Canvas.SetLeft(_instructions, Math.Max(12, (ActualWidth - _instructions.ActualWidth) / 2)); Canvas.SetTop(_instructions, Math.Max(24, ActualHeight - 90)); Focus(); };
        MouseLeftButtonDown += PointerDown; MouseMove += PointerMove; MouseLeftButtonUp += PointerUp;
        _canvas.LostMouseCapture += (_, _) => { _dragging = false; _moving = false; _resizeEdges = ResizeEdges.None; };
        _canvas.SizeChanged += (_, _) => UpdateSelection();
        MouseRightButtonDown += (_, e) => { Cancel(); e.Handled = true; };
        PreviewKeyDown += (_, e) => { if (HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true; };
    }
    private Button CreateAction(string label, string icon, string hint)
    {
        var button = new Button { Background = Brushes.White, BorderBrush = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(48, 52, 60)), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(2), ToolTip = hint, Cursor = Cursors.Hand, FontSize = 12 };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new ToolGlyph { Kind = icon, Width = 17, Height = 17, Foreground = button.Foreground, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        return button;
    }
    private void AddAction(string label, string icon, CaptureAction action, string hint)
    {
        var button = CreateAction(label, icon, hint);
        button.Click += (_, e) => { Complete(action); e.Handled = true; };
        _actionButtons.Add(action, button); _actionPanel.Children.Add(button);
    }
    private void ToggleFocus()
    {
        if (_rect.Width < 2 || _rect.Height < 2) return;
        _focusMode = !_focusMode;
        _focusView.IsHitTestVisible = _focusMode;
        _selection.IsHitTestVisible = !_focusMode;
        _focusButton.Background = _focusMode ? new SolidColorBrush(Color.FromRgb(235, 242, 255)) : Brushes.White;
        _focusButton.BorderBrush = _focusMode ? new SolidColorBrush(Color.FromRgb(183, 206, 249)) : Brushes.Transparent;
        if (_focusMode) _focusSurface.Focus(); else Focus();
        UpdateSelection();
    }
    internal void SetAiBusy(bool busy)
    {
        foreach (var action in new[] { CaptureAction.SendAi, CaptureAction.Summary })
            _actionButtons[action].IsEnabled = !busy;
    }
    private bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (_actionButtons[CaptureAction.SendAi].ContextMenu?.IsOpen == true)
        { if (key == Key.Escape) { _actionButtons[CaptureAction.SendAi].ContextMenu!.IsOpen = false; return true; } return false; }
        if (key == Key.Escape) { Cancel(); return true; }
        if (key == Key.Enter) { Complete(CaptureAction.Copy); return true; }
        if (key == Key.E && (modifiers & ModifierKeys.Control) != 0) { Complete(CaptureAction.Edit); return true; }
        if (key == Key.Z && (modifiers & ModifierKeys.Control) != 0) { _focusSurface.Undo(); return true; }
        if (key == Key.F && modifiers == ModifierKeys.None) { ToggleFocus(); return true; }
        return false;
    }
    private Point GetPoint(MouseEventArgs e) { var p = e.GetPosition(_canvas); return new Point(Math.Clamp(p.X, 0, _canvas.ActualWidth), Math.Clamp(p.Y, 0, _canvas.ActualHeight)); }
    private void PointerDown(object sender, MouseButtonEventArgs e)
    {
        if (_actions.IsMouseOver || _actionButtons[CaptureAction.SendAi].ContextMenu?.IsOpen == true || _focusMode) return;
        if (e.ClickCount == 2 && _rect.Contains(GetPoint(e))) { Complete(); return; }
        var point = GetPoint(e);
        _resizeEdges = HitResizeEdges(point);
        _moving = _resizeEdges == ResizeEdges.None && !_rect.IsEmpty && _rect.Width >= 2 && _rect.Height >= 2 && _rect.Contains(point);
        _moveOrigin = point; _rectBeforeMove = _rect; _anchor = point;
        if (!_moving && _resizeEdges == ResizeEdges.None) { _rect = new Rect(point, point); UpdateSelection(); }
        _dragging = true; _canvas.CaptureMouse(); e.Handled = true;
    }
    private void PointerMove(object sender, MouseEventArgs e)
    {
        if (_focusMode) { Cursor = _actions.IsMouseOver ? Cursors.Arrow : Cursors.Cross; return; }
        var point = GetPoint(e);
        if (!_dragging)
        {
            Cursor = _actions.IsMouseOver ? Cursors.Arrow : CursorFor(HitResizeEdges(point), _rect.Width >= 2 && _rect.Height >= 2 && _rect.Contains(point));
            return;
        }
        if (_resizeEdges != ResizeEdges.None)
        {
            _rect = ResizeSelection(_rectBeforeMove, point - _moveOrigin, _resizeEdges, _canvas.ActualWidth, _canvas.ActualHeight);
            Cursor = CursorFor(_resizeEdges, false);
        }
        else if (_moving) { _rect = new Rect(Math.Clamp(_rectBeforeMove.X + point.X - _moveOrigin.X, 0, _canvas.ActualWidth - _rectBeforeMove.Width), Math.Clamp(_rectBeforeMove.Y + point.Y - _moveOrigin.Y, 0, _canvas.ActualHeight - _rectBeforeMove.Height), _rectBeforeMove.Width, _rectBeforeMove.Height); Cursor = Cursors.SizeAll; }
        else _rect = new Rect(_anchor, point);
        UpdateSelection();
    }
    private void PointerUp(object sender, MouseButtonEventArgs e) { _dragging = false; _moving = false; _resizeEdges = ResizeEdges.None; _canvas.ReleaseMouseCapture(); }
    private void UpdateSelection()
    {
        var valid = _rect.Width >= 2 && _rect.Height >= 2;
        UpdateShade(valid);
        _selection.Visibility = valid ? Visibility.Visible : Visibility.Collapsed; _label.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_selection, _rect.X); Canvas.SetTop(_selection, _rect.Y); _selection.Width = _rect.Width; _selection.Height = _rect.Height;
        var px = ToPixelRect(_rect, _canvas.ActualWidth, _canvas.ActualHeight, _desktop.PixelWidth, _desktop.PixelHeight);
        _label.Text = $"{px.Width} × {px.Height}   " + (_focusMode ? "在选区内拖动高亮框 · Ctrl+Z 撤销 · F 返回框选" : "Enter 复制  ·  Ctrl+E 编辑  ·  F 高亮");
        Canvas.SetLeft(_label, _rect.X); Canvas.SetTop(_label, Math.Max(0, _rect.Y - 38));
        var centerX = _rect.Left + _rect.Width / 2; var centerY = _rect.Top + _rect.Height / 2;
        var positions = new[] { _rect.TopLeft, new Point(centerX, _rect.Top), _rect.TopRight, new Point(_rect.Right, centerY), _rect.BottomRight, new Point(centerX, _rect.Bottom), _rect.BottomLeft, new Point(_rect.Left, centerY) };
        for (var index = 0; index < _handles.Count; index++)
        {
            _handles[index].Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_handles[index], positions[index].X - 4); Canvas.SetTop(_handles[index], positions[index].Y - 4);
        }
        _actions.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
        _instructions.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        if (valid)
        {
            if (px.Width >= 2 && px.Height >= 2 && px != _focusCrop)
            {
                _focusCrop = px;
                var source = new CroppedBitmap(_desktop, px); source.Freeze(); _focusSurface.Source = source;
            }
            _focusView.Visibility = Visibility.Visible;
            _focusView.Width = _rect.Width; _focusView.Height = _rect.Height;
            Canvas.SetLeft(_focusView, _rect.X); Canvas.SetTop(_focusView, _rect.Y);
            _actionPanel.MaxWidth = Math.Max(100, Math.Min(960, _canvas.ActualWidth - 28));
            _actions.Measure(new Size(_actionPanel.MaxWidth + 12, double.PositiveInfinity));
            var actionWidth = _actions.DesiredSize.Width; var actionHeight = _actions.DesiredSize.Height;
            var actionX = Math.Clamp(_rect.Right - actionWidth, 8, Math.Max(8, _canvas.ActualWidth - actionWidth - 8));
            var actionY = _rect.Bottom + 12;
            if (actionY + actionHeight > _canvas.ActualHeight - 8) actionY = _rect.Top - actionHeight - 12;
            if (actionY < 8) actionY = _rect.Bottom - actionHeight - 12;
            actionY = Math.Clamp(actionY, 8, Math.Max(8, _canvas.ActualHeight - actionHeight - 8));
            Canvas.SetLeft(_actions, actionX); Canvas.SetTop(_actions, actionY);
        }
        else { _focusView.Visibility = Visibility.Collapsed; _focusSurface.Source = null; _focusCrop = Int32Rect.Empty; }
    }

    private void UpdateShade(bool hasSelection)
    {
        // Clip the dark layer to the desktop minus the live selection. Both geometries use
        // the same WPF coordinates as the handles, so moving, resizing and DPI changes keep
        // the hole aligned without decoding or repainting another copy of the screenshot.
        var bounds = new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight);
        Geometry clip = new RectangleGeometry(bounds);
        if (hasSelection)
        {
            var clear = _rect;
            clear.Intersect(bounds);
            if (!clear.IsEmpty)
                clip = new CombinedGeometry(GeometryCombineMode.Exclude, clip, new RectangleGeometry(clear));
        }
        clip.Freeze();
        _shade.Clip = clip;
    }

    [Flags]
    internal enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private ResizeEdges HitResizeEdges(Point point)
    {
        if (_rect.IsEmpty || _rect.Width < 2 || _rect.Height < 2) return ResizeEdges.None;
        var hitBox = _rect; hitBox.Inflate(7, 7);
        if (!hitBox.Contains(point)) return ResizeEdges.None;
        var edges = ResizeEdges.None;
        var leftDistance = Math.Abs(point.X - _rect.Left); var rightDistance = Math.Abs(point.X - _rect.Right);
        var topDistance = Math.Abs(point.Y - _rect.Top); var bottomDistance = Math.Abs(point.Y - _rect.Bottom);
        if (Math.Min(leftDistance, rightDistance) <= 7) edges |= leftDistance <= rightDistance ? ResizeEdges.Left : ResizeEdges.Right;
        if (Math.Min(topDistance, bottomDistance) <= 7) edges |= topDistance <= bottomDistance ? ResizeEdges.Top : ResizeEdges.Bottom;
        return edges;
    }

    private static Cursor CursorFor(ResizeEdges edges, bool inside)
    {
        if (edges is (ResizeEdges.Left | ResizeEdges.Top) or (ResizeEdges.Right | ResizeEdges.Bottom)) return Cursors.SizeNWSE;
        if (edges is (ResizeEdges.Left | ResizeEdges.Bottom) or (ResizeEdges.Right | ResizeEdges.Top)) return Cursors.SizeNESW;
        if ((edges & (ResizeEdges.Left | ResizeEdges.Right)) != 0) return Cursors.SizeWE;
        if ((edges & (ResizeEdges.Top | ResizeEdges.Bottom)) != 0) return Cursors.SizeNS;
        return inside ? Cursors.SizeAll : Cursors.Cross;
    }

    internal static Rect ResizeSelection(Rect original, Vector delta, ResizeEdges edges, double canvasWidth, double canvasHeight)
    {
        if (canvasWidth < 2 || canvasHeight < 2 || original.IsEmpty) return original;
        var left = original.Left; var top = original.Top; var right = original.Right; var bottom = original.Bottom;
        if ((edges & ResizeEdges.Left) != 0) left = Math.Clamp(left + delta.X, 0, right - 2);
        if ((edges & ResizeEdges.Right) != 0) right = Math.Clamp(right + delta.X, left + 2, canvasWidth);
        if ((edges & ResizeEdges.Top) != 0) top = Math.Clamp(top + delta.Y, 0, bottom - 2);
        if ((edges & ResizeEdges.Bottom) != 0) bottom = Math.Clamp(bottom + delta.Y, top + 2, canvasHeight);
        return new Rect(new Point(left, top), new Point(right, bottom));
    }
    private void Complete(CaptureAction action = CaptureAction.Copy)
    {
        if (!Enum.IsDefined(action) || action == CaptureAction.None) return;
        if (_rect.IsEmpty || _rect.Width < 2 || _rect.Height < 2) return;
        var pixelRect = ToPixelRect(_rect, _canvas.ActualWidth, _canvas.ActualHeight, _desktop.PixelWidth, _desktop.PixelHeight);
        if (pixelRect.Width < 2 || pixelRect.Height < 2) return;
        var crop = new CroppedBitmap(_desktop, pixelRect); crop.Freeze(); OriginalCapturedImage = crop;
        var annotated = _focusCrop == pixelRect && _focusSurface.HasAnnotations;
        CapturedImage = annotated ? _focusSurface.Export() : crop;
        CapturedDocumentJson = annotated ? _focusSurface.ExportDocument() : "";
        Action = action; DialogResult = true;
    }
    private void Cancel() { Action = CaptureAction.None; CapturedImage = null; OriginalCapturedImage = null; CapturedDocumentJson = ""; DialogResult = false; }
    public static Int32Rect ToPixelRect(Rect rectangle, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight)
    {
        if (logicalWidth <= 0 || logicalHeight <= 0) return Int32Rect.Empty;
        var left = Math.Clamp((int)Math.Floor(rectangle.Left * pixelWidth / logicalWidth), 0, pixelWidth);
        var top = Math.Clamp((int)Math.Floor(rectangle.Top * pixelHeight / logicalHeight), 0, pixelHeight);
        var right = Math.Clamp((int)Math.Ceiling(rectangle.Right * pixelWidth / logicalWidth), left, pixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(rectangle.Bottom * pixelHeight / logicalHeight), top, pixelHeight);
        return new Int32Rect(left, top, right - left, bottom - top);
    }
}
