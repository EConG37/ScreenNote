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

public enum CaptureAction { None, Copy, Edit, SendAi, Ocr, Translate, RecordGif = 7, Scroll }

public sealed partial class CaptureWindow : Window
{
    private readonly BitmapSource _desktop;
    private readonly Drawing.Rectangle _bounds;
    private readonly Canvas _canvas = new();
    private readonly Border _shade = new() { Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Shapes.Rectangle _selection = new() { Stroke = new SolidColorBrush(Color.FromRgb(37, 99, 235)), StrokeThickness = 2, Fill = Brushes.Transparent };
    private readonly TextBlock _label = new() { Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(220, 32, 34, 38)), Padding = new Thickness(10, 6, 10, 6) };
    private readonly Border _actions = new() { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(221, 223, 227)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(6), Visibility = Visibility.Collapsed };
    private readonly List<Shapes.Rectangle> _handles = [];
    private readonly AnnotationSurface _focusSurface = new() { Tool = AnnotationTool.HighlightBox, InkColor = Color.FromRgb(75, 125, 238) };
    private readonly Viewbox _focusView = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly StackPanel _actionPanel = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _completionPanel = new() { Orientation = Orientation.Horizontal };
    private readonly Border _actionDivider = new() { Width = 1, Height = 20, Background = new SolidColorBrush(Color.FromRgb(228, 231, 225)), Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private Button _cancelButton = null!;
    private bool _selectionOnly, _toolLabelsVisible = true;
    private readonly Dictionary<CaptureAction, Button> _actionButtons = new();
    private Button _focusButton = null!;
    private bool _focusMode;
    private Int32Rect _focusCrop;
    private Point _anchor;
    private Point _moveOrigin;
    private Rect _rect;
    private Rect _rectBeforeMove;
    private bool _dragging, _moving;
    private IReadOnlyList<Drawing.Rectangle> _windowRegions = Array.Empty<Drawing.Rectangle>();
    private bool _smartPreview, _smartPress;
    private Rect _smartRect;
    private ResizeEdges _resizeEdges;
    public BitmapSource? CapturedImage { get; private set; }
    public BitmapSource? OriginalCapturedImage { get; private set; }
    public string CapturedDocumentJson { get; private set; } = "";
    public CaptureAction Action { get; private set; }
    public Drawing.Rectangle SelectedScreenBounds { get; private set; }
    public string SelectedAiTarget { get; set; } = "deepseek";
    public Size DesktopPixelSize => new(_desktop.PixelWidth, _desktop.PixelHeight);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    public CaptureWindow() : this(Forms.SystemInformation.VirtualScreen) { }

    private CaptureWindow(Drawing.Rectangle bounds) : this(CaptureDesktop(bounds), bounds) { _windowRegions = WindowRegionDetector.Snapshot(bounds); }

    internal static BitmapSource CaptureDesktop(Drawing.Rectangle bounds)
    {
        using var screenshot = new Drawing.Bitmap(bounds.Width, bounds.Height, Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var graphics = Drawing.Graphics.FromImage(screenshot)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, Drawing.CopyPixelOperation.SourceCopy);
        var pixels = screenshot.LockBits(new Drawing.Rectangle(0, 0, bounds.Width, bounds.Height), ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var bitmap = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgr32, null, pixels.Scan0, pixels.Stride * bounds.Height, pixels.Stride);
            bitmap.Freeze(); return bitmap;
        }
        finally { screenshot.UnlockBits(pixels); }
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
        var actionPanel = _actionPanel;
        var copy = CreateAction("复制截屏", "copy", "Enter 或双击选区：复制截屏", Colors.White);
        copy.Background = new SolidColorBrush(Color.FromRgb(35, 120, 75)); copy.FontWeight = FontWeights.SemiBold;
        var enterHint = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Child = new TextBlock { Text = "↵", FontSize = 13, FontWeight = FontWeights.Normal } };
        ((StackPanel)copy.Content).Children.Add(enterHint);
        var edit = CreateAction("编辑截屏", "pen", "Ctrl+E：编辑截屏", Color.FromRgb(140, 97, 22));
        edit.Background = new SolidColorBrush(Color.FromRgb(255, 246, 220));
        var cancel = _cancelButton = CreateAction("取消", "close", "Esc：取消", Color.FromRgb(181, 76, 69));
        copy.Click += (_, e) => { Complete(CaptureAction.Copy); e.Handled = true; };
        edit.Click += (_, e) => { Complete(CaptureAction.Edit); e.Handled = true; };
        cancel.Click += (_, e) => { Cancel(); e.Handled = true; };
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
        AddAction("录制 GIF", "record", CaptureAction.RecordGif, "录制这块屏幕区域，结束后复制 GIF 动图；Ctrl+G");
        AddAction("滚动截图", "scroll", CaptureAction.Scroll, "在选区内向下滚动，将页面拼成一张长图");
        _completionPanel.Children.Add(cancel); _completionPanel.Children.Add(edit); _completionPanel.Children.Add(copy);
        var actionLayout = new StackPanel { Orientation = Orientation.Horizontal };
        actionLayout.Children.Add(actionPanel); actionLayout.Children.Add(_actionDivider); actionLayout.Children.Add(_completionPanel);
        _actions.Child = actionLayout; _canvas.Children.Add(_actions);
        InitializeEditTools();
        Content = grid;
        SourceInitialized += (_, _) =>
        {
            if (!SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x0040))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将截图选区窗口覆盖到完整桌面，请重试。");
        };
        Loaded += (_, _) => Focus();
        MouseLeftButtonDown += PointerDown; MouseMove += PointerMove; MouseLeftButtonUp += PointerUp;
        _canvas.LostMouseCapture += (_, _) => { _dragging = false; _moving = false; _resizeEdges = ResizeEdges.None; };
        _canvas.SizeChanged += (_, _) => UpdateSelection();
        MouseRightButtonDown += (_, e) => { Cancel(); e.Handled = true; };
        PreviewKeyDown += (_, e) => { if (HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true; };
    }
    private Button CreateAction(string label, string icon, string hint, Color? color = null)
    {
        var button = new Button { Height = 36, MinWidth = 36, Background = Brushes.White, BorderBrush = Brushes.Transparent, Foreground = new SolidColorBrush(color ?? Color.FromRgb(48, 52, 60)), Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(2, 0, 2, 0), ToolTip = hint, Cursor = Cursors.Hand, FontSize = 12 };
        if (TryFindResource("CaptureToolbarButton") is Style style) button.Style = style;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(button, 250);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new ToolGlyph { Kind = icon, Width = 17, Height = 17, Foreground = button.Foreground, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        return button;
    }
    private void FitActionBar(double availableWidth)
    {
        // Keep every action on the same row; compact auxiliary tools before hiding any actions.
        SetToolLabelsVisible(availableWidth >= 980);
        _actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    }
    private void SetToolLabelsVisible(bool visible)
    {
        if (_toolLabelsVisible == visible) return;
        _toolLabelsVisible = visible;
        foreach (Button button in _actionPanel.Children)
        {
            var content = (StackPanel)button.Content;
            ((ToolGlyph)content.Children[0]).Margin = new Thickness(0, 0, visible ? 6 : 0, 0);
            for (var i = 1; i < content.Children.Count; i++) content.Children[i].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            content.InvalidateMeasure(); button.InvalidateMeasure();
        }
        _actionPanel.InvalidateMeasure();
        ((StackPanel)_actions.Child).InvalidateMeasure(); _actions.InvalidateMeasure();
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
        SetCaptureTool(_focusMode && _focusSurface.Tool == AnnotationTool.HighlightBox ? null : AnnotationTool.HighlightBox);
        UpdateSelection();
    }
    private void SetCaptureTool(AnnotationTool? tool)
    {
        if (tool.HasValue && (_rect.Width < 2 || _rect.Height < 2)) return;
        if (tool.HasValue) _smartPreview = false;
        _focusMode = tool.HasValue;
        if (tool.HasValue) _focusSurface.Tool = tool.Value;
        else _focusSurface.ClearSelection();
        _focusView.IsHitTestVisible = _focusMode;
        _selection.IsHitTestVisible = !_focusMode;
        _focusButton.Background = _focusMode && tool == AnnotationTool.HighlightBox ? new SolidColorBrush(Color.FromRgb(235, 242, 255)) : Brushes.White;
        _focusButton.BorderBrush = Brushes.Transparent;
        foreach (var entry in _editToolButtons) entry.Value.Background = tool == entry.Key ? new SolidColorBrush(Color.FromRgb(232, 240, 235)) : Brushes.White;
        if (_focusMode) _focusSurface.Focus(); else Focus();
        UpdateSelection();
    }
    internal void SetAiBusy(bool busy)
    {
        _actionButtons[CaptureAction.SendAi].IsEnabled = !busy;
    }
    private bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (_actionButtons[CaptureAction.SendAi].ContextMenu?.IsOpen == true)
        { if (key == Key.Escape) { _actionButtons[CaptureAction.SendAi].ContextMenu!.IsOpen = false; return true; } return false; }
        if (key == Key.Escape) { Cancel(); return true; }
        if (key == Key.Enter) { Complete(CaptureAction.Copy); return true; }
        if (_selectionOnly) return false;
        if (key == Key.E && (modifiers & ModifierKeys.Control) != 0) { Complete(CaptureAction.Edit); return true; }
        if (key == Key.G && modifiers == ModifierKeys.Control) { Complete(CaptureAction.RecordGif); return true; }
        if (key == Key.Z && (modifiers & ModifierKeys.Control) != 0) { _focusSurface.Undo(); return true; }
        if (key == Key.Y && modifiers == ModifierKeys.Control) { _focusSurface.Redo(); return true; }
        if (modifiers == ModifierKeys.None && CaptureToolKey(key, out var tool)) { SetCaptureTool(tool); return true; }
        if (key == Key.F && modifiers == ModifierKeys.None) { ToggleFocus(); return true; }
        return false;
    }
    private Point GetPoint(MouseEventArgs e) { var p = e.GetPosition(_canvas); return new Point(Math.Clamp(p.X, 0, _canvas.ActualWidth), Math.Clamp(p.Y, 0, _canvas.ActualHeight)); }
    private void PointerDown(object sender, MouseButtonEventArgs e)
    {
        if (_actions.IsMouseOver || _editTools.IsMouseOver || _actionButtons[CaptureAction.SendAi].ContextMenu?.IsOpen == true || _focusMode) return;
        if (e.ClickCount == 2 && _rect.Contains(GetPoint(e))) { Complete(); return; }
        var point = GetPoint(e);
        _smartPress = _smartPreview; _smartRect = _rect; _smartPreview = false;
        if (_smartPress) _rect = new Rect();
        _resizeEdges = HitResizeEdges(point);
        _moving = _resizeEdges == ResizeEdges.None && !_rect.IsEmpty && _rect.Width >= 2 && _rect.Height >= 2 && _rect.Contains(point);
        _moveOrigin = point; _rectBeforeMove = _rect; _anchor = point;
        if (!_moving && _resizeEdges == ResizeEdges.None) { _rect = new Rect(point, point); UpdateSelection(); }
        _dragging = true; _canvas.CaptureMouse(); e.Handled = true;
    }
    private void PointerMove(object sender, MouseEventArgs e)
    {
        if (_focusMode) { Cursor = _actions.IsMouseOver || _editTools.IsMouseOver ? Cursors.Arrow : Cursors.Cross; return; }
        var point = GetPoint(e);
        if (!_dragging)
        {
            if ((_smartPreview || _rect.IsEmpty || _rect.Width < 2) && !_actions.IsMouseOver && !_editTools.IsMouseOver) PreviewWindowAt(point);
            Cursor = _actions.IsMouseOver || _editTools.IsMouseOver ? Cursors.Arrow : CursorFor(HitResizeEdges(point), _rect.Width >= 2 && _rect.Height >= 2 && _rect.Contains(point));
            return;
        }
        if (_smartPress && (point - _anchor).Length < 4) return;
        _smartPress = false;
        if (_resizeEdges != ResizeEdges.None)
        {
            _rect = ResizeSelection(_rectBeforeMove, point - _moveOrigin, _resizeEdges, _canvas.ActualWidth, _canvas.ActualHeight);
            Cursor = CursorFor(_resizeEdges, false);
        }
        else if (_moving) { _rect = new Rect(Math.Clamp(_rectBeforeMove.X + point.X - _moveOrigin.X, 0, _canvas.ActualWidth - _rectBeforeMove.Width), Math.Clamp(_rectBeforeMove.Y + point.Y - _moveOrigin.Y, 0, _canvas.ActualHeight - _rectBeforeMove.Height), _rectBeforeMove.Width, _rectBeforeMove.Height); Cursor = Cursors.SizeAll; }
        else _rect = new Rect(_anchor, point);
        UpdateSelection();
    }
    private void PointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_smartPress) { _rect = _smartRect; _smartPress = false; UpdateSelection(); }
        _dragging = false; _moving = false; _resizeEdges = ResizeEdges.None; _canvas.ReleaseMouseCapture();
    }
    private void PreviewWindowAt(Point point)
    {
        if (_canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0) return;
        var pixel = new Drawing.Point(_bounds.X + (int)(point.X * _bounds.Width / _canvas.ActualWidth), _bounds.Y + (int)(point.Y * _bounds.Height / _canvas.ActualHeight));
        Rect next = new();
        foreach (var window in _windowRegions)
            if (window.Contains(pixel)) { next = new Rect((window.X - _bounds.X) * _canvas.ActualWidth / _bounds.Width, (window.Y - _bounds.Y) * _canvas.ActualHeight / _bounds.Height, window.Width * _canvas.ActualWidth / _bounds.Width, window.Height * _canvas.ActualHeight / _bounds.Height); break; }
        if (_rect == next) return;
        _rect = next; _smartPreview = next.Width >= 2; UpdateSelection();
    }
    private void UpdateSelection()
    {
        var valid = _rect.Width >= 2 && _rect.Height >= 2;
        UpdateShade(valid);
        _selection.Visibility = valid ? Visibility.Visible : Visibility.Collapsed; _label.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_selection, _rect.X); Canvas.SetTop(_selection, _rect.Y); _selection.Width = _rect.Width; _selection.Height = _rect.Height;
        var px = ToPixelRect(_rect, _canvas.ActualWidth, _canvas.ActualHeight, _desktop.PixelWidth, _desktop.PixelHeight);
        _label.Text = $"{px.Width} × {px.Height}   " + (_smartPreview ? "单击选择窗口 · 拖动自由框选" : _selectionOnly ? "Enter 加入拼图 · Esc 取消" : _focusMode ? "在选区内标注 · Ctrl+Z 撤销 · V 返回框选" : "Enter 复制  ·  Ctrl+E 编辑  ·  上方工具直接标注");
        Canvas.SetLeft(_label, _rect.X); Canvas.SetTop(_label, Math.Max(0, _rect.Y - 38));
        var centerX = _rect.Left + _rect.Width / 2; var centerY = _rect.Top + _rect.Height / 2;
        var positions = new[] { _rect.TopLeft, new Point(centerX, _rect.Top), _rect.TopRight, new Point(_rect.Right, centerY), _rect.BottomRight, new Point(centerX, _rect.Bottom), _rect.BottomLeft, new Point(_rect.Left, centerY) };
        for (var index = 0; index < _handles.Count; index++)
        {
            _handles[index].Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_handles[index], positions[index].X - 4); Canvas.SetTop(_handles[index], positions[index].Y - 4);
        }
        _actions.Visibility = valid && !_smartPreview ? Visibility.Visible : Visibility.Collapsed;
        _editTools.Visibility = Visibility.Collapsed;
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
            FitActionBar(Math.Max(100, _canvas.ActualWidth - 28));
            var actionWidth = _actions.DesiredSize.Width; var actionHeight = _actions.DesiredSize.Height;
            var actionX = Math.Clamp(_rect.Right - actionWidth, 8, Math.Max(8, _canvas.ActualWidth - actionWidth - 8));
            var actionY = _rect.Bottom + 12;
            if (actionY + actionHeight > _canvas.ActualHeight - 8) actionY = _rect.Top - actionHeight - 12;
            if (actionY < 8) actionY = _rect.Bottom - actionHeight - 12;
            actionY = Math.Clamp(actionY, 8, Math.Max(8, _canvas.ActualHeight - actionHeight - 8));
            Canvas.SetLeft(_actions, actionX); Canvas.SetTop(_actions, actionY);
            PositionEditTools(!_smartPreview && !_selectionOnly);
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
        SelectedScreenBounds = new Drawing.Rectangle(_bounds.X + pixelRect.X, _bounds.Y + pixelRect.Y, pixelRect.Width, pixelRect.Height);
        if (action is CaptureAction.RecordGif or CaptureAction.Scroll) { Action = action; DialogResult = true; return; }
        var crop = new CroppedBitmap(_desktop, pixelRect); crop.Freeze(); OriginalCapturedImage = crop;
        var annotated = _focusCrop == pixelRect && _focusSurface.HasAnnotations;
        CapturedImage = annotated ? _focusSurface.Export() : crop;
        CapturedDocumentJson = annotated ? _focusSurface.ExportDocument() : "";
        Action = action; DialogResult = true;
    }
    private void Cancel() { Action = CaptureAction.None; CapturedImage = null; OriginalCapturedImage = null; CapturedDocumentJson = ""; DialogResult = false; }
    internal void UseSelectionOnly(string label)
    {
        _selectionOnly = true; _actionPanel.Visibility = Visibility.Collapsed;
        _actionDivider.Visibility = Visibility.Collapsed;
        _actionButtons[CaptureAction.Edit].Visibility = Visibility.Collapsed;
        var copy = _actionButtons[CaptureAction.Copy];
        ((TextBlock)((StackPanel)copy.Content).Children[1]).Text = label;
        copy.ToolTip = "Enter 或双击选区：" + label;
        System.Windows.Automation.AutomationProperties.SetName(copy, label);
    }
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
