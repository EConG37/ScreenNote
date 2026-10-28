using System;
using System.Diagnostics;
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

internal static class GifRecordingUi
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static readonly Type RecordingType = typeof(MainWindow).Assembly.GetType("ScreenNote.GifRecordingWindow")!;
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        var folder = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(".build", "gif-recording-ui"));
        Directory.CreateDirectory(folder);
        try
        {
            typeof(App).GetProperty(nameof(App.IsTestMode))!.SetValue(null, true);
            typeof(App).GetProperty(nameof(App.DataDirectory))!.SetValue(null, folder);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "test-resources.xaml"));
            app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(ns + "ResourceDictionary", source.Root!.Attributes().Where(x => x.IsNamespaceDeclaration), source.Root.Element(ns + "Application.Resources")!.Elements()).ToString());

            var fixture = new Window { Width = 320, Height = 180, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, Background = Brushes.Red };
            fixture.Show();
            SetWindowPos(new WindowInteropHelper(fixture).Handle, new nint(-1), 80, 80, 320, 180, 0x0040);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            timer.Tick += (_, _) => fixture.Background = ReferenceEquals(fixture.Background, Brushes.Red) ? Brushes.Blue : Brushes.Red;
            timer.Start();
            var region = new System.Drawing.Rectangle(80, 80, 320, 180);
            var window = NewWindow(region, folder); window.Show();
            SetWindowPos(new WindowInteropHelper(window).Handle, new nint(-1), 80, 80, 0, 0, 0x0041);
            Check(Get<bool>(window, "_excluded"), "Windows excludes recording controls from capture");
            var operation = (Task)Call(window, "StartAsync")!;
            Until(() =>
            {
                if (operation.IsCompleted) throw new InvalidOperationException("Recording ended before capture: " + Get<TextBlock>(window, "_status").Text, operation.Exception);
                return Get<bool>(window, "_recording");
            }, 6000);
            Pump(700);
            Get<CancellationTokenSource>(window, "_stop").Cancel();
            Until(() => operation.IsCompleted, 8000); operation.GetAwaiter().GetResult();
            var result = Get<object>(window, "_result");
            var path = (string)result.GetType().GetProperty("Path")!.GetValue(result)!;
            Check(File.Exists(path) && !Get<bool>(window, "_running"), "Stopping commits GIF and completes the recording state");
            using (var stream = File.OpenRead(path))
            {
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Check(decoder.Frames.Count >= 2, "Real desktop recording contains multiple frames");
                foreach (var frame in decoder.Frames)
                {
                    var pixel = new byte[4];
                    new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(5, 5, 1, 1), pixel, 4, 0);
                    Check(pixel[1] < 15 && (pixel[0] > 240 || pixel[2] > 240), "Overlapping control window is absent from the recorded fixture");
                }
            }
            Check(Clipboard.GetFileDropList().Cast<string>().SequenceEqual(new[] { path }) && !Clipboard.ContainsImage(), "Automatic clipboard copy exposes the full GIF file");
            Pump(120);
            var content = (FrameworkElement)window.Content; content.UpdateLayout();
            var preview = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32); preview.Render(content);
            using (var stream = File.Create(Path.Combine(folder, "gif-result.png"))) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview)); encoder.Save(stream); }
            window.Close();
            Check(File.Exists(path), "Closing the result keeps the GIF available for paste");

            var before = Directory.GetFiles(Path.Combine(folder, "recordings")).Length;
            var countdown = NewWindow(region, folder); countdown.Show();
            var cancelled = (Task)Call(countdown, "StartAsync")!; Pump(100); countdown.Close();
            Until(() => cancelled.IsCompleted, 3000);
            Check(!countdown.IsVisible && Directory.GetFiles(Path.Combine(folder, "recordings")).Length == before, "Closing during countdown records nothing");

            var discarded = NewWindow(region, folder); discarded.Show();
            var discardedTask = (Task)Call(discarded, "StartAsync")!;
            Until(() => Get<bool>(discarded, "_recording"), 6000); Pump(300); discarded.Close();
            Until(() => discardedTask.IsCompleted, 5000);
            Check(!discarded.IsVisible && Directory.GetFiles(Path.Combine(folder, "recordings")).Length == before, "Cancel during capture removes the discarded recording");

            var main = new MainWindow([]); main.Show();
            VerifyMainFlow(main, app);
            main.Close();
            timer.Stop(); fixture.Close(); app.Shutdown();
            File.WriteAllText(Path.Combine(folder, "result.txt"), $"PASS: {_checks} live GIF UI checks.");
            Console.WriteLine($"PASS: {_checks} live GIF UI checks. Preview: {folder}"); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
    private static Window NewWindow(System.Drawing.Rectangle region, string folder) => (Window)Activator.CreateInstance(RecordingType, Flags, null, new object[] { region, folder }, null)!;
    private static void VerifyMainFlow(MainWindow main, Application app)
    {
        Task? operation = null;
        Exception? failure = null;
        var selected = false; var opened = false;
        var frame = new DispatcherFrame();
        var deadline = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            try
            {
                var selector = app.Windows.OfType<CaptureWindow>().FirstOrDefault(item => item.IsVisible);
                if (selector != null && !selected)
                {
                    selected = true;
                    typeof(CaptureWindow).GetField("_rect", Flags)!.SetValue(selector, new Rect(80, 80, 320, 180));
                    typeof(CaptureWindow).GetMethod("Complete", Flags)!.Invoke(selector, new object[] { CaptureAction.RecordGif });
                }
                if (Get<Window?>(main, "_gifRecording") is { IsVisible: true } recording && !opened)
                {
                    opened = true;
                    Check(Get<bool>(main, "_capturing"), "Screenshot selection enters the GIF flow while retaining the capture gate");
                    Call(main, "ShowWorkspace");
                    Check(!main.IsVisible, "Reopening the workspace keeps it out of the recording");
                    Check(((Task)Call(main, "CaptureAsync")!).IsCompleted && !app.Windows.OfType<CaptureWindow>().Any(item => item.IsVisible), "Another screenshot cannot interrupt GIF recording");
                    recording.Close();
                }
                if (operation?.IsCompleted == true) frame.Continue = false;
                if (deadline.Elapsed.TotalSeconds > 10) throw new TimeoutException("Main GIF flow timed out.");
            }
            catch (Exception ex)
            {
                failure = ex;
                foreach (var dialog in app.Windows.Cast<Window>().Where(item => !ReferenceEquals(item, main)).ToArray()) dialog.Close();
                frame.Continue = false;
            }
        };
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => operation = (Task)Call(main, "CaptureAsync")!));
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (failure != null) throw failure;
        operation!.GetAwaiter().GetResult();
        Check(selected && opened, "Selected GIF action opens the recorder instead of static image history");
        Check(!Get<bool>(main, "_capturing") && main.IsVisible, "Closing GIF setup restores the workspace and releases capture");
    }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static object? Call(object target, string name) => target.GetType().GetMethod(name, Flags)!.Invoke(target, null);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; Console.WriteLine("PASS: " + message); }
    private static void Until(Func<bool> condition, int timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("UI operation timed out."); Pump(15); }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
