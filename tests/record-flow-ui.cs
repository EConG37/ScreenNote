using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using ScreenNote;
using Drawing = System.Drawing;

/// <summary>
/// Live checks for the recording entry: the option dialog, the trim editor and the
/// three step flow started from the main window. Needs an interactive desktop session.
/// </summary>
internal static class RecordFlowUi
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static readonly Assembly AppAssembly = typeof(MainWindow).Assembly;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        var folder = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(".build", "record-flow-ui"));
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

            CheckPicker(folder);
            var gif = MakeGif(folder);
            CheckGifEditor(folder, gif);
            var mp4 = MakeMp4(folder);
            CheckVideoEditor(folder, mp4);
            CheckMainWindowFlow(folder);

            app.Shutdown();
            var stale = Path.Combine(folder, "failure.txt");
            if (File.Exists(stale)) File.Delete(stale);
            File.WriteAllText(Path.Combine(folder, "result.txt"), $"PASS: {_checks} recording flow checks.");
            Console.WriteLine($"PASS: {_checks} recording flow checks. Artifacts: {folder}");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "failure.txt"), ex.ToString());
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckPicker(string folder)
    {
        var picker = new RecordWindow();
        picker.Show(); Pump(260);
        Shot(picker, Path.Combine(folder, "01-record-options-gif.png"));
        var gif = Options(picker);
        Check(Mode(gif) == "Gif", "Recording dialog defaults to GIF");
        Check(Number(gif, "FramesPerSecond") == 15, "Default GIF speed is 15 fps");
        Check(Number(gif, "TargetBytes") == 10 * 1024 * 1024, "Default GIF target is the 10 MB WeChat size");
        Check(((FrameworkElement)Field(picker, "GifSettings")).IsVisible, "GIF settings are visible for the GIF mode");
        Check(!((FrameworkElement)Field(picker, "VideoSettings")).IsVisible, "Video settings stay hidden for the GIF mode");
        Check(((Button)Field(picker, "StartButton")).Content?.ToString()?.Contains("选择区域", StringComparison.Ordinal) == true, "Start button explains the next step");

        var videoCard = (RadioButton)Field(picker, "VideoCard");
        videoCard.IsChecked = true; Pump(120);
        var video = Options(picker);
        Check(Mode(video) == "Video", "Choosing the video card switches the mode");
        Check(Number(video, "FramesPerSecond") == 30, "Default video speed is 30 fps");
        Check(((FrameworkElement)Field(picker, "VideoSettings")).IsVisible, "Video settings appear for the video mode");
        Check(!((FrameworkElement)Field(picker, "GifSettings")).IsVisible, "GIF settings hide for the video mode");
        Shot(picker, Path.Combine(folder, "02-record-options-video.png"));
        picker.Close();
    }

    private static void CheckGifEditor(string folder, string gif)
    {
        var window = new RecordEditorWindow(gif);
        window.Show(); Pump(420);
        Set(window, "_crop", new Int32Rect(20, 10, 160, 100));
        Call(window, "Layout");
        window.EndSlider.Value = 1.0; Pump(160);
        Shot(window, Path.Combine(folder, "03-trim-editor.png"));
        Check(window.CropText.Text.Contains("160", StringComparison.Ordinal), "Crop panel reports the trimmed size");
        Check(window.KeepText.Text.Length > 0, "Trim panel reports the kept duration");
        var path = Path.Combine(folder, "export-crop.gif");
        var bytes = Export(window.GetType(), "ExportGif", gif, path, TimeSpan.Zero, TimeSpan.FromSeconds(1),
            new Int32Rect(20, 10, 160, 100), 15, 10L * 1024 * 1024, null);
        window.Close();
        Check(File.Exists(path) && bytes > 0, "GIF export writes a GIF file");
        using var stream = File.OpenRead(path);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Check(decoder.Frames.Count >= 2, "Exported GIF keeps several frames");
        Check(decoder.Frames[0].PixelWidth == 160 && decoder.Frames[0].PixelHeight == 100, "Exported GIF follows the crop rectangle");
        Check(new FileInfo(path).Length <= 10 * 1024 * 1024, "Exported GIF stays inside the 10 MB target");
    }

    private static void CheckVideoEditor(string folder, string mp4)
    {
        var window = new RecordEditorWindow(mp4);
        window.Show(); Pump(600);
        Check(window.VideoChoice.IsChecked == true, "Video recordings open with the video output selected");
        window.GifChoice.IsChecked = true; Pump(160);
        window.EndSlider.Value = 1.0; Pump(160);
        Shot(window, Path.Combine(folder, "04-video-trim-editor.png"));
        var gifPath = Path.Combine(folder, "export-from-video.gif");
        var crop = new Int32Rect(0, 0, 320, 180);
        Export(window.GetType(), "ExportGif", mp4, gifPath, TimeSpan.Zero, TimeSpan.FromSeconds(1), crop, 15,
            10L * 1024 * 1024, null);
        var videoPath = Path.Combine(folder, "export-trimmed.mp4");
        Export(window.GetType(), "ExportVideo", mp4, videoPath, TimeSpan.Zero, TimeSpan.FromSeconds(1), crop, 24, null);
        window.Close();
        Check(File.Exists(gifPath), "MP4 can be exported as an animated GIF");
        using (var stream = File.OpenRead(gifPath))
        {
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(decoder.Frames.Count >= 2, "Exported GIF from video holds several frames");
        }
        Check(new FileInfo(gifPath).Length <= 10 * 1024 * 1024, "GIF converted from video stays inside the 10 MB target");
        Check(File.Exists(videoPath) && new FileInfo(videoPath).Length > 4000, "Trimmed MP4 export writes a playable file");
    }

    private static long Export(Type type, string name, params object?[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        return Convert.ToInt64(method.Invoke(null, args));
    }

    private static void CheckMainWindowFlow(string folder)
    {
        var app = Application.Current;
        var main = new MainWindow([]); main.Show(); Pump(300);
        var frame = new DispatcherFrame();
        var deadline = Stopwatch.StartNew();
        Task? operation = null;
        var pickerShot = false; var selectorHandled = false; var recorderSeen = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (app.Windows.OfType<RecordWindow>().FirstOrDefault(item => item.IsVisible) is { } picker && !pickerShot)
                {
                    pickerShot = true;
                    Shot(picker, Path.Combine(folder, "05-main-record-dialog.png"));
                    Click((Button)Field(picker, "StartButton"));
                }
                if (app.Windows.OfType<CaptureWindow>().FirstOrDefault(item => item.IsVisible) is { } selector && !selectorHandled)
                {
                    selectorHandled = true;
                    Check(!main.IsVisible, "Choosing a recording hides the workspace");
                    typeof(CaptureWindow).GetField("_rect", Flags)!.SetValue(selector, new Rect(80, 80, 320, 180));
                    typeof(CaptureWindow).GetMethod("Complete", Flags)!.Invoke(selector, new object[] { CaptureAction.Copy });
                }
                if (app.Windows.OfType<RecordingWindow>().FirstOrDefault(item => item.IsVisible) is { } recorder && !recorderSeen)
                {
                    recorderSeen = true;
                    Check((bool)main.GetType().GetField("_capturing", Flags)!.GetValue(main)!, "Recording keeps the capture gate closed");
                    Shot(recorder, Path.Combine(folder, "06-recording-hud.png"));
                    recorder.Close();
                }
                if (operation?.IsCompleted == true) frame.Continue = false;
                if (deadline.Elapsed.TotalSeconds > 20) throw new TimeoutException("Record flow timed out.");
            }
            catch (Exception error) { frame.Continue = false; Console.Error.WriteLine(error); throw; }
        };
        timer.Start();
        operation = (Task)typeof(MainWindow).GetMethod("RecordAsync", Flags)!.Invoke(main, null)!;
        Dispatcher.PushFrame(frame);
        timer.Stop(); Pump(700);
        Check(pickerShot, "Record button opens the recording dialog");
        Check(selectorHandled, "Recording dialog continues into region selection");
        Check(recorderSeen, "Region selection reaches the recording HUD");
        Check(!(bool)main.GetType().GetField("_capturing", Flags)!.GetValue(main)!, "Closing the recorder releases the capture gate");
        Check(main.IsVisible, "Workspace returns after a cancelled recording");
        main.Close();
    }

    private static string MakeGif(string folder)
    {
        var type = AppAssembly.GetType("ScreenNote.GifRecorder", true)!;
        var method = type.GetMethod("Record", Flags | BindingFlags.Static)!;
        var size = new Drawing.Size(200, 120);
        var token = CancellationToken.None;
        var result = method.Invoke(null, new object?[]
        {
            (Func<BitmapSource>)(() => Frame(Colors.Red, 200, 120)), size, folder, token, null,
            TimeSpan.FromMilliseconds(1200), long.MaxValue, 10,
        })!;
        var path = (string)result.GetType().GetProperty("Path")!.GetValue(result)!;
        Check(File.Exists(path), "Synthetic GIF fixture recorded");
        return path;
    }

    private static string MakeMp4(string folder)
    {
        var type = AppAssembly.GetType("ScreenNote.VideoRecorder", true)!;
        var method = type.GetMethod("Record", Flags | BindingFlags.Static)!;
        var size = new Drawing.Size(320, 180);
        var index = 0;
        var result = method.Invoke(null, new object?[]
        {
            (Func<BitmapSource>)(() => Frame(index++ % 2 == 0 ? Colors.SteelBlue : Colors.Black, 320, 180)),
            size, folder, null, CancellationToken.None, null, TimeSpan.FromMilliseconds(1500), long.MaxValue, 30,
        })!;
        var path = (string)result.GetType().GetProperty("Path")!.GetValue(result)!;
        Check(File.Exists(path) && new FileInfo(path).Length > 4000, "Synthetic MP4 fixture recorded");
        return path;
    }

    internal static BitmapSource Frame(Color color, int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R; pixels[index + 3] = 255;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Shot(Window window, string file)
    {
        Pump(120);
        var content = (FrameworkElement)window.Content; content.UpdateLayout();
        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = window.Background is System.Windows.Media.SolidColorBrush brush && brush.Color.A > 0
                ? brush.Color : Colors.White;
            context.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        using var stream = File.Create(file);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    private static object Options(Window picker) => picker.GetType().GetProperty("Options", Flags)!.GetValue(picker)!;
    private static string Mode(object options) => options.GetType().GetProperty("Mode")!.GetValue(options)!.ToString()!;
    private static int Number(object options, string name) => Convert.ToInt32(options.GetType().GetProperty(name)!.GetValue(options));
    private static object Field(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    private static void Call(object target, string name) => target.GetType().GetMethod(name, Flags)!.Invoke(target, null);
    private static void Say(string message) => Console.WriteLine("[step] " + message + " " + DateTime.Now.ToString("HH:mm:ss.fff"));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; Console.WriteLine("PASS: " + message); }
    private static void Until(Func<bool> condition, int timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("Recording flow timed out."); Pump(15); }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}