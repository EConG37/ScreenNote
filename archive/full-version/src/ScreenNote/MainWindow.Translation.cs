using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenNote;

public partial class MainWindow
{
    private async Task<string> ReadTranslationTextAsync(IEnumerable<BitmapSource> images, string engine, CancellationToken token)
    {
        var texts = new List<string>();
        foreach (var image in images)
        {
            token.ThrowIfCancellationRequested();
            var result = await _ocr.RecognizeAsync(image, token, engine);
            if (result.Error != null) throw new InvalidOperationException(result.Error);
            if (!string.IsNullOrWhiteSpace(result.Text)) texts.Add(result.Text);
        }
        return string.Join("\n\n", texts);
    }
    private void ShowCaptureTranslation(BitmapSource image)
    {
        var engine = _settings.OcrEngine;
        var window = new TranslationWindow(readSource: token => ReadTranslationTextAsync([image], engine, token));
        window.Show();
    }
    private void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_changingStorage || _capturing || !SaveCurrentHistory()) return;
        try
        {
            if (HistoryPanel.Visibility == Visibility.Visible)
            {
                var selected = HistoryList.SelectedItems.Cast<HistoryEntry>().OrderBy(entry => entry.CreatedAt).ToArray();
                if (selected.Length == 0) return;
                if (selected.Length > 10) { SetStatus("每次最多翻译 10 张截图，请分批选择。"); return; }
                // Snapshot documents now so another capture or deletion cannot change this request.
                var documents = selected.Select(entry => _history.Load(entry)).ToArray();
                var engine = _settings.OcrEngine;
                var window = new TranslationWindow(readSource: async token =>
                {
                    var texts = new List<string>();
                    foreach (var document in documents)
                    {
                        token.ThrowIfCancellationRequested();
                        var text = document.Text;
                        if (string.IsNullOrWhiteSpace(text)) text = await ReadTranslationTextAsync([document.Rendered], engine, token);
                        if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
                    }
                    return string.Join("\n\n", texts);
                });
                window.Show();
            }
            else if (HasImage())
            {
                var text = OcrPanel.Visibility == Visibility.Visible && OcrText.SelectionLength > 0 ? OcrText.SelectedText : OcrText.Text;
                if (!string.IsNullOrWhiteSpace(text)) new TranslationWindow(text).Show();
                else ShowCaptureTranslation(Surface.Export());
            }
        }
        catch (Exception ex) { SetStatus("翻译未能开始，当前内容已保留：" + ex.Message); }
    }
}
