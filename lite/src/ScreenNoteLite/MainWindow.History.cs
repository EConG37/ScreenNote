using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ScreenNote;

public partial class MainWindow
{
    // The lite edition has no text workbench. These fields only carry OCR text that a
    // full-edition history record already contains, so opening and re-saving that record
    // cannot erase it.
    private string _legacyText = "";
    private string _legacyOriginalText = "";
    private string _legacyLastRecognized = "";
    private string CurrentTarget => (EditorTargetChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? "deepseek";
    internal void RestoreUpgradeBackup(string directory)
    {
        // One-time migration from the previous running editor. The old program can only
        // export a flattened PNG; all future captures retain their original annotation objects.
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(Path.Combine(Path.GetFullPath(directory), "image.png"));
            bitmap.EndInit(); bitmap.Freeze();
            var bitmapBytes = EncodePng(bitmap);
            var unfinished = _historyEntries.FirstOrDefault(entry =>
            {
                if (entry.Origin != "升级前编辑") return false;
                var saved = _history.Load(entry);
                return saved.Text.Length == 0 && saved.DocumentJson.Length == 0
                    && EncodePng(saved.Source).SequenceEqual(bitmapBytes);
            });
            if (unfinished == null) LoadImage(bitmap, "升级前编辑");
            else OpenHistory(unfinished);
            var textFile = Path.Combine(directory, "text.txt");
            if (File.Exists(textFile)) _legacyText = File.ReadAllText(textFile);
            if (SaveCurrentHistory()) { ShowHistory(); SetStatus("升级前的图片已保留在历史中。"); }
        }
        catch (Exception ex)
        {
            HistoryResult.Text = "旧编辑备份仍在原文件夹中，导入未完成：" + ex.Message;
            SetStatus(HistoryResult.Text);
            try { File.WriteAllText(Path.Combine(directory, "import-error.txt"), ex.ToString()); } catch { }
        }
    }
    private void CancelPendingWork()
    {
        _documentGeneration++;
    }
    private void MarkHistoryDirty()
    {
        if (_switchingDocument || !_ready || _currentHistory == null) return;
        _historyDirty = true; _autoSaveTimer.Stop(); _autoSaveTimer.Start();
    }
    internal bool SaveCurrentHistory()
    {
        _autoSaveTimer.Stop();
        if (!_historyDirty || _switchingDocument || _currentHistory == null || Surface.Source == null) return true;
        try
        {
            _history.Update(_currentHistory, Surface.Export(), Surface.ExportDocument(), _legacyText, _legacyOriginalText, _legacyLastRecognized);
            _historyDirty = false;
            return true;
        }
        catch (Exception ex) { SetStatus("历史保存未完成，当前编辑仍在窗口中：" + ex.Message); return false; }
    }
    private void RefreshHistorySummary()
    {
        HistoryCount.Text = $"{_historyEntries.Count} 张截图 · 自动保存";
        EmptyHistoryPanel.Visibility = _historyEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    internal void ShowHistory()
    {
        if (_changingStorage) return;
        if (!SaveCurrentHistory()) return;
        HistoryPanel.Visibility = Visibility.Visible; EditorPanel.Visibility = Visibility.Collapsed;
        ModeHint.Text = "截图历史"; Title = "拾屏 精简版 · 截图历史";
        WindowState = WindowState.Normal; Width = 1080; Height = 780; ConstrainToCurrentScreen();
        RefreshHistorySummary(); HistoryButton.IsEnabled = false;
        RefreshPrimaryActions();
    }
    internal void ShowEditor()
    {
        HistoryPanel.Visibility = Visibility.Collapsed; EditorPanel.Visibility = Visibility.Visible;
        ModeHint.Text = "编辑截屏"; Title = "拾屏 精简版 · 截图编辑"; HistoryButton.IsEnabled = true;
        if (WindowState != WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            Width = 1360; Height = 920; ConstrainToCurrentScreen();
        }
        UpdateLayout(); UpdateZoom();
        RefreshPrimaryActions();
    }
    private void ConstrainToCurrentScreen()
    {
        var window = new System.Windows.Interop.WindowInteropHelper(this);
        var screen = window.Handle != 0 ? System.Windows.Forms.Screen.FromHandle(window.Handle) : System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var area = screen.WorkingArea;
        Width = Math.Min(Width, area.Width / dpi.DpiScaleX - 16); Height = Math.Min(Height, area.Height / dpi.DpiScaleY - 16);
        Left = area.Left / dpi.DpiScaleX + (area.Width / dpi.DpiScaleX - Width) / 2;
        Top = area.Top / dpi.DpiScaleY + (area.Height / dpi.DpiScaleY - Height) / 2;
    }
    internal void OpenHistory(HistoryEntry entry)
    {
        if (!SaveCurrentHistory()) return;
        try
        {
            var document = _history.Load(entry);
            CancelPendingWork(); _switchingDocument = true;
            try
            {
                Surface.RestoreDocument(document.Source, document.DocumentJson); Surface.ClearSelection(); SelectTool(AnnotationTool.Select);
                _currentHistory = entry;
                _legacyText = document.Text; _legacyOriginalText = document.OriginalOcr; _legacyLastRecognized = document.LastRecognized;
                _historyDirty = false;
                WelcomePanel.Visibility = Visibility.Collapsed;
                ImageInfo.Text = $"{document.Source.PixelWidth} × {document.Source.PixelHeight} px";
            }
            finally { _switchingDocument = false; }
            ShowEditor(); ShowWorkspace(); SetStatus("已打开历史截图，标注仍可继续修改。 ");
        }
        catch (Exception ex) { SetStatus("这条历史无法打开：" + ex.Message); }
    }
    private void History_Click(object sender, RoutedEventArgs e) { ShowHistory(); SetStatus("勾选截图进行发送；双击继续编辑。"); }
    private void FinishEditing_Click(object sender, RoutedEventArgs e) { if (SaveCurrentHistory()) Hide(); }
    private void EditHistory_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: HistoryEntry entry }) OpenHistory(entry); }
    private void DeleteHistoryEntry_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: HistoryEntry entry }) ConfirmDeleteHistory([entry]);
    }
    private void DeleteSelectedHistory_Click(object sender, RoutedEventArgs e) =>
        ConfirmDeleteHistory(HistoryList.SelectedItems.Cast<HistoryEntry>().ToArray());
    private void ConfirmDeleteHistory(IReadOnlyList<HistoryEntry> entries)
    {
        if (entries.Count == 0 || HistoryOperationBusy()) return;
        var text = entries.Count == 1 ? "删除这张截图？" : $"删除选中的 {entries.Count} 张截图？";
        text += "\n原图和标注会一起删除，无法撤销。已导出的图片和原文件夹备份不受影响。";
        if (MessageBox.Show(this, text, "删除截图", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) DeleteHistoryEntries(entries);
    }
    private bool HistoryOperationBusy()
    {
        if (!_sending && !_changingStorage && !_capturing) return false;
        HistoryResult.Text = "当前操作尚未结束，请完成后再删除截图。"; SetStatus(HistoryResult.Text); return true;
    }
    internal int DeleteHistoryEntries(IReadOnlyList<HistoryEntry> entries)
    {
        if (entries.Count == 0 || HistoryOperationBusy()) return 0;
        var deleted = 0;
        var failures = new List<string>();
        foreach (var entry in entries.DistinctBy(item => (item.StorageRoot, item.Id)))
        {
            try
            {
                _history.Delete(entry);
                if (_currentHistory?.Id == entry.Id && _currentHistory.StorageRoot == entry.StorageRoot) ClearDeletedDocument();
                _historyEntries.Remove(entry); deleted++;
            }
            catch (Exception ex) { failures.Add(entry.Title + "：" + ex.Message); }
        }
        RefreshHistorySummary(); RefreshHistoryActions();
        HistoryResult.Text = failures.Count == 0 ? $"已删除 {deleted} 张截图。"
            : $"已删除 {deleted} 张截图，{failures.Count} 张删除失败，仍保留在历史中。\n" + string.Join("\n", failures.Take(3));
        SetStatus(HistoryResult.Text);
        return deleted;
    }
    private void ClearDeletedDocument()
    {
        CancelPendingWork(); _autoSaveTimer.Stop(); _switchingDocument = true;
        try
        {
            _currentHistory = null; _historyDirty = false;
            Surface.Source = null; Surface.ClearSelection(); SelectTool(AnnotationTool.Select);
            _legacyText = ""; _legacyOriginalText = ""; _legacyLastRecognized = "";
            WelcomePanel.Visibility = Visibility.Visible;
            ImageInfo.Text = "准备就绪";
        }
        finally { _switchingDocument = false; }
        ShowHistory();
    }
    private void HistoryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var dependency = e.OriginalSource as DependencyObject;
        for (var current = dependency; current != null && current != HistoryList;)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase) return;
            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        if (dependency != null && ItemsControl.ContainerFromElement(HistoryList, dependency) is ListBoxItem { Content: HistoryEntry entry }) OpenHistory(entry);
    }
    private void HistorySelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HistorySendButton == null) return;
        var count = HistoryList.SelectedItems.Count;
        HistorySelectionCount.Text = count == 0 ? "勾选图片后批量处理" : $"已选 {count} 张";
        RefreshHistoryActions();
    }
    private void RefreshHistoryActions()
    {
        var available = HistoryList.SelectedItems.Count > 0 && !_sending && !_changingStorage && !_capturing;
        HistorySendButton.IsEnabled = available; HistoryExportButton.IsEnabled = available;
        HistoryDeleteButton.IsEnabled = available;
        CollageButton.IsEnabled = !_changingStorage && !_capturing;
        RefreshPrimaryActions();
    }
    private void SelectAllHistory_Click(object sender, RoutedEventArgs e) => HistoryList.SelectAll();
    private void ClearHistorySelection_Click(object sender, RoutedEventArgs e) => HistoryList.UnselectAll();
    private void AiTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingTarget) return;
        _syncingTarget = true;
        try { if (sender == HistoryTargetChoice) EditorTargetChoice.SelectedIndex = HistoryTargetChoice.SelectedIndex; else HistoryTargetChoice.SelectedIndex = EditorTargetChoice.SelectedIndex; }
        finally { _syncingTarget = false; }
        _settings.AiTarget = CurrentTarget;
        if (!App.IsTestMode) { try { SettingsService.Save(_settings); } catch { } }
    }
    private byte[][] SelectedHistoryImages()
    {
        if (!SaveCurrentHistory()) throw new IOException("当前编辑尚未保存，请重试保存后再提交。");
        var selected = HistoryList.SelectedItems.Cast<HistoryEntry>().OrderBy(entry => entry.CreatedAt).ToArray();
        return selected.Select(entry => _history.ReadRenderedPng(entry)).ToArray();
    }
    private async void HistoryBatchSend_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || HistoryList.SelectedItems.Count == 0) return;
        if (HistoryList.SelectedItems.Count > AiBridgeService.MaximumImageCount) { HistoryResult.Text = $"每次最多提交 {AiBridgeService.MaximumImageCount} 张，请减少选择。"; return; }
        var target = CurrentTarget;
        var generation = _documentGeneration;
        _sending = true; RefreshHistoryActions();
        SetStatus("正在准备所选截图…");
        try
        {
            if (!SaveCurrentHistory()) throw new IOException("当前编辑尚未保存，请重试保存后再提交。");
            var selected = HistoryList.SelectedItems.Cast<HistoryEntry>().OrderBy(entry => entry.CreatedAt).ToArray();
            var snapshots = await System.Threading.Tasks.Task.Run(() =>
            {
                long remaining = AiBridgeService.MaximumImageBytes;
                return selected.Select(entry => { var png = _history.ReadRenderedPng(entry, remaining); remaining -= png.Length; return png; }).ToArray();
            });
            SetStatus($"正在把 {snapshots.Length} 张图片带到 {AiBridgeService.TargetName(target)}…");
            var result = await _browserPaste.SendAsync(snapshots, target);
            HistoryResult.Text = result.Message;
            if (generation == _documentGeneration) SetStatus(result.Message);
        }
        catch (Exception ex) { HistoryResult.Text = "图片准备未完成：" + ex.Message; }
        finally { _sending = false; RefreshHistoryActions(); }
    }
    private async System.Threading.Tasks.Task SendImageBatchAsync(IReadOnlyList<byte[]> images, string target)
    {
        if (_sending) return;
        var generation = _documentGeneration;
        _sending = true; RefreshHistoryActions();
        SetStatus($"正在把 {images.Count} 张图片带到 {AiBridgeService.TargetName(target)}…");
        try
        {
            var result = await _browserPaste.SendAsync(images, target);
            HistoryResult.Text = result.Message;
            if (generation == _documentGeneration) SetStatus(result.Message);
        }
        catch (Exception ex) { HistoryResult.Text = "图片传递未完成：" + ex.Message; if (generation == _documentGeneration) SetStatus(HistoryResult.Text); }
        finally { _sending = false; RefreshHistoryActions(); }
    }
    private void HistoryExport_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItems.Count == 0) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择保存截图的文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var images = SelectedHistoryImages();
            var folder = Path.Combine(dialog.FolderName, "拾屏截图-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(folder);
            for (var index = 0; index < images.Length; index++) File.WriteAllBytes(Path.Combine(folder, $"截图-{index + 1:00}.png"), images[index]);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = true });
            HistoryResult.Text = $"已导出 {images.Length} 张独立图片，可一起拖入其他 AI 或聊天软件。";
        }
        catch (Exception ex) { HistoryResult.Text = "导出未完成：" + ex.Message; }
    }
    private static byte[] EncodePng(BitmapSource bitmap)
    {
        using var stream = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); return stream.ToArray();
    }
}
