using System;
using System.Threading.Tasks;
using System.Windows;

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
        var target = selector.SelectedAiTarget;
        EditorTargetChoice.SelectedIndex = target switch { "chatgpt" => 1, "doubao" => 2, _ => 0 };
        await SendImageBatchAsync([EncodePng(image)], target);
    }
}
