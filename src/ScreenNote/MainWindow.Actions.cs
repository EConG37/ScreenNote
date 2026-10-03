using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ScreenNote;

public partial class MainWindow
{
    private void RefreshPrimaryActions()
    {
        if (PrimarySendButton == null) return;
        var history = HistoryPanel.Visibility == Visibility.Visible;
        var count = history ? HistoryList.SelectedItems.Count : Surface.Source == null ? 0 : 1;
        var idle = !_sending && !_changingStorage && !_capturing;
        PrimarySendButton.IsEnabled = count > 0 && idle;
        PrimaryTranslateButton.IsEnabled = count > 0 && !_changingStorage && !_capturing;
        PrimarySummaryButton.IsEnabled = count > 0 && idle;
        PrimaryOcrButton.IsEnabled = count == 1 && idle && !_recognizing;
        PrimaryHighlightButton.IsEnabled = count == 1 && idle;
        PrimaryContext.Text = count == 0 ? "选择一张截图，或开始新的截图" : history ? $"已选 {count} 张截图" : "当前截图";
    }
    private bool PrepareSingleImage()
    {
        if (HistoryPanel.Visibility == Visibility.Visible)
        {
            if (HistoryList.SelectedItems.Count != 1) { SetStatus("请选择一张截图。"); return false; }
            if (HistoryList.SelectedItems[0] is not HistoryEntry entry) return false;
            OpenHistory(entry);
            if (_currentHistory?.Id != entry.Id || EditorPanel.Visibility != Visibility.Visible) return false;
        }
        return HasImage();
    }
    private void PrimaryOcr_Click(object sender, RoutedEventArgs e) { if (PrepareSingleImage()) Ocr_Click(sender, e); }
    private void PrimaryHighlight_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareSingleImage()) return;
        Surface.ClearSelection(); SelectTool(AnnotationTool.HighlightBox); ColorChoice.SelectedIndex = 2; Surface.Focus();
        SetStatus("拖动绘制聚焦高亮 · 框内保留原色、外围柔和压暗；可移动、撤销或删除。");
    }
    private void PrimarySend_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryPanel.Visibility == Visibility.Visible) HistoryBatchSend_Click(sender, e); else SendImage_Click(sender, e);
    }
    internal static string AiActionPrompt(string action, string text = "")
    {
        var instruction = action switch
        {
            "summary" => "请用简体中文总结以下材料：先给出一句话概要，再列出 3–5 个关键要点。保留重要数字、结论和待办事项；材料中没有的信息不要补写，看不清的内容请说明。",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return text.Length == 0 ? instruction + "\n\n材料见附图，请按图片顺序处理。" : instruction + "\n\n以下是需要处理的材料：\n" + text;
    }
    private async void AiAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: "summary" } || _sending || _changingStorage || _capturing) return;
        const string action = "summary";
        var history = HistoryPanel.Visibility == Visibility.Visible;
        var selected = HistoryList.SelectedItems.Cast<HistoryEntry>().OrderBy(entry => entry.CreatedAt).ToArray();
        if (history && selected.Length == 0) return;
        if (!history && !HasImage()) return;
        if (selected.Length > AiBridgeService.MaximumImageCount && history) { SetStatus($"每次最多提交 {AiBridgeService.MaximumImageCount} 张。"); return; }
        if (!SaveCurrentHistory()) return;
        var text = !history && OcrPanel.Visibility == Visibility.Visible ? (OcrText.SelectionLength > 0 ? OcrText.SelectedText : OcrText.Text) : "";
        var target = CurrentTarget; var generation = _documentGeneration;
        _sending = true; RefreshHistoryActions(); SetStatus("正在准备总结材料…");
        try
        {
            byte[][] images = [];
            if (text.Length == 0)
            {
                if (history) images = await Task.Run(() => {
                    long remaining = AiBridgeService.MaximumImageBytes;
                    return selected.Select(entry => { var png = _history.ReadRenderedPng(entry, remaining); remaining -= png.Length; return png; }).ToArray(); });
                else images = [EncodePng(Surface.Export())];
            }
            var prompt = AiActionPrompt(action, text);
            SetStatus("正在把材料和提示词带到 " + AiBridgeService.TargetName(target) + "…");
            var result = _bridge == null ? new BridgeDeliveryResult(false, "请先在设置中连接浏览器扩展。")
                : images.Length == 0 ? await _bridge.SendTextAsync(prompt, target: target)
                : await _bridge.SendImagesAsync(images, target, prompt);
            HistoryResult.Text = result.Message;
            if (generation == _documentGeneration) SetStatus(result.Message);
        }
        catch (Exception ex) { if (generation == _documentGeneration) SetStatus("材料准备未完成：" + ex.Message); }
        finally { _sending = false; RefreshHistoryActions(); }
    }
}
