using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using ScreenNote;

internal static class FeaturePolish
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static readonly List<string> Checks = [];
    private static string _folder = "";
    [STAThread]
    private static int Main(string[] args)
    {
        _folder = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(".build", "feature-polish-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_folder);
        try
        {
            typeof(App).GetProperty(nameof(App.IsTestMode))!.SetValue(null, true);
            typeof(App).GetProperty(nameof(App.DataDirectory))!.SetValue(null, _folder);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "test-resources.xaml"));
            app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(ns + "ResourceDictionary", source.Root!.Attributes().Where(x => x.IsNamespaceDeclaration), source.Root.Element(ns + "Application.Resources")!.Elements()).ToString());
            VerifyHighlight();
            var window = new MainWindow([]); window.Show(); Drain();
            VerifyActions(window); VerifyCaptureOverlay(window); window.Close(); app.Shutdown();
            File.WriteAllText(Path.Combine(_folder, "feature-polish.json"), JsonSerializer.Serialize(new { status = "passed", checks = Checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS: " + Checks.Count + " feature and layout checks. Previews: " + _folder); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(_folder, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Checks.Add(message); Console.WriteLine("PASS: " + message); }
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethods(Flags).Single(x => x.Name == method && x.GetParameters().Length == args.Length).Invoke(target, args);
    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Drain() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static BitmapSource Fixture(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4; var n = unchecked((uint)(x * 374761393 + y * 668265263)); n = (n ^ (n >> 13)) * 1274126177;
            pixels[i] = (byte)n; pixels[i + 1] = (byte)(n >> 8); pixels[i + 2] = (byte)(n >> 16); pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); bitmap.Freeze(); return bitmap;
    }
    private static byte[] Bytes(BitmapSource bitmap) { var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; converted.CopyPixels(data, bitmap.PixelWidth * 4, 0); return data; }
    private static string HighlightDocument(AnnotationSurface surface, Rect rect)
    {
        var json = JsonNode.Parse(surface.ExportDocument())!;
        json["Marks"] = JsonSerializer.SerializeToNode(new[] { new { Id = Guid.NewGuid(), Tool = AnnotationTool.HighlightBox, Start = new { X = rect.Left, Y = rect.Top }, End = new { X = rect.Right, Y = rect.Bottom }, Points = Array.Empty<object>(), Color = "#FF4B7DEE", Width = 3, TextSize = 22, TextWidth = 400, Text = "", Number = 0 } });
        return json.ToJsonString();
    }
    private static void VerifyHighlight()
    {
        var source = Fixture(160, 120); var surface = new AnnotationSurface { Source = source };
        surface.RestoreDocument(source, HighlightDocument(surface, new Rect(25, 25, 100, 65)));
        Check(surface.ExportDocument().Contains("\"Tool\":11"), "The new highlight tool appends to the enum so older annotation values stay compatible");
        var image = Bytes(surface.Export()); var original = Bytes(source);
        Check(!image.Take(20 * 160 * 4).SequenceEqual(original.Take(20 * 160 * 4)), "Focus highlight gently dims pixels outside the focus region");
        var interior = (50 * 160 + 60) * 4;
        Check(image.Skip(interior).Take(4).SequenceEqual(original.Skip(interior).Take(4)), "Focus interior retains its exact original color");
        Check(!image.SequenceEqual(original), "Highlight exports its white rounded outline, color accent and dim exterior");
        var restored = new AnnotationSurface(); restored.RestoreDocument(source, surface.ExportDocument());
        Check(Bytes(restored.Export()).SequenceEqual(image), "Highlight remains editable and renders identically after history restore");
        Call(surface, "SetSelected", ((System.Collections.IList)typeof(AnnotationSurface).GetField("_marks", Flags)!.GetValue(surface)!)[0], true);
        surface.DeleteSelected(); Check(Bytes(surface.Export()).SequenceEqual(original), "Deleting the focus region restores original pixels");
        surface.Undo(); Check(Bytes(surface.Export()).SequenceEqual(image), "Undo restores the complete focus mark");
        surface.Redo(); Check(Bytes(surface.Export()).SequenceEqual(original), "Redo removes the focus mark again");
        var multi = JsonNode.Parse(HighlightDocument(surface, new Rect(25, 25, 100, 65)))!;
        var second = multi["Marks"]![0]!.DeepClone(); second["Id"] = Guid.NewGuid().ToString();
        second["Start"] = JsonSerializer.SerializeToNode(new { X = 90, Y = 45 }); second["End"] = JsonSerializer.SerializeToNode(new { X = 150, Y = 105 });
        ((JsonArray)multi["Marks"]!).Add(second); surface.RestoreDocument(source, multi.ToJsonString());
        var combined = Bytes(surface.Export());
        Check(combined.Take(4).SequenceEqual(image.Take(4)), "Multiple focus boxes dim the exterior only once");
        Check(combined.Skip(interior).Take(4).SequenceEqual(original.Skip(interior).Take(4)) && combined.Skip((75 * 160 + 140) * 4).Take(4).SequenceEqual(original.Skip((75 * 160 + 140) * 4).Take(4)), "Both focus interiors retain original pixels when multiple boxes overlap");
    }
    private static void VerifyCaptureOverlay(MainWindow main)
    {
        var demo = (BitmapSource)typeof(MainWindow).Assembly.GetType("ScreenNote.DemoImage")!.GetMethod("Create")!.Invoke(null, null)!;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(242, 243, 246)), null, new Rect(0, 0, 1280, 860));
            dc.DrawImage(demo, new Rect(170, 80, 940, 680));
        }
        var desktop = new RenderTargetBitmap(1280, 860, 96, 96, PixelFormats.Pbgra32); desktop.Render(visual); desktop.Freeze();
        var capture = new CaptureWindow(desktop);
        var root = (Grid)capture.Content;
        var toolbar = (Border)typeof(CaptureWindow).GetField("_actions", Flags)!.GetValue(capture)!;
        var buttons = ((WrapPanel)toolbar.Child).Children.Cast<Button>().ToArray();
        foreach (var width in new[] { 1280d, 900d, 640d })
        {
            var height = width * 860 / 1280;
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            typeof(CaptureWindow).GetField("_rect", Flags)!.SetValue(capture, new Rect(width * 170 / 1280, height * 80 / 860, width * 940 / 1280, height * 680 / 860));
            Call(capture, "UpdateSelection"); root.UpdateLayout();
            foreach (var button in buttons)
            {
                var point = button.TranslatePoint(new Point(), root);
                Check(button.Visibility == Visibility.Visible && button.ActualWidth > 0 && point.X >= 0 && point.Y >= 0 && point.X + button.ActualWidth <= width && point.Y + button.ActualHeight <= height, "Floating action " + (System.Windows.Automation.AutomationProperties.GetName(button) is { Length: > 0 } label ? label : button.Content) + " stays fully visible at " + width);
            }
        }
        root.Measure(new Size(1280, 860)); root.Arrange(new Rect(0, 0, 1280, 860)); root.UpdateLayout();
        typeof(CaptureWindow).GetField("_rect", Flags)!.SetValue(capture, new Rect(170, 80, 940, 680)); Call(capture, "UpdateSelection"); root.UpdateLayout();
        void Save(string name)
        {
            var image = new RenderTargetBitmap(1280, 860, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(_folder, name)); encoder.Save(file);
        }
        Save("capture-toolbar-072.png");
        Call(capture, "ToggleFocus");
        var focus = (AnnotationSurface)typeof(CaptureWindow).GetField("_focusSurface", Flags)!.GetValue(capture)!;
        focus.RestoreDocument(focus.Source!, HighlightDocument(focus, new Rect(48, 278, 780, 52)));
        root.UpdateLayout(); Save("capture-focus-072.png");
        var source = focus.Source!; var document = focus.ExportDocument(); var rendered = focus.Export();
        typeof(CaptureWindow).GetProperty(nameof(CaptureWindow.Action))!.SetValue(capture, CaptureAction.Edit);
        typeof(CaptureWindow).GetProperty(nameof(CaptureWindow.CapturedImage))!.SetValue(capture, rendered);
        typeof(CaptureWindow).GetProperty(nameof(CaptureWindow.OriginalCapturedImage))!.SetValue(capture, source);
        typeof(CaptureWindow).GetProperty(nameof(CaptureWindow.CapturedDocumentJson))!.SetValue(capture, document);
        var task = (System.Threading.Tasks.Task)Call(main, "HandleCaptureActionAsync", capture, false)!;
        Check(task.IsCompletedSuccessfully, "Editing a highlighted capture completes immediately without a second selector");
        var surface = Control<AnnotationSurface>(main, "Surface");
        Check(Bytes(surface.Source!).SequenceEqual(Bytes(source)) && Bytes(surface.Export()).SequenceEqual(Bytes(rendered)), "Floating highlight routes the original crop and editable focus layer to the editor");
        Check((bool)Call(main, "SaveCurrentHistory")!, "The new capture and focus persist before leaving the editor");
        var editorDocument = surface.ExportDocument(); var historyCount = Control<ListBox>(main, "HistoryList").Items.Count;
        typeof(CaptureWindow).GetProperty(nameof(CaptureWindow.Action))!.SetValue(capture, CaptureAction.Copy);
        var copyTask = (System.Threading.Tasks.Task)Call(main, "HandleCaptureActionAsync", capture, false)!;
        Check(copyTask.IsCompletedSuccessfully && Control<ListBox>(main, "HistoryList").Items.Count == historyCount + 1, "Copying an inline highlight retains exactly one new history record");
        Check(surface.ExportDocument() == editorDocument && Bytes(Clipboard.GetImage()!).SequenceEqual(Bytes(rendered)), "Inline highlight copy includes the focus while preserving the existing active editor");
        Call(capture, "SetAiBusy", true);
        Check(!buttons[2].IsEnabled && buttons[5].IsEnabled && !buttons[6].IsEnabled && buttons[0].IsEnabled && buttons[3].IsEnabled, "A pending delivery disables AI actions while allowing local translation, Copy and OCR");
        capture.Close();
    }
    private static void VerifyActions(MainWindow window)
    {
        SavePreview(window, "empty.png");
        var actions = new[] { "PrimarySendButton", "PrimaryOcrButton", "PrimaryHighlightButton", "PrimaryTranslateButton", "PrimarySummaryButton" };
        foreach (var name in actions) Check(Control<Button>(window, name).Visibility == Visibility.Visible, name + " is visible in the first toolbar on startup");
        Check(!Control<Button>(window, "PrimarySendButton").IsEnabled && Control<Button>(window, "CaptureButton").IsEnabled, "Empty state disables image actions but keeps new capture available");
        var demo = (BitmapSource)typeof(MainWindow).Assembly.GetType("ScreenNote.DemoImage")!.GetMethod("Create")!.Invoke(null, null)!;
        Call(window, "LoadImage", demo, "工作记录");
        Check(actions.All(name => Control<Button>(window, name).IsEnabled), "The first toolbar enables every applicable image action in the editor");
        Control<Button>(window, "PrimaryHighlightButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var surface = Control<AnnotationSurface>(window, "Surface");
        Check(surface.Tool == AnnotationTool.HighlightBox && Control<ComboBox>(window, "ColorChoice").SelectedIndex == 2, "One-click highlight selects the box tool with the blue accent default");
        surface.RestoreDocument(demo, HighlightDocument(surface, new Rect(48, 278, 780, 52)));
        Call(window, "SelectTool", AnnotationTool.Select);
        var selectedMark = ((System.Collections.IList)typeof(AnnotationSurface).GetField("_marks", Flags)!.GetValue(surface)!)[0];
        Call(surface, "SetSelected", selectedMark, true); surface.InkColor = Colors.Blue;
        var beforeToolSwitch = Bytes(surface.Export());
        Control<Button>(window, "PrimaryHighlightButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Bytes(surface.Export()).SequenceEqual(beforeToolSwitch), "Selecting the amber highlight tool never recolors a previously selected annotation");
        surface.RestoreDocument(demo, HighlightDocument(surface, new Rect(48, 278, 780, 52)));
        Call(window, "ShowEditor", true);
        Control<TextBox>(window, "OcrText").Text = "今天的三个小目标\n\n01  截图后画箭头、加文字、圈出重点。\n02  识别文字后，直接修改错字和换行。\n03  整理好的内容，带到 AI 网页继续提问。\n\nCapture. Annotate. Edit. Ask.";
        Drain(); SavePreview(window, "editor-ocr.png");
        foreach (var width in new[] { 900.0, 1040.0, 1260.0 })
        {
            window.Width = width; window.UpdateLayout(); Drain();
            var bar = Control<StackPanel>(window, "PrimaryActionBar");
            var target = Control<ComboBox>(window, "EditorTargetChoice");
            foreach (var name in actions)
            {
                var button = Control<Button>(window, name); var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                Check(bounds.Right <= window.ActualWidth - 8 && bounds.Bottom <= 155, name + " is unclipped at width " + width);
            }
            var barBounds = bar.TransformToAncestor(window).TransformBounds(new Rect(bar.RenderSize));
            var targetBounds = target.TransformToAncestor(window).TransformBounds(new Rect(target.RenderSize));
            Check(barBounds.Right <= targetBounds.Left, "Primary actions never overlap the target selector at width " + width);
            Check(Control<WrapPanel>(window, "ToolPanel").ActualHeight <= 58, "Drawing tools stay on one row with the OCR panel at width " + width);
        }
        var promptMethod = typeof(MainWindow).GetMethod("AiActionPrompt", BindingFlags.Static | BindingFlags.NonPublic)!;
        try { promptMethod.Invoke(null, new object[] { "translate", "校对后的材料" }); throw new InvalidOperationException("Translation should not create AI prompts"); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { Check(true, "Translation no longer creates an AI prompt"); }
        Check(((string)promptMethod.Invoke(null, new object[] { "summary", "" })!).Contains("附图"), "Image summary references the attachments in their original order");
        Call(window, "LoadImage", demo, "阅读摘录"); Call(window, "LoadImage", demo, "项目想法"); Call(window, "ShowHistory");
        var list = Control<ListBox>(window, "HistoryList"); list.SelectAll(); Drain(); SavePreview(window, "history.png");
        Check(Control<Button>(window, "PrimarySendButton").IsEnabled && Control<Button>(window, "PrimaryTranslateButton").IsEnabled && Control<Button>(window, "PrimarySummaryButton").IsEnabled, "Multiple selected screenshots enable send, translation and summary at the top");
        Check(!Control<Button>(window, "PrimaryOcrButton").IsEnabled && !Control<Button>(window, "PrimaryHighlightButton").IsEnabled, "Single-image editing tools disable clearly when multiple images are selected");
        typeof(MainWindow).GetField("_sending", Flags)!.SetValue(window, true); Call(window, "RefreshHistoryActions");
        Check(actions.Where(name => name != "PrimaryTranslateButton").All(name => !Control<Button>(window, name).IsEnabled) && Control<Button>(window, "PrimaryTranslateButton").IsEnabled, "An active AI transfer keeps independent local translation available");
        typeof(MainWindow).GetField("_sending", Flags)!.SetValue(window, false); Call(window, "RefreshHistoryActions");
    }
    private static void SavePreview(MainWindow window, string name)
    {
        window.UpdateLayout(); Drain(); var visual = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(_folder, name)); encoder.Save(file);
    }
}
