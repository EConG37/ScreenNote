using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenNote;

public sealed partial class CaptureWindow
{
    private readonly Border _editTools = new() { Background = new SolidColorBrush(Color.FromArgb(248, 249, 251, 255)), BorderBrush = new SolidColorBrush(Color.FromRgb(218, 225, 241)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(8), Visibility = Visibility.Collapsed };
    private readonly Dictionary<AnnotationTool, Button> _editToolButtons = new();
    private void InitializeEditTools()
    {
        var groups = new StackPanel { Orientation = Orientation.Horizontal };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var properties = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Add(Panel group, string label, string icon, Action action)
        {
            var shortLabel = label.Split('（')[0] switch { "返回框选" => "框选", "选择标注" => "选择", var value => value };
            var button = CreateAction(shortLabel, icon, label);
            button.Width = button.MinWidth = 36;
            button.Height = button.MinHeight = 36;
            button.Padding = new Thickness(4);
            button.Margin = new Thickness(1, 0, 1, 0);
            button.Content = new ToolGlyph { Kind = icon, Width = 20, Height = 20 };
            button.Click += (_, e) => { action(); e.Handled = true; };
            group.Children.Add(button);
            return button;
        }
        Add(tools, "返回框选（V）", "capture", () => SetCaptureTool(null));
        foreach (var (tool, label, icon) in new[] {
            (AnnotationTool.Select, "选择标注", "select"), (AnnotationTool.Pen, "画笔（P）", "pen"),
            (AnnotationTool.Arrow, "箭头（A）", "arrow"), (AnnotationTool.Line, "直线（L）", "line"),
            (AnnotationTool.Rectangle, "矩形（R）", "rect"), (AnnotationTool.Ellipse, "椭圆（E）", "ellipse"),
            (AnnotationTool.Text, "文字（T）", "text"), (AnnotationTool.Number, "序号（N）", "number"),
            (AnnotationTool.Mosaic, "马赛克（M）", "mosaic"), (AnnotationTool.Highlighter, "荧光笔（H）", "highlighter") })
        {
            _editToolButtons.Add(tool, Add(tools, label, icon, () => SetCaptureTool(tool)));
        }
        Add(properties, "撤销（Ctrl+Z）", "undo", _focusSurface.Undo);
        Add(properties, "重做（Ctrl+Y）", "redo", _focusSurface.Redo);
        var color = new ComboBox { Width = 70, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, ToolTip = "标注颜色" };
        System.Windows.Automation.AutomationProperties.SetName(color, "标注颜色");
        foreach (var (name, ink) in new[] { ("红色", Colors.Crimson), ("黄色", Colors.Gold), ("绿色", Colors.SeaGreen), ("蓝色", Colors.RoyalBlue), ("黑色", Colors.Black), ("白色", Colors.White) })
            color.Items.Add(new ComboBoxItem { Content = name, Tag = ink });
        color.SelectedIndex = 0; _focusSurface.InkColor = Colors.Crimson;
        color.SelectionChanged += (_, _) => { if (color.SelectedItem is ComboBoxItem { Tag: Color ink }) _focusSurface.InkColor = ink; };
        var colorGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        colorGroup.Children.Add(color); properties.Children.Add(colorGroup);
        var width = new ComboBox { Width = 68, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, ToolTip = "线条粗细" };
        System.Windows.Automation.AutomationProperties.SetName(width, "线条粗细");
        foreach (var size in new[] { 2, 4, 8, 12 }) width.Items.Add(new ComboBoxItem { Content = size + " px", Tag = size });
        width.SelectedIndex = 1; _focusSurface.StrokeWidth = 4;
        width.SelectionChanged += (_, _) => { if (width.SelectedItem is ComboBoxItem { Tag: int size }) _focusSurface.StrokeWidth = size; };
        var widthGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        widthGroup.Children.Add(width); properties.Children.Add(widthGroup);
        groups.Children.Add(tools);
        groups.Children.Add(new Border { Width = 1, Height = 24, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(218, 225, 241)), Margin = new Thickness(6, 0, 6, 0) });
        groups.Children.Add(properties);
        _editTools.Child = new ScrollViewer { Content = groups, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _canvas.Children.Add(_editTools);
    }
    private void PositionEditTools(bool visible)
    {
        _editTools.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        const double edge = 8;
        _editTools.MaxWidth = Math.Min(700 * StudioUi.InterfaceScale, Math.Max(100, _canvas.ActualWidth - edge * 2));
        _editTools.MaxHeight = Math.Max(100, _canvas.ActualHeight - edge * 2);
        // The finite width must reach the WrapPanels before DesiredSize is used.
        _editTools.Measure(new Size(_editTools.MaxWidth, _editTools.MaxHeight));
        var size = _editTools.DesiredSize;
        var maxLeft = Math.Max(edge, _canvas.ActualWidth - size.Width - edge);
        var maxTop = Math.Max(edge, _canvas.ActualHeight - size.Height - edge);
        var left = Math.Clamp(_rect.Right - size.Width, edge, maxLeft);
        var actions = _actions.Visibility == Visibility.Visible
            ? new Rect(Canvas.GetLeft(_actions), Canvas.GetTop(_actions), _actions.DesiredSize.Width, _actions.DesiredSize.Height)
            : Rect.Empty;
        var candidates = new List<double> { _rect.Top - size.Height - 10, _rect.Bottom + 10 };
        if (!actions.IsEmpty)
        {
            candidates.Add(actions.Bottom + edge);
            candidates.Add(actions.Top - size.Height - edge);
        }
        candidates.Add(edge); candidates.Add(maxTop);
        var top = Math.Clamp(_rect.Top + 10, edge, maxTop);
        foreach (var candidate in candidates)
        {
            if (candidate < edge || candidate > maxTop) continue;
            if (!actions.IsEmpty && new Rect(left, candidate, size.Width, size.Height).IntersectsWith(actions)) continue;
            top = candidate; break;
        }
        Canvas.SetLeft(_editTools, left);
        Canvas.SetTop(_editTools, top);
        _label.Measure(new Size(Math.Max(100, _canvas.ActualWidth - edge * 2), double.PositiveInfinity));
        var labelTop = Math.Clamp(_rect.Top - _label.DesiredSize.Height - 6, edge, Math.Max(edge, _canvas.ActualHeight - _label.DesiredSize.Height - edge));
        if (new Rect(Canvas.GetLeft(_label), labelTop, _label.DesiredSize.Width, _label.DesiredSize.Height).IntersectsWith(new Rect(left, top, size.Width, size.Height)))
            labelTop = top - _label.DesiredSize.Height - 6 >= edge ? top - _label.DesiredSize.Height - 6 : Math.Min(top + size.Height + 6, _canvas.ActualHeight - _label.DesiredSize.Height - edge);
        Canvas.SetTop(_label, Math.Max(edge, labelTop));
    }
    private static bool CaptureToolKey(Key key, out AnnotationTool? tool)
    {
        tool = key switch { Key.P => AnnotationTool.Pen, Key.A => AnnotationTool.Arrow, Key.L => AnnotationTool.Line, Key.R => AnnotationTool.Rectangle, Key.E => AnnotationTool.Ellipse, Key.T => AnnotationTool.Text, Key.N => AnnotationTool.Number, Key.M => AnnotationTool.Mosaic, Key.H => AnnotationTool.Highlighter, _ => null };
        return tool.HasValue || key == Key.V;
    }
}
