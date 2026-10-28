using System.Linq;
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
}
