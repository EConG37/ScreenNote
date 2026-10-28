using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenNote;

public sealed partial class CaptureWindow
{
    private readonly Border _editTools = new() { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(221, 223, 227)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(6), Visibility = Visibility.Collapsed };
    private readonly Dictionary<AnnotationTool, Button> _editToolButtons = new();
    private void InitializeEditTools()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void Add(string label, string icon, Action action)
        {
            var button = CreateAction(label, icon, label);
            ((StackPanel)button.Content).Children[1].Visibility = Visibility.Collapsed;
            ((ToolGlyph)((StackPanel)button.Content).Children[0]).Margin = new Thickness(0);
            button.Padding = new Thickness(8, 0, 8, 0);
            button.Click += (_, e) => { action(); e.Handled = true; };
            row.Children.Add(button);
        }
        Add("返回框选（V）", "capture", () => SetCaptureTool(null));
        foreach (var (tool, label, icon) in new[] {
            (AnnotationTool.Select, "选择标注", "select"), (AnnotationTool.Pen, "画笔（P）", "pen"),
            (AnnotationTool.Arrow, "箭头（A）", "arrow"), (AnnotationTool.Line, "直线（L）", "line"),
            (AnnotationTool.Rectangle, "矩形（R）", "rect"), (AnnotationTool.Ellipse, "椭圆（E）", "ellipse"),
            (AnnotationTool.Text, "文字（T）", "text"), (AnnotationTool.Number, "序号（N）", "number"),
            (AnnotationTool.Mosaic, "马赛克（M）", "mosaic"), (AnnotationTool.Highlighter, "荧光笔（H）", "highlighter") })
        {
            Add(label, icon, () => SetCaptureTool(tool));
            _editToolButtons.Add(tool, (Button)row.Children[^1]);
        }
        row.Children.Add(new Border { Width = 1, Height = 20, Background = Brushes.LightGray, Margin = new Thickness(6, 0, 6, 0) });
        Add("撤销（Ctrl+Z）", "undo", _focusSurface.Undo);
        Add("重做（Ctrl+Y）", "redo", _focusSurface.Redo);
        var color = new ComboBox { Width = 72, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "标注颜色" };
        foreach (var (name, ink) in new[] { ("红色", Colors.Crimson), ("黄色", Colors.Gold), ("绿色", Colors.SeaGreen), ("蓝色", Colors.RoyalBlue), ("黑色", Colors.Black), ("白色", Colors.White) })
            color.Items.Add(new ComboBoxItem { Content = name, Tag = ink });
        color.SelectedIndex = 0; _focusSurface.InkColor = Colors.Crimson;
        color.SelectionChanged += (_, _) => { if (color.SelectedItem is ComboBoxItem { Tag: Color ink }) _focusSurface.InkColor = ink; };
        row.Children.Add(color);
        var width = new ComboBox { Width = 68, VerticalAlignment = VerticalAlignment.Center, ToolTip = "线条粗细" };
        foreach (var size in new[] { 2, 4, 8, 12 }) width.Items.Add(new ComboBoxItem { Content = size + " px", Tag = size });
        width.SelectedIndex = 1; _focusSurface.StrokeWidth = 4;
        width.SelectionChanged += (_, _) => { if (width.SelectedItem is ComboBoxItem { Tag: int size }) _focusSurface.StrokeWidth = size; };
        row.Children.Add(width);
        _editTools.Child = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _canvas.Children.Add(_editTools);
    }
    private void PositionEditTools(bool visible)
    {
        _editTools.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        _editTools.MaxWidth = Math.Max(100, _canvas.ActualWidth - 16);
        _editTools.Measure(new Size(_editTools.MaxWidth, double.PositiveInfinity));
        var size = _editTools.DesiredSize;
        var top = _rect.Top - size.Height - 10;
        // Keep the bar accessible when the selection touches the screen edge.
        if (top < 8) top = Math.Min(_rect.Top + 10, Math.Max(8, _canvas.ActualHeight - size.Height - 8));
        var left = Math.Clamp(_rect.Right - size.Width, 8, Math.Max(8, _canvas.ActualWidth - size.Width - 8));
        if (_actions.Visibility == Visibility.Visible)
        {
            var actions = new Rect(Canvas.GetLeft(_actions), Canvas.GetTop(_actions), _actions.DesiredSize.Width, _actions.DesiredSize.Height);
            if (new Rect(left, top, size.Width, size.Height).IntersectsWith(actions))
                top = actions.Top - size.Height - 8 >= 8 ? actions.Top - size.Height - 8 : Math.Min(actions.Bottom + 8, Math.Max(8, _canvas.ActualHeight - size.Height - 8));
        }
        Canvas.SetLeft(_editTools, left);
        Canvas.SetTop(_editTools, top);
        Canvas.SetTop(_label, Math.Max(0, top - 36));
    }
    private static bool CaptureToolKey(Key key, out AnnotationTool? tool)
    {
        tool = key switch { Key.P => AnnotationTool.Pen, Key.A => AnnotationTool.Arrow, Key.L => AnnotationTool.Line, Key.R => AnnotationTool.Rectangle, Key.E => AnnotationTool.Ellipse, Key.T => AnnotationTool.Text, Key.N => AnnotationTool.Number, Key.M => AnnotationTool.Mosaic, Key.H => AnnotationTool.Highlighter, _ => null };
        return tool.HasValue || key == Key.V;
    }
}
