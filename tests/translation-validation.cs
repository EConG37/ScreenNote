using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Runtime.InteropServices;
using ScreenNote;

internal static class TranslationValidation
{
    [DllImport("user32.dll")] private static extern nint GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly List<string> Checks = [];
    private static string _directory = "";
    [STAThread]
    private static int Main()
    {
        _directory = Path.GetFullPath(".build/translation-validation-071"); Directory.CreateDirectory(_directory);
        try
        {
            typeof(App).GetProperty(nameof(App.IsTestMode))!.SetValue(null, true);
            typeof(App).GetProperty(nameof(App.DataDirectory))!.SetValue(null, Path.Combine(_directory, "data-" + Guid.NewGuid().ToString("N")));
            var previousReport = Path.Combine(_directory, "report.json"); if (File.Exists(previousReport)) File.Delete(previousReport);
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "test-resources.xaml")); XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(ns + "ResourceDictionary", document.Root!.Attributes().Where(a => a.IsNamespaceDeclaration), document.Root.Element(ns + "Application.Resources")!.Elements()).ToString());
            VerifyEngine(); VerifyWindow(); VerifyRoutes();
            File.WriteAllText(Path.Combine(_directory, "report.json"), JsonSerializer.Serialize(new { status = "passed", checks = Checks }, new JsonSerializerOptions { WriteIndented = true }));
            var previousFailure = Path.Combine(_directory, "failure.txt"); if (File.Exists(previousFailure)) File.Delete(previousFailure);
            app.Shutdown(); Console.WriteLine($"PASS: {Checks.Count} translation checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(_directory, "failure.txt"), ex.ToString()); return 1; }
    }
    private static void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); Checks.Add(description); Console.WriteLine("PASS: " + description); }
    private static T Field<T>(object window, string name) => (T)window.GetType().GetField(name, Flags)!.GetValue(window)!;
    private static object? Call(object window, string name, params object[] args) => window.GetType().GetMethod(name, Flags)!.Invoke(window, args);
    private static void PumpUntil(Func<bool> done, int milliseconds = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) { timer.Stop(); frame.Continue = false; } }; timer.Start(); Dispatcher.PushFrame(frame);
        if (!done()) throw new TimeoutException("Translation UI operation did not finish.");
    }
    private static TranslationWindow ActiveTranslation() => System.Windows.Application.Current.Windows.OfType<TranslationWindow>().Last();
    private static void VerifyEngine()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ScreenNote", "translation");
        var existingOperations = Directory.Exists(temp) ? Directory.GetDirectories(temp).ToHashSet() : [];
        var service = new TranslationService();
        var english = service.TranslateAsync("The meeting starts at 3 PM.\n\nPlease copy the screenshot and edit the text.").GetAwaiter().GetResult();
        Check(english.Error == null && english.Direction == "en_zh" && Regex.IsMatch(english.Text, "[\u3400-\u9fff]") && english.Text.Contains("3"), "Real bundled CPU model translates English to Chinese from a Unicode installation path");
        Check(english.Text.Contains("\n\n") && !english.Text.Contains('▁'), "Translated paragraphs retain blank lines and no token markers leak into the UI");
        var chinese = service.TranslateAsync("会议将在下午三点开始。\n\n请复制截图并修改文字。").GetAwaiter().GetResult();
        Check(chinese.Error == null && chinese.Direction == "zh_en" && chinese.Text.Contains("meeting", StringComparison.OrdinalIgnoreCase) && chinese.Text.Contains("screenshot", StringComparison.OrdinalIgnoreCase), "Auto direction translates Chinese into meaningful English");
        var forced = service.TranslateAsync("Hello world!", "en_zh").GetAwaiter().GetResult();
        Check(forced.Error == null && forced.Direction == "en_zh", "Explicit language direction runs the chosen model");
        Check(service.TranslateAsync(" ").GetAwaiter().GetResult().Error != null && service.TranslateAsync("text", "fr").GetAwaiter().GetResult().Error != null && service.TranslateAsync(new string('x', 20001)).GetAwaiter().GetResult().Error != null, "Empty, unsupported and oversized requests have clear local errors");
        using var cancellation = new CancellationTokenSource(); cancellation.CancelAfter(180);
        try { service.TranslateAsync(string.Join(" ", Enumerable.Repeat("Please copy the screenshot.", 300)), cancellationToken: cancellation.Token).GetAwaiter().GetResult(); throw new Exception("Translation cancellation was ignored"); }
        catch (OperationCanceledException) { Check(true, "Cancellation interrupts the real worker process"); }
        Check(!Directory.Exists(temp) || Directory.GetDirectories(temp).ToHashSet().SetEquals(existingOperations), "Success and cancellation clean their own temporary source text and preserve other operations");
        File.WriteAllText(Path.Combine(_directory, "samples.json"), JsonSerializer.Serialize(new { english, chinese, forced }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class DelayedTranslator : ITranslationService
    {
        public readonly List<(string text, string direction, CancellationToken token, TaskCompletionSource<TranslationResult> completion)> Requests = [];
        public Task<TranslationResult> TranslateAsync(string text, string direction = "auto", CancellationToken cancellationToken = default)
        { var completion = new TaskCompletionSource<TranslationResult>(); Requests.Add((text, direction, cancellationToken, completion)); return completion.Task; }
    }
    private static void VerifyWindow()
    {
        var service = new DelayedTranslator(); var window = new TranslationWindow("Original text", translator: service); window.Show();
        PumpUntil(() => service.Requests.Count == 1);
        var source = Field<TextBox>(window, "_source"); var output = Field<TextBox>(window, "_result"); var copy = Field<Button>(window, "_copy"); var translate = Field<Button>(window, "_translate");
        source.Text = "Corrected text";
        Check(service.Requests[0].token.IsCancellationRequested && translate.IsEnabled && !copy.IsEnabled, "Editing source cancels the old request and disables stale result copying");
        service.Requests[0].completion.SetResult(new("old translation", "en_zh")); PumpUntil(() => Field<CancellationTokenSource?>(window, "_operation") == null);
        Check(output.Text.Length == 0 && source.Text == "Corrected text", "A late completion cannot overwrite corrected source or install a stale translation");
        translate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => service.Requests.Count == 2);
        var freshTranslation = "修改后的文字 " + Guid.NewGuid().ToString("N");
        service.Requests[1].completion.SetResult(new(freshTranslation, "en_zh")); PumpUntil(() => copy.IsEnabled);
        Check(output.IsReadOnly && output.Text == freshTranslation && source.Text == "Corrected text", "Source and translation remain separate and both visible");
        window.Dispatcher.InvokeAsync(() => copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        try { PumpUntil(() => Field<TextBlock>(window, "_status").Text == "译文已复制。", 5000); }
        catch { var holder = GetOpenClipboardWindow(); GetWindowThreadProcessId(holder, out var pid); Console.WriteLine($"Copy failure: status={Field<TextBlock>(window, "_status").Text}, copying={Field<bool>(window, "_copying")}, holder={holder}, pid={pid}, self={Environment.ProcessId}, enabled={copy.IsEnabled}"); throw; }
        PumpUntil(() => { try { return Clipboard.GetText() == output.Text; } catch (System.Runtime.InteropServices.COMException) { return false; } }, 5000);
        Check(true, "Copy Translation places just the translation on the clipboard");
        var direction = Field<ComboBox>(window, "_direction"); direction.SelectedIndex = 2; translate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => service.Requests.Count == 3);
        Check(service.Requests[2].direction == "zh_en", "Changing direction selects the requested translation model");
        window.Close(); Check(service.Requests[2].token.IsCancellationRequested, "Closing the translation window cancels its pending operation");
        service.Requests[2].completion.SetResult(new("late", "zh_en"));
        var errorService = new DelayedTranslator(); var errorWindow = new TranslationWindow("source", translator: errorService); errorWindow.Show(); PumpUntil(() => errorService.Requests.Count == 1);
        errorService.Requests[0].completion.SetResult(new("", "", "test failure")); PumpUntil(() => Field<Button>(errorWindow, "_translate").IsEnabled);
        Check(Field<TextBox>(errorWindow, "_source").Text == "source" && Field<TextBlock>(errorWindow, "_status").Text == "test failure", "An engine failure retains source and permits retry"); errorWindow.Close();
    }
    private static void VerifyRoutes()
    {
        var main = new MainWindow([]); main.Show();
        var demo = (BitmapSource)typeof(MainWindow).Assembly.GetType("ScreenNote.DemoImage")!.GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, null)!;
        Call(main, "LoadImage", demo, "translation fixture"); Call(main, "ShowEditor", true);
        var ocr = Field<TextBox>(main, "OcrText"); ocr.Text = "First line.\nThe meeting starts at 3 PM."; ocr.Select(12, ocr.Text.Length - 12);
        var beforeText = ocr.Text; var beforeDocument = Field<AnnotationSurface>(main, "Surface").ExportDocument();
        Field<Button>(main, "PrimaryTranslateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); var window = ActiveTranslation();
        Check(Field<TextBox>(window, "_source").Text == ocr.SelectedText && !Field<bool>(main, "_sending"), "Editor translation uses selected corrected OCR text without a browser transfer");
        PumpUntil(() => Field<Button>(window, "_copy").IsEnabled); Preview(window);
        Check(ocr.Text == beforeText && Field<AnnotationSurface>(main, "Surface").ExportDocument() == beforeDocument, "Translation never changes the original OCR text or editable annotations"); window.Close();
        Call(main, "SaveCurrentHistory"); Call(main, "ShowHistory"); var list = Field<ListBox>(main, "HistoryList"); list.SelectAll();
        Field<Button>(main, "PrimaryTranslateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window = ActiveTranslation(); PumpUntil(() => Field<TextBox>(window, "_source").Text.Length > 0);
        Check(Field<TextBox>(window, "_source").Text == beforeText, "History translation reuses saved corrected OCR without recognizing the image again"); window.Close();
        var selector = new CaptureWindow(demo, new System.Drawing.Rectangle(0, 0, demo.PixelWidth, demo.PixelHeight));
        typeof(CaptureWindow).GetProperty(nameof(CaptureWindow.Action))!.SetValue(selector, CaptureAction.Translate);
        typeof(CaptureWindow).GetProperty(nameof(CaptureWindow.CapturedImage))!.SetValue(selector, demo);
        var count = list.Items.Count; var task = (Task)Call(main, "HandleCaptureActionAsync", selector, false)!;
        Check(task.IsCompletedSuccessfully && list.Items.Count == count + 1 && !Field<bool>(main, "_sending"), "Floating Translate retains one screenshot and opens local translation without a bridge");
        window = ActiveTranslation(); PumpUntil(() => Field<Button>(window, "_copy").IsEnabled, 25000);
        Check(Field<TextBox>(window, "_source").Text.Contains("截图") && Field<TextBox>(window, "_result").Text.Length > 0, "A fresh screenshot runs actual OCR followed by offline translation in the app");
        Check(!System.Windows.Application.Current.Windows.OfType<Window>().Any(w => w.Title.Contains("DeepSeek")), "No translation route opens an AI feedback window"); window.Close(); selector.Close(); main.Close();
    }
    private static void Preview(TranslationWindow window)
    {
        window.UpdateLayout(); var root = (FrameworkElement)window.Content;
        foreach (var size in new[] { new Size(880, 520), new Size(580, 350) })
        {
            root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
            foreach (var name in new[] { "_source", "_result", "_copy", "_translate", "_direction" })
            { var control = Field<FrameworkElement>(window, name); var point = control.TranslatePoint(new Point(), root); Check(control.ActualWidth > 0 && point.X >= 0 && point.Y >= 0 && point.X + control.ActualWidth <= size.Width + 1 && point.Y + control.ActualHeight <= size.Height + 1, $"Translation {name} fits at {size.Width} pixels"); }
        }
        root.Measure(new Size(880, 520)); root.Arrange(new Rect(0, 0, 880, 520)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(880, 520, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var dc = background.RenderOpen()) dc.DrawRectangle(window.Background, null, new Rect(0, 0, 880, 520));
        bitmap.Render(background); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(_directory, "translation-071.png")); encoder.Save(file);
    }
}
