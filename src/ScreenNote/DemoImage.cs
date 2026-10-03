using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenNote;

internal static class DemoImage
{
    public static BitmapSource Create()
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 940, 680));
            draw.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(237, 246, 239)), null, new Rect(38, 36, 864, 104), 14, 14);
            Text(draw, "工作记录 / PROJECT NOTES", 66, 62, 14, "#18756B");
            Text(draw, "把截图变成可以继续编辑的内容", 66, 92, 27, "#253C3C");
            Text(draw, "今天的三个小目标", 58, 180, 24, "#253C3C");
            Text(draw, "01  截图后画箭头、加文字、圈出重点。", 62, 240, 21, "#536563");
            Text(draw, "02  识别文字后，直接修改错字和换行。", 62, 294, 21, "#536563");
            Text(draw, "03  整理好的内容，带到 AI 网页继续提问。", 62, 348, 21, "#536563");
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(233, 237, 230)), null, new Rect(58, 420, 824, 1));
            Text(draw, "ScreenNote keeps your OCR workflow on your device.", 62, 461, 20, "#253C3C");
            Text(draw, "Capture. Annotate. Edit. Ask.", 62, 505, 21, "#18756B");
            Text(draw, "这是一张用于界面与识别测试的示例图片。", 62, 604, 16, "#85938C");
        }
        var bitmap = new RenderTargetBitmap(940, 680, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    private static void Text(DrawingContext context, string text, double x, double y, double size, string color)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), 1);
        context.DrawText(formatted, new Point(x, y));
    }
}
