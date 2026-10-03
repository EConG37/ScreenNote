using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenNote;

public partial class MainWindow
{
    private async Task HandleCaptureActionAsync(CaptureWindow selector, bool wasVisible)
    {
        var image = selector.CapturedImage!;
        var source = selector.OriginalCapturedImage ?? image;
        var document = selector.CapturedDocumentJson;
        if (selector.Action is CaptureAction.Edit or CaptureAction.Ocr)
        {
            LoadImage(source);
            if (document.Length > 0) { Surface.RestoreDocument(source, document); SelectTool(AnnotationTool.Select); MarkHistoryDirty(); }
            if (!SaveCurrentHistory()) throw new InvalidOperationException("截图已保留，标注保存未完成。");
            ShowWorkspace();
            if (selector.Action == CaptureAction.Ocr) { ShowEditor(true); await RecognizeAsync(true); }
            else SetStatus("截图与高亮已保留，开始编辑。");
            return;
        }
        if (selector.Action == CaptureAction.Copy && document.Length == 0)
        { CompleteCapture(image, CaptureAction.Copy); return; }

        var entry = _history.Create(source);
        _historyEntries.Insert(0, entry); RefreshHistorySummary();
        if (document.Length > 0) _history.Update(entry, image, document, "", "", "");
        if (selector.Action == CaptureAction.Copy)
        {
            try { Clipboard.SetImage(image); SetStatus("截图与高亮已复制，历史已保留。"); }
            catch (Exception ex) { ShowHistory(); ShowWorkspace(); SetStatus("截图已保留，复制未完成：" + ex.Message); }
            return;
        }
        if (selector.Action == CaptureAction.Translate) { ShowCaptureTranslation(image); return; }
        var target = selector.Action == CaptureAction.SendAi ? selector.SelectedAiTarget : CurrentTarget;
        if (selector.Action == CaptureAction.SendAi) EditorTargetChoice.SelectedIndex = target switch { "chatgpt" => 1, "doubao" => 2, _ => 0 };
        await DeliverCaptureAsync(image, selector.Action, target);
    }

    private async Task DeliverCaptureAsync(BitmapSource image, CaptureAction action, string target)
    {
        var name = AiBridgeService.TargetName(target);
        var description = new TextBlock { Text = "正在打开 " + name + " 并准备截图…", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(83, 88, 96)), Margin = new Thickness(0, 12, 0, 20), LineHeight = 23 };
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "截图已保留", FontSize = 20, FontWeight = FontWeights.SemiBold });
        content.Children.Add(description);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = "复制截图", Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "关闭" };
        buttons.Children.Add(copy); buttons.Children.Add(close); content.Children.Add(buttons);
        var feedback = new Window { Title = "拾屏 · " + name, Width = 430, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Brushes.White, Content = content };
        copy.Click += (_, _) => { try { Clipboard.SetImage(image); description.Text = "截图已复制，可在 AI 网页中直接粘贴。"; } catch (Exception ex) { description.Text = "复制未完成：" + ex.Message; } };
        close.Click += (_, _) => feedback.Close();
        _sending = true; RefreshHistoryActions();
        feedback.Show();
        var progress = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        progress.Tick += (_, _) => { if (_bridge?.Status.StartsWith("正在", StringComparison.Ordinal) == true) description.Text = _bridge.Status; };
        progress.Start();
        try
        {
            var prompt = action == CaptureAction.Summary ? AiActionPrompt("summary") : "";
            var result = _bridge == null ? new BridgeDeliveryResult(false, "请先在设置中连接浏览器扩展。截图已保留，可复制后粘贴。")
                : await _bridge.SendImagesAsync([EncodePng(image)], target, prompt);
            progress.Stop(); description.Text = result.Message; SetStatus(result.Message);
        }
        catch (Exception ex) { description.Text = "截图已保留，网页传递未完成：" + ex.Message; SetStatus(description.Text); }
        finally { progress.Stop(); _sending = false; RefreshHistoryActions(); }
    }
}
