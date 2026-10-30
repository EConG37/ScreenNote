using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenNote;

public partial class MainWindow
{
    private ScrollCaptureWindow? _scrollCapture;
    private CollageWindow? _collageWindow;
    private bool ActivateCompositionWindow()
    {
        Window? window = _scrollCapture?.IsVisible == true ? _scrollCapture : _collageWindow?.IsVisible == true ? _collageWindow : null;
        if (window == null) return false;
        window.Activate(); return true;
    }

    private void Collage_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing || _changingStorage || !SaveCurrentHistory()) return;
        if (_collageWindow != null) { _collageWindow.Activate(); return; }
        try
        {
            var images = HistoryList.SelectedItems.Cast<HistoryEntry>().OrderBy(entry => entry.CreatedAt).Take(20)
                .Select(entry => new CollageFrame(_history.Load(entry).Rendered, entry.Title)).ToList();
            if (images.Count == 0 && Surface.Source != null && EditorPanel.Visibility == Visibility.Visible)
                images.Add(new CollageFrame(Surface.Export(), "当前图片"));
            var collage = new CollageWindow(images, CaptureForCollageAsync) { Owner = this };
            _collageWindow = collage;
            collage.Closed += (_, _) => { if (ReferenceEquals(_collageWindow, collage)) _collageWindow = null; };
            collage.EditRequested += (_, _) =>
            {
                try { if (collage.ResultImage is { } result) { LoadImage(result, "拼图"); ShowWorkspace(); SetStatus("拼图已保留，可继续标注或复制分享。"); } }
                catch (Exception ex) { SetStatus("拼图已生成，进入编辑未完成：" + ex.Message); }
            };
            collage.Show();
        }
        catch (Exception ex) { _collageWindow = null; SetStatus("拼图未完成：" + ex.Message); }
    }

    private async Task<BitmapSource?> CaptureForCollageAsync()
    {
        if (_capturing || _changingStorage) return null;
        if (!SaveCurrentHistory()) return null;
        _capturing = true; RefreshHistoryActions(); Hide();
        try
        {
            await Task.Delay(180);
            var selector = new CaptureWindow(); selector.UseSelectionOnly("加入拼图");
            if (selector.ShowDialog() != true || selector.CapturedImage == null) return null;
            var entry = _history.Create(selector.CapturedImage, "拼图素材");
            _historyEntries.Insert(0, entry); RefreshHistorySummary();
            return selector.CapturedImage;
        }
        finally { _capturing = false; RefreshHistoryActions(); }
    }

    private async Task CaptureScrollAsync(System.Drawing.Rectangle region, bool wasVisible)
    {
        _scrollCapture = new ScrollCaptureWindow(region);
        try
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _scrollCapture.Closed += (_, _) => closed.TrySetResult();
            _scrollCapture.Show();
            await closed.Task;
            if (_scrollCapture.CapturedImage is { } image)
            {
                LoadImage(image, "滚动截图"); ShowWorkspace();
                try { Clipboard.SetImage(image); SetStatus("长图已拼接并复制，已保留在截图历史中。"); }
                catch (Exception ex) { SetStatus("长图已保留，复制未完成：" + ex.Message); }
            }
            else if (wasVisible) ShowWorkspace();
        }
        finally { _scrollCapture = null; }
    }
}
