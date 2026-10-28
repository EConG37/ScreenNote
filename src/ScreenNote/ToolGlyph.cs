using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenNote;

/// <summary>Shared vector icons remain sharp at every Windows display scale.</summary>
public sealed class ToolGlyph : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(ToolGlyph), new FrameworkPropertyMetadata("capture", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public ToolGlyph() { Width = 20; Height = 20; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        var data = Kind switch
        {
            "capture" => "M7,2 L3,2 3,7 M13,2 L17,2 17,7 M3,13 L3,17 7,17 M17,13 L17,17 13,17 M6,7 L14,7 14,13 6,13 Z",
            "record" => "M10,3 A7,7 0 1 1 9.99,3 M8,7 L14,10 8,13 Z",
            "scroll" => "M4,3 L16,3 16,17 4,17 Z M10,6 L10,14 M7,11 L10,14 13,11",
            "collage" => "M2,3 L11,3 11,11 2,11 Z M7,12 L18,12 18,17 7,17 Z M13,5 L18,5 18,10 13,10 Z",
            "ocr" => "M3,6 L3,3 7,3 M13,3 L17,3 17,6 M3,14 L3,17 7,17 M13,17 L17,17 17,14 M6,7 L14,7 M10,7 L10,14 M8,14 L12,14",
            "focus" => "M7,3 L5,3 Q3,3 3,5 L3,7 M13,3 L15,3 Q17,3 17,5 L17,7 M3,13 L3,15 Q3,17 5,17 L7,17 M17,13 L17,15 Q17,17 15,17 L13,17 M7,10 L13,10",
            "translate" => "M2,5 L12,5 M7,2 L7,5 M10,5 Q10,11 3,14 M4,8 Q6,12 11,14 M12,17 L15,8 18,17 M13,14 L17,14",
            "send" => "M3,3 L17,10 3,17 6,10 Z M6,10 L17,10",
            "history" => "M3,8 A7,7 0 1 1 3,13 M3,3 L3,8 8,8 M10,6 L10,10 13,12",
            "import" => "M3,9 L3,17 17,17 17,9 M10,14 L10,2 M6,6 L10,2 14,6",
            "settings" => "M3,5 L17,5 M3,10 L17,10 M3,15 L17,15 M7,3 L7,7 M13,8 L13,12 M7,13 L7,17",
            "select" => "M4,2 L4,16 8,12 12,18 15,16 11,10 17,9 Z",
            "pen" => "M3,15 L3,18 6,18 17,7 14,4 Z M12,6 L15,9",
            "arrow" => "M3,17 L17,3 M9,3 L17,3 17,11",
            "line" => "M3,17 L17,3",
            "rect" => "M3,4 L17,4 17,16 3,16 Z",
            "ellipse" => "M10,3 A7,7 0 1 1 9.99,3",
            "text" => "M3,4 L17,4 M10,4 L10,17 M7,17 L13,17",
            "highlighter" => "M4,13 L12,3 17,7 9,17 Z M3,18 L10,18",
            "number" => "M10,2 A8,8 0 1 1 9.99,2 M8,7 L10,5 10,15 M8,15 L12,15",
            "mosaic" => "M3,3 L17,3 17,17 3,17 Z M3,8 L17,8 M3,12 L17,12 M8,3 L8,17 M12,3 L12,17",
            "cover" => "M3,4 L17,4 17,16 3,16 Z M5,6 L15,14 M5,14 L15,6",
            "copy" => "M7,7 L17,7 17,17 7,17 Z M13,7 L13,3 3,3 3,13 7,13",
            "close" => "M5,5 L15,15 M15,5 L5,15",
            "undo" => "M7,4 L3,8 7,12 M3,8 L12,8 Q17,8 17,13 L17,16",
            "redo" => "M13,4 L17,8 13,12 M17,8 L8,8 Q3,8 3,13 L3,16",
            "save" => "M3,3 L15,3 17,5 17,17 3,17 Z M6,3 L6,8 13,8 13,3 M6,17 L6,12 14,12 14,17",
            _ => "M4,10 L16,10"
        };
        dc.PushTransform(new ScaleTransform(ActualWidth / 20, ActualHeight / 20));
        dc.DrawGeometry(null, new Pen(Foreground, 1.45) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse(data));
        dc.Pop();
    }
}
