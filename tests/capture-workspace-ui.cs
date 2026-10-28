using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
using ScreenNote;

internal static class CaptureWorkspaceUi
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static string _folder = "";
    private static readonly List<string> Checks = [];
    [STAThread]
    private static int Main(string[] args)
    {
        _folder = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--")) ?? ".build/feature-expansion/workspace");
        Directory.CreateDirectory(_folder);
        try
        {
            typeof(App).GetProperty(nameof(App.IsTestMode))!.SetValue(null, true);
            typeof(App).GetProperty(nameof(App.DataDirectory))!.SetValue(null, _folder);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "test-resources.xaml"));
            app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(ns + "ResourceDictionary", source.Root!.Attributes().Where(x => x.IsNamespaceDeclaration), source.Root.Element(ns + "Application.Resources")!.Elements()).ToString());
            var main = new MainWindow([]); main.Show(); Pump(100);
            Call(main, "LoadImage", Fixture("今晚的月色真美。", Colors.DarkSlateBlue), "台词一");
            Call(main, "LoadImage", Fixture("风也温柔。", Colors.DarkSlateGray), "台词二");
            if (args.Contains("--review")) { app.MainWindow = main; app.ShutdownMode = ShutdownMode.OnMainWindowClose; app.Run(); return 0; }
            VerifyCapture(main); VerifyCollage(main); VerifyZoom(main); VerifyContinuousCapture(); VerifyScrollRecovery();
            main.Close(); app.Shutdown();
            File.WriteAllLines(Path.Combine(_folder, "checks.txt"), Checks);
            Console.WriteLine($"PASS: {Checks.Count} capture, crop and zoom checks."); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(_folder, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
    private static void VerifyCapture(MainWindow main)
    {
        var desktop = Fixture("智能框选与截图标注", Colors.SlateGray);
        var capture = new CaptureWindow(desktop, new System.Drawing.Rectangle(-100, 0, 960, 540)); capture.Show(); Pump(100);
        Call(capture, "HandleKey", Key.P, ModifierKeys.None);
        Check(!Get<bool>(capture, "_focusMode"), "A drawing shortcut before selecting a crop does not block free selection");
        var canvas = Get<Canvas>(capture, "_canvas");
        Set(capture, "_windowRegions", new System.Drawing.Rectangle[] { new(0, 70, 430, 330), new(-100, 0, 960, 540) });
        var hover = new Point(200 * canvas.ActualWidth / 960, 150 * canvas.ActualHeight / 540);
        Call(capture, "PreviewWindowAt", hover); Pump(50);
        var rect = Get<Rect>(capture, "_rect");
        Check(Math.Abs(rect.Width * 960 / canvas.ActualWidth - 430) < 1 && Get<bool>(capture, "_smartPreview"), "Smart hover chooses the frontmost containing program window with negative desktop coordinates");
        Check(Get<Border>(capture, "_actions").Visibility == Visibility.Collapsed && Get<Border>(capture, "_editTools").Visibility == Visibility.Collapsed, "Hover preview stays uncluttered until a window is selected");
        var click = canvas.PointToScreen(hover);
        SetCursorPos((int)click.X, (int)click.Y); Pump(40); mouse_event(2, 0, 0, 0, 0); Pump(40); mouse_event(4, 0, 0, 0, 0); Pump(40);
        Check(!Get<bool>(capture, "_smartPreview") && Get<Border>(capture, "_actions").Visibility == Visibility.Visible && Math.Abs(Get<Rect>(capture, "_rect").Width * 960 / canvas.ActualWidth - 430) < 1, "A native single click commits the recognized window and reveals both toolbars");
        Call(capture, "PreviewWindowAt", new Point(-500, -500));
        Check(Get<Rect>(capture, "_rect").Width == 0, "Moving away from all program windows clears the preview safely");
        Set(capture, "_rect", new Rect(canvas.ActualWidth * .12, canvas.ActualHeight * .18, canvas.ActualWidth * .7, canvas.ActualHeight * .68));
        Set(capture, "_smartPreview", false); Call(capture, "UpdateSelection"); Pump(50);
        var upper = Get<Border>(capture, "_editTools"); var lower = Get<Border>(capture, "_actions");
        rect = Get<Rect>(capture, "_rect");
        Check(upper.Visibility == Visibility.Visible && Canvas.GetTop(upper) + upper.ActualHeight <= rect.Top, "Drawing toolbar appears above the committed crop");
        var footer = Get<StackPanel>(capture, "_completionPanel");
        Check(Name((Button)footer.Children[0]) == "取消" && Name((Button)footer.Children[1]) == "编辑截屏" && Name((Button)footer.Children[2]) == "复制截屏", "Completion actions use the requested Cancel, Edit, Copy order");
        var tools = Get<IDictionary>(capture, "_editToolButtons");
        foreach (var tool in new[] { AnnotationTool.Pen, AnnotationTool.Arrow, AnnotationTool.Line, AnnotationTool.Number, AnnotationTool.Mosaic })
        {
            ((Button)tools[tool]!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Get<AnnotationSurface>(capture, "_focusSurface").Tool == tool && Get<bool>(capture, "_focusMode"), "Upper toolbar activates " + tool);
        }
        ((Button)tools[AnnotationTool.Arrow]!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var surface = Get<AnnotationSurface>(capture, "_focusSurface");
        var view = Get<Viewbox>(capture, "_focusView");
        var start = view.PointToScreen(new Point(view.ActualWidth * .25, view.ActualHeight * .3));
        var end = view.PointToScreen(new Point(view.ActualWidth * .7, view.ActualHeight * .65));
        SetCursorPos((int)start.X, (int)start.Y); mouse_event(2, 0, 0, 0, 0); Pump(50);
        SetCursorPos((int)end.X, (int)end.Y); Pump(80); mouse_event(4, 0, 0, 0, 0); Pump(50);
        Check(surface.HasAnnotations, "Native mouse dragging draws an arrow inside the crop");
        var document = surface.ExportDocument(); surface.Undo(); Check(!surface.HasAnnotations, "Upper toolbar drawing participates in undo"); surface.Redo();
        Check(surface.ExportDocument() == document, "Redo restores the editable arrow");
        Call(capture, "SetCaptureTool", (AnnotationTool?)null);
        Check(!Get<bool>(capture, "_focusMode") && !view.IsHitTestVisible, "Return to selection restores crop movement");
        Save(capture, "capture-edit-tools.png"); capture.Close();
        Check(typeof(MainWindow).Assembly.GetType("ScreenNote.WindowRegionDetector")!.GetMethod("Snapshot", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [System.Windows.Forms.SystemInformation.VirtualScreen]) is IList list && list.Count > 0, "Native program detection discovers visible windows on the actual desktop");
    }
    private static void VerifyContinuousCapture()
    {
        var frameType = typeof(MainWindow).Assembly.GetType("ScreenNote.CollageFrame")!;
        var type = typeof(MainWindow).Assembly.GetType("ScreenNote.CollageWindow")!;
        var remaining = 3;
        Func<Task<BitmapSource?>> capture = () => Task.FromResult<BitmapSource?>(remaining-- > 0 ? Fixture("连续截图", Colors.DarkSlateBlue) : null);
        var window = (Window)Activator.CreateInstance(type, Flags, null, [Array.CreateInstance(frameType, 0), capture], null)!;
        window.Show(); Pump(30); Set(window, "_repeatCapture", true);
        Await((Task)Call(window, "AddCaptureAsync")!);
        Check(Get<IList>(window, "_frames").Count == 3 && window.IsVisible && !Get<bool>(window, "_adding"), "Continuous capture collects several selections and returns to the same collage after Escape");
        window.Close();
    }
    private static void VerifyScrollRecovery()
    {
        var type = typeof(MainWindow).Assembly.GetType("ScreenNote.ScrollCaptureWindow")!;
        var first = Fixture("第一屏", Colors.DarkSlateBlue); BitmapSource current = first;
        var window = (Window)Activator.CreateInstance(type, Flags, null, [new System.Drawing.Rectangle(100, 100, 960, 540)], null)!;
        type.GetProperty("FrameSource", Flags)!.SetValue(window, (Func<BitmapSource>)(() => current));
        window.Show(); Await((Task)Call(window, "StartAsync")!);
        current = Fixture("跳转到无重叠页面", Colors.Goldenrod); Await((Task)Call(window, "FinishAsync")!);
        Check(window.IsVisible && type.GetProperty("CapturedImage", Flags)!.GetValue(window) == null && Get<TextBlock>(window, "_status").Text.Contains("不连续"), "A missing overlap prevents Finish from silently exporting an incomplete long screenshot");
        current = first; Await((Task)Call(window, "FinishAsync")!);
        Check(!window.IsVisible && type.GetProperty("CapturedImage", Flags)!.GetValue(window) is BitmapSource { PixelHeight: 540 }, "Returning to the last accepted page recovers and completes the existing long screenshot");
    }
    private static void Await(Task task)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 10000) Pump(20);
        if (!task.IsCompleted) throw new TimeoutException("Workspace operation did not finish");
        task.GetAwaiter().GetResult();
    }
    private static void VerifyCollage(MainWindow main)
    {
        Call(main, "Collage_Click", main, new RoutedEventArgs()); var collage = Get<Window>(main, "_collageWindow"); Pump(100);
        var add = collage.GetType().GetMethod("AddCaptureAsync", Flags)!;
        // Add a second image through the public workspace's capture callback without
        // depending on the private user's desktop content.
        var frames = Get<IList>(collage, "_frames");
        var frameType = typeof(MainWindow).Assembly.GetType("ScreenNote.CollageFrame")!;
        frames.Add(Activator.CreateInstance(frameType, Fixture("风也温柔。", Colors.DarkSlateGray), "台词二")!);
        var list = Get<ListBox>(collage, "_list"); list.SelectedIndex = 1; Pump(80);
        Get<Slider>(collage, "_cropTop").Value = 64; Get<Slider>(collage, "_cropBottom").Value = 89; Call(collage, "Render");
        var output = Get<BitmapSource>(collage, "_composed");
        Check(output.PixelHeight == 678, "Independent top and bottom crop boundaries compose exactly the selected subtitle rows");
        var selected = frames[1]!; var crop = frameType.GetProperty("Crop", Flags)!.GetValue(selected);
        list.SelectedIndex = 0; Check(!Get<Slider>(collage, "_cropTop").IsEnabled, "Cover image always remains complete");
        list.SelectedIndex = 1; Check(Math.Abs(Get<Slider>(collage, "_cropTop").Value - 64) < .1 && Equals(crop, frameType.GetProperty("Crop", Flags)!.GetValue(selected)), "Per-image crop survives selection changes");
        Get<Slider>(collage, "_subtitle").Value = 15; Call(collage, "Render");
        Check(Equals(crop, frameType.GetProperty("Crop", Flags)!.GetValue(selected)), "Changing the default strip does not overwrite an individually adjusted image");
        Call(collage, "Move", -1); Check(ReferenceEquals(frames[0], selected), "Moving a cropped image preserves its crop and changes the cover");
        Get<ComboBox>(collage, "_previewZoom").SelectedIndex = 2; Pump(50);
        Check(Math.Abs(Get<Image>(collage, "_preview").Width - Get<BitmapSource>(collage, "_composed").PixelWidth) < 1, "Collage preview supports actual-size inspection");
        Save(collage, "collage-per-image-crop.png"); collage.Close();
    }
    private static void VerifyZoom(MainWindow main)
    {
        Call(main, "ShowEditor", false); Pump(80);
        var view = (Viewbox)main.FindName("ImageView"); var width = view.Width;
        Call(main, "ChangeEditorZoom", 1.25); Pump(50);
        Check(view.Width > width * 1.24, "Visible zoom controls enlarge the editor image");
        ((ComboBox)main.FindName("ZoomChoice")).SelectedIndex = 2; Pump(50);
        Check(Math.Abs(view.Width - ((AnnotationSurface)main.FindName("Surface")).Source!.PixelWidth) < .1, "100% restores original pixel size after custom zoom");
        Save(main, "larger-editor.png");
        var type = typeof(MainWindow).Assembly.GetType("ScreenNote.ImageViewerWindow")!;
        var viewer = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [Fixture("大图查看", Colors.DarkSlateBlue)], null)!;
        viewer.Show(); Pump(80); Call(viewer, "Zoom", 2d, (Point?)null); Pump(50);
        Check(viewer.WindowState == WindowState.Maximized && Math.Abs(Get<Image>(viewer, "_image").Width - 1920) < .1, "Independent image viewer opens maximized and zooms beyond the viewport");
        Save(viewer, "image-viewer.png"); viewer.Close();
    }
    private static BitmapSource Fixture(string subtitle, Color color)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 960, 540));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), null, new Point(650, 180), 95, 95);
            dc.DrawText(new FormattedText("拾屏 · 截图与台词示例", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 28, Brushes.White, 1), new Point(45, 50));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), null, new Rect(0, 425, 960, 115));
            dc.DrawText(new FormattedText(subtitle, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 36, Brushes.White, 1), new Point(290, 460));
        }
        var image = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    private static void Save(Window window, string name) { window.UpdateLayout(); var root = (FrameworkElement)window.Content; var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(_folder, name)); encoder.Save(stream); }
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); Checks.Add(text); Console.WriteLine("PASS: " + text); }
    private static string Name(Button button) => System.Windows.Automation.AutomationProperties.GetName(button);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethods(Flags).Single(x => x.Name == name && x.GetParameters().Length == args.Length).Invoke(target, args);
    private static void Pump(int milliseconds) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
}
