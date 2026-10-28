using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using ScreenNote;

internal static class CompositionUi
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int _checks;
    private static string _folder = "";
    [STAThread]
    private static int Main(string[] args)
    {
        _folder = Path.GetFullPath(args.FirstOrDefault() ?? ".build/composition-ui"); Directory.CreateDirectory(_folder);
        try
        {
            typeof(App).GetProperty(nameof(App.IsTestMode))!.SetValue(null, true);
            typeof(App).GetProperty(nameof(App.DataDirectory))!.SetValue(null, _folder);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "test-resources.xaml"));
            app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(ns + "ResourceDictionary", document.Root!.Attributes().Where(item => item.IsNamespaceDeclaration), document.Root.Element(ns + "Application.Resources")!.Elements()).ToString());

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = new Image { Source = Page(), Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left } };
            var fixture = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, Content = scroll, ShowInTaskbar = false };
            fixture.Show(); SetWindowPos(new WindowInteropHelper(fixture).Handle, new nint(-1), 70, 70, 400, 320, 0x40); Pump(150);
            var region = new System.Drawing.Rectangle(70, 70, 400, 320);
            var main = new MainWindow([]); main.Show(); Pump(100);

            Task? capture = null; Task? start = null; Task? finish = null; Window? recorder = null;
            var chose = false; var scrolled = false; var goal = 0;
            Drive(() => capture?.IsCompleted == true, () =>
            {
                var selector = app.Windows.OfType<CaptureWindow>().FirstOrDefault(window => window.IsVisible);
                if (!chose && selector != null) { chose = true; SelectRegion(selector, region, CaptureAction.Scroll); }
                if (Get<object?>(main, "_scrollCapture") is Window window)
                {
                    recorder = window;
                    if (start == null) { start = (Task)Call(window, "StartAsync")!; return; }
                    if (!start.IsCompleted) return;
                    var stitcher = Get<object?>(window, "_stitcher");
                    if (stitcher == null) throw new Exception("Scroll capture could not start: " + Get<TextBlock>(window, "_status").Text);
                    if (!scrolled)
                    {
                        fixture.Activate(); SetCursorPos(220, 220);
                        mouse_event(0x0800, 0, 0, unchecked((uint)-2400), 0); scrolled = true;
                        return;
                    }
                    if (scroll.VerticalOffset <= 0) return;
                    goal = 320 + (int)Math.Round(scroll.VerticalOffset * VisualTreeHelper.GetDpi(fixture).DpiScaleY);
                    if ((Get<object?>(window, "_wheel") is not object wheel || (int)wheel.GetType().GetProperty("Pending", Flags)!.GetValue(wheel)! == 0) && (int)stitcher.GetType().GetProperty("Height", Flags)!.GetValue(stitcher)! >= goal && finish == null)
                        finish = (Task)Call(window, "FinishAsync")!;
                }
            }, () => capture = (Task)Call(main, "CaptureAsync")!, 15000);
            capture!.GetAwaiter().GetResult();
            var surface = (AnnotationSurface)main.FindName("Surface");
            Check(chose && recorder != null && surface.Source != null, "Screenshot selection reaches real scrolling capture and opens the long image in the editor" + $" (selected={chose}, recorder={recorder != null}, status={((TextBlock)main.FindName("StatusText")).Text})");
            Check(Math.Abs(surface.Source!.PixelHeight - goal) <= 2, "Desktop scrolling appends only the newly revealed rows");
            Check(scroll.VerticalOffset >= 300, "Native mouse wheel reaches the original page while scroll capture is running");
            Check(Clipboard.ContainsImage() && Clipboard.GetImage()!.PixelHeight == surface.Source.PixelHeight, "Finished long screenshot is copied and retained");
            Save(surface.Source, "live-scroll.png");

            var image1 = Solid(Colors.DarkSlateBlue); var image2 = Solid(Colors.DarkGreen);
            Call(main, "LoadImage", image1, "台词一"); Call(main, "LoadImage", image2, "台词二");
            var history = (ListBox)main.FindName("HistoryList"); history.SelectedItems.Clear();
            history.SelectedItems.Add(history.Items[0]); history.SelectedItems.Add(history.Items[1]);
            Call(main, "Collage_Click", main, new RoutedEventArgs());
            var collage = Get<Window>(main, "_collageWindow"); Pump(150);
            Check(Get<IList>(collage, "_frames").Count == 2, "Selected history images open in the collage workspace");
            var initial = Get<BitmapSource>(collage, "_composed");
            Get<Slider>(collage, "_subtitle").Value = 35; Call(collage, "Render");
            Check(Get<BitmapSource>(collage, "_composed").PixelHeight > initial.PixelHeight, "Subtitle crop height updates the preview");
            var list = Get<ListBox>(collage, "_list"); list.SelectedIndex = 1; Call(collage, "Move", -1);
            Check(list.SelectedIndex == 0, "Images can be reordered without losing selection");
            Call(collage, "Render"); SaveWindow(collage, "collage-workspace.png");
            Task? add = null; var addedSelection = false;
            Drive(() => add?.IsCompleted == true, () =>
            {
                var selector = app.Windows.OfType<CaptureWindow>().FirstOrDefault(window => window.IsVisible);
                if (selector != null && !addedSelection) { addedSelection = true; SelectRegion(selector, region, CaptureAction.Copy); }
            }, () => add = (Task)Call(main, "CaptureAsync")!, 8000);
            add!.GetAwaiter().GetResult();
            Check(addedSelection && ReferenceEquals(Get<object>(main, "_collageWindow"), collage) && collage.IsVisible && Get<IList>(collage, "_frames").Count == 3, "Another screenshot returns to the same collage and adds a third image");
            Call(collage, "Copy");
            Check(Clipboard.GetImage()!.PixelHeight == Get<BitmapSource>(collage, "_composed").PixelHeight, "Collage copies the current rendered image");
            var before = history.Items.Count;
            Get<Button>(collage, "_edit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!collage.IsVisible && history.Items.Count == before + 1 && ((HistoryEntry)history.Items[0]).Origin == "拼图", "Complete opens the collage in the editor and preserves originals in history");
            Check(!Get<bool>(main, "_capturing"), "All composition paths release the screenshot gate");
            main.Close(); fixture.Close(); app.Shutdown();
            File.WriteAllText(Path.Combine(_folder, "result.txt"), $"PASS: {_checks} live composition UI checks.");
            Console.WriteLine($"PASS: {_checks} live composition UI checks. {_folder}"); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(_folder, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
    private static void SelectRegion(CaptureWindow selector, System.Drawing.Rectangle region, CaptureAction action)
    {
        var bounds = Get<System.Drawing.Rectangle>(selector, "_bounds"); var canvas = Get<Canvas>(selector, "_canvas");
        var scaleX = canvas.ActualWidth / bounds.Width; var scaleY = canvas.ActualHeight / bounds.Height;
        typeof(CaptureWindow).GetField("_rect", Flags)!.SetValue(selector, new Rect((region.X - bounds.X) * scaleX, (region.Y - bounds.Y) * scaleY, region.Width * scaleX, region.Height * scaleY));
        typeof(CaptureWindow).GetMethod("Complete", Flags)!.Invoke(selector, new object[] { action });
    }
    private static void Drive(Func<bool> completed, Action tick, Action begin, int timeout)
    {
        Exception? failure = null; var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            try { tick(); if (completed()) frame.Continue = false; if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("Composition UI operation timed out."); }
            catch (Exception ex)
            {
                failure = ex;
                foreach (var window in Application.Current.Windows.Cast<Window>().Where(window => window is CaptureWindow || window.GetType().Name == "ScrollCaptureWindow").ToArray()) window.Close();
                frame.Continue = false;
            }
        };
        Dispatcher.CurrentDispatcher.BeginInvoke(begin); timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (failure != null) throw failure;
    }
    private static void SaveWindow(Window window, string name)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); Save(bitmap, name);
    }
    private static void Pump(int milliseconds) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethods(Flags).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args);
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); _checks++; Console.WriteLine("PASS: " + text); }
    private static void Save(BitmapSource bitmap, string name) { using var stream = File.Create(Path.Combine(_folder, name)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
    private static BitmapSource Solid(Color color) { var pixels = new byte[320 * 180 * 4]; for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; } var image = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, pixels, 1280); image.Freeze(); return image; }
    private static BitmapSource Page()
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 400, 2000));
            for (var i = 0; i < 60; i++) dc.DrawText(new FormattedText($"{i:D3} 行 · 滚动页面验证 · 示例 {i * 79}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.Black, 1), new Point(14, i * 30 + 4));
        }
        var image = new RenderTargetBitmap(400, 2000, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
}
