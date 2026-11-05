using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ScreenNote;

/// <summary>Presentation helpers for the small, code-created tool windows.</summary>
internal static partial class StudioUi
{
    internal static Brush Brush(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(key switch
        {
            "Ink" => "#29334E", "Muted" => "#65708B", "Accent" => "#5965F2",
            "Line" => "#DAE1F1", "Surface" => "#F9FBFF", "AccentSoft" => "#E8ECFF", _ => "#F0F4FF"
        }));
    }

    internal static Border Panel(UIElement content, Thickness padding)
    {
        var panel = new Border { Padding = padding, Child = content };
        if (Application.Current?.TryFindResource("StudioPanel") is Style style) panel.Style = style;
        else
        {
            panel.CornerRadius = new CornerRadius(16); panel.Background = Brush("Surface");
            panel.BorderBrush = Brush("Line"); panel.BorderThickness = new Thickness(1);
        }
        return panel;
    }

    internal static StackPanel Label(string kind, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new ToolGlyph { Kind = kind, Width = 18 * InterfaceScale, Height = 18 * InterfaceScale, Margin = new Thickness(0, 0, 7 * InterfaceScale, 0), VerticalAlignment = VerticalAlignment.Center };
        glyph.SetValue(CompactedProperty, true);
        glyph.SetBinding(Control.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        panel.Children.Add(glyph);
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    internal static void Label(Button button, string kind, string text)
    {
        button.Content = Label(kind, text);
        AutomationProperties.SetName(button, text);
    }

    internal static string ActionIcon(string title) => title switch
    {
        "继续截图" => "capture", "添加图片" or "打开文件夹" or "选择文件夹" or "打开当前文件夹" => "folder",
        "复制拼图" or "复制 GIF" or "复制视频" or "停止并复制" or "完成并复制" => "copy",
        "保存拼图" or "另存为" or "保存设置" or "停止并保存" => "save",
        "完成并编辑" => "pen", "放大查看" or "适应窗口" => "focus",
        "上移" or "下移" => "arrow", "移除" or "关闭" or "取消" or "放弃" or "取消倒计时" => "close",
        "查看动画" or "播放视频" or "开始录制" => "play", "暂停" => "pause", "继续" => "play",
        "压缩 GIF" or "压缩已有 GIF" or "停止压缩" => "trim",
        "开始采集" => "scroll", "返回（Esc）" => "back", "确定" or "检测占用" => "check",
        _ => "settings"
    };
}
