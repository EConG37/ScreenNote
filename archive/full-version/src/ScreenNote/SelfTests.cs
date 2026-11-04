using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace ScreenNote;

internal static class SelfTests
{
    public static async Task<Dictionary<string, object>> RunAsync(MainWindow window)
    {
        var passed = new List<string>();
        void Check(bool success, string name) { if (!success) throw new InvalidOperationException("Self-test failed: " + name); passed.Add(name); }
        AnnotationSurface.VerifyRenderingAndHistory(); passed.Add("Annotation export, mosaic layers, undo/redo");
        var desktopCapture = new CaptureWindow();
        Check(desktopCapture.DesktopPixelSize.Width > 0 && desktopCapture.DesktopPixelSize.Height > 0, "Windows desktop capture API returns a bitmap (not saved)");
        desktopCapture.Close();
        Check(HotkeyService.TryParse("Ctrl+Alt+A", out var modifiers, out var key) && modifiers == 3 && key == 65, "Global shortcut parsing");
        Check(!HotkeyService.TryParse("A", out _, out _) && !HotkeyService.TryParse("Ctrl+Unknown", out _, out _), "Reject invalid shortcuts");
        var pixelRect = CaptureWindow.ToPixelRect(new Rect(100, 50, 400, 300), 1280, 720, 2560, 1440);
        Check(pixelRect == new Int32Rect(200, 100, 800, 600), "Capture coordinates at 200 percent scale");
        var clipped = CaptureWindow.ToPixelRect(new Rect(-10, -10, 4000, 4000), 1280, 720, 2560, 1440);
        Check(clipped == new Int32Rect(0, 0, 2560, 1440), "Capture selection clips to bitmap");
        window.ShowEditor(true); window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        window.OcrText.Text = "前缀\nhello\nworld\n后缀";
        window.OcrText.IsUndoEnabled = false; window.OcrText.IsUndoEnabled = true;
        window.OcrText.Select(3, 11);
        window.ApplyTextTransform(TextCleanup.MergeLines);
        Check(TextCleanup.NormalizeLineEndings(window.OcrText.Text) == "前缀\nhello world\n后缀", "Selected text cleanup preserves surrounding text");
        window.OcrText.Undo();
        Check(TextCleanup.NormalizeLineEndings(window.OcrText.Text) == "前缀\nhello\nworld\n后缀", "Text cleanup undo restores complete content");
        var image = window.Surface.Export();
        Check(image.PixelWidth == 940 && image.PixelHeight == 680, "Export preserves original image dimensions");
        var samplePath = Path.Combine(App.DataDirectory, "sample-export.png"); MainWindow.SavePng(image, samplePath);
        Check(File.Exists(samplePath) && new FileInfo(samplePath).Length > 1000, "PNG image export");
        var ocr = await new OcrService().RecognizeAsync(image);
        Check(ocr.Error == null && ocr.Text.Contains("Capture", StringComparison.OrdinalIgnoreCase) && ocr.Text.Contains("截图", StringComparison.Ordinal), "Actual offline bilingual OCR of app sample");
        return new Dictionary<string, object> { ["status"] = "passed", ["checks"] = passed, ["ocrLanguage"] = ocr.Language, ["ocrText"] = ocr.Text };
    }
}
