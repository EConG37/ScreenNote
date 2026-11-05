using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenNote;
using Drawing = System.Drawing;

internal static partial class RecordFlowUi
{
    private static void CheckSilverAppearance(string folder, bool shellOnly)
    {
        var image = (BitmapSource)AppAssembly.GetType("ScreenNote.DemoImage")!.GetMethod("Create", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
        var main = new MainWindow(new[] { "--appearance-check" });
        try
        {
            main.Show(); Pump(180);
            Check(Math.Abs(main.FontSize - 11.2) < .01, "Main interface text is 80 percent of the previous 14 DIP size");
            Shot(main, Path.Combine(folder, "01-library-empty.png"));
            typeof(MainWindow).GetMethod("LoadImage", Flags)!.Invoke(main, new object[] { image, "界面检查" });
            Call(main, "ShowEditor"); Pump(180);
            var annotationButtons = ((Panel)Field(main, "ToolPanel")).Children.OfType<Button>().ToArray();
            Check(annotationButtons.All(button => !Descendants<TextBlock>(button).Any(text => !string.IsNullOrWhiteSpace(text.Text))), "Annotation tools show icons without permanent text labels");
            Check(annotationButtons.All(button => button.ToolTip != null && !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(button))), "Icon-only tools retain tooltips and accessible names");
            Click(annotationButtons.Single(button => Equals(button.Tag, "Arrow")));
            Check(((AnnotationSurface)Field(main, "Surface")).Tool == AnnotationTool.Arrow, "Icon-only arrow tool remains selectable");
            Click(annotationButtons.Single(button => Equals(button.Tag, "Rectangle")));
            Check(((AnnotationSurface)Field(main, "Surface")).Tool == AnnotationTool.Rectangle, "Icon-only rectangle tool remains selectable");
            Check(((FrameworkElement)Field(main, "EditorPanel")).IsVisible, "Screenshot editor opens after loading an image");
            Check(((FrameworkElement)Field(main, "Surface")).ActualWidth > 0, "Annotation surface retains a usable canvas");
            Shot(main, Path.Combine(folder, "02-image-editor.png"));
            main.Width = main.MinWidth; main.Height = main.MinHeight; Pump(140);
            Shot(main, Path.Combine(folder, "03-image-editor-minimum.png"));
            main.Width = 1200; main.Height = 820; Call(main, "ShowHistory"); Pump(100);
            var history = (ListBox)Field(main, "HistoryList"); history.SelectAll();
            Check(history.Items.Count > 0, "Isolated screenshot history is populated");
            Shot(main, Path.Combine(folder, "04-library-selected.png"));
            Exception? settingsError = null;
            var settingsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            settingsTimer.Tick += (_, _) =>
            {
                settingsTimer.Stop();
                var settings = main.OwnedWindows.Cast<Window>().FirstOrDefault();
                try
                {
                    Check(settings != null && settings.IsVisible, "Settings opens from the main window");
                    Shot(settings!, Path.Combine(folder, "05-settings.png"));
                    var scroll = Descendants<ScrollViewer>(settings!).FirstOrDefault();
                    scroll?.ScrollToEnd(); Pump(80);
                    Shot(settings!, Path.Combine(folder, "06-settings-bottom.png"));
                }
                catch (Exception error) { settingsError = error; }
                finally { settings?.Close(); }
            };
            settingsTimer.Start(); Call(main, "ShowSettings"); settingsTimer.Stop();
            if (settingsError != null) throw settingsError;
        }
        finally { main.Close(); }
        if (shellOnly) return;

        Window Construct(string type, params object[] args) => (Window)Activator.CreateInstance(AppAssembly.GetType("ScreenNote." + type)!, Flags, null, args, null)!;
        void Inspect(Window window, string name)
        {
            try { window.Show(); Pump(160); Check(window.ActualWidth > 100 && window.ActualHeight > 100, name + " renders"); Shot(window, Path.Combine(folder, name + ".png")); }
            finally { window.Close(); }
        }
        Inspect(Construct("ImageViewerWindow", image), "07-image-viewer");
        var frameType = AppAssembly.GetType("ScreenNote.CollageFrame")!;
        var frames = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(frameType))!;
        frames.Add(Activator.CreateInstance(frameType, Flags, null, new object[] { image, "截图一" }, null));
        frames.Add(Activator.CreateInstance(frameType, Flags, null, new object[] { image, "截图二" }, null));
        Func<Task<BitmapSource?>> capture = () => Task.FromResult<BitmapSource?>(null);
        var collage = Construct("CollageWindow", frames, capture);
        try
        {
            collage.Width = 1200; collage.Height = 820; collage.Show(); Pump(160); collage.UpdateLayout();
            VerifyCollageActions(collage);
            Shot(collage, Path.Combine(folder, "08-collage.png"));
            collage.Width = 1000; collage.Height = 700; Pump(120); collage.UpdateLayout();
            VerifyCollageActions(collage);
            Shot(collage, Path.Combine(folder, "08-collage-minimum.png"));
        }
        finally { collage.Close(); }
        Inspect(Construct("ScrollCaptureWindow", new Drawing.Rectangle(30, 30, 800, 500)), "09-scroll-capture");
        CheckPicker(folder);
        var picker = Construct("RecordWindow");
        try
        {
            Inspect(Construct("RecordingWindow", Options(picker), new Drawing.Rectangle(0, 0, 800, 450), folder), "10-recorder-ready");
        }
        finally { picker.Close(); }
        var gifPath = Path.Combine(folder, "appearance-recording.gif");
        var writerType = AppAssembly.GetType("ScreenNote.AnimatedGifWriter")!;
        using (var stream = File.Create(gifPath))
        using (var writer = (IDisposable)Activator.CreateInstance(writerType, Flags, null, new object[] { stream, image.PixelWidth, image.PixelHeight }, null)!)
        {
            var append = writerType.GetMethod("Append", Flags)!;
            for (var i = 0; i < 3; i++) append.Invoke(writer, new object[] { image, 80, 256, 0, 0 });
        }
        var editor = Editor(gifPath);
        try
        {
            editor.Show(); Pump(140); Call(editor, "PausePreview");
            var nav = (RadioButton)Field(editor, "PictureNav");
            Check(Math.Abs(nav.Width - 62.4) < .01, "Recording navigation tiles retain the requested 80 percent width");
            ((RadioButton)Field(editor, "OutputNav")).IsChecked = true;
            nav.IsChecked = true; Pump(40);
            Check(Math.Abs(nav.Width - 62.4) < .01, "Navigation state changes keep compact sizing stable");
            Check(((FrameworkElement)Field(editor, "FilmStrip")).ActualWidth > 0, "Recording timeline is visible");
            ((RadioButton)Field(editor, "RatioWide")).IsChecked = true;
            var crop = (Int32Rect)editor.GetType().GetMethod("EffectiveCrop", Flags)!.Invoke(editor, null)!;
            Check(Math.Abs(crop.Width * 9.0 - crop.Height * 16) <= 16, "Wide aspect control applies an actual 16:9 crop");
            ((RadioButton)Field(editor, "RatioSquare")).IsChecked = true;
            crop = (Int32Rect)editor.GetType().GetMethod("EffectiveCrop", Flags)!.Invoke(editor, null)!;
            Check(crop.Width == crop.Height, "Square aspect control applies an actual square crop");
            ((RadioButton)Field(editor, "RatioOriginal")).IsChecked = true;
            crop = (Int32Rect)editor.GetType().GetMethod("EffectiveCrop", Flags)!.Invoke(editor, null)!;
            Check(crop.Width == image.PixelWidth && crop.Height == image.PixelHeight, "Original aspect restores the full source frame");
            Shot(editor, Path.Combine(folder, "11-record-editor.png"));
            editor.Width = editor.MinWidth; editor.Height = editor.MinHeight; Pump(120);
            Shot(editor, Path.Combine(folder, "12-record-editor-minimum.png"));
        }
        finally { editor.Close(); }
        var captureWindow = new CaptureWindow(image, new Drawing.Rectangle(0, 0, image.PixelWidth, image.PixelHeight));
        try
        {
            captureWindow.Show(); Pump(100);
            Set(captureWindow, "_rect", new Rect(100, 100, Math.Min(620, image.PixelWidth - 130), Math.Min(300, image.PixelHeight - 130)));
            Call(captureWindow, "UpdateSelection");
            Shot(captureWindow, Path.Combine(folder, "13-capture-toolbar.png"));
            Check(((FrameworkElement)Field(captureWindow, "_actions")).IsVisible, "Capture action toolbar is visible after selection");
            var editingTools = (FrameworkElement)Field(captureWindow, "_editTools");
            var tools = (System.Collections.Generic.Dictionary<AnnotationTool, Button>)Field(captureWindow, "_editToolButtons");
            Check(tools.Values.All(button => !Descendants<TextBlock>(button).Any(text => !string.IsNullOrWhiteSpace(text.Text))), "Floating annotation buttons do not force Chinese labels below icons");
            var rowPositions = tools.Values.Select(button => button.TransformToAncestor(editingTools).Transform(new Point()).Y).ToArray();
            Check(rowPositions.Max() - rowPositions.Min() < 1, "Floating annotation tools stay on one row");
            Click(tools[AnnotationTool.Arrow]);
            Check(((AnnotationSurface)Field(captureWindow, "_focusSurface")).Tool == AnnotationTool.Arrow, "Floating arrow icon retains its annotation action");
            foreach (var control in Descendants<Button>(editingTools).Where(button => button.IsVisible))
                Check(WithinWindow(captureWindow, control), "Visible annotation tool fits inside the capture window");
            foreach (var control in Descendants<ComboBox>(editingTools).Where(box => box.IsVisible))
                Check(WithinWindow(captureWindow, control), "Annotation setting fits inside the capture window");
        }
        finally { captureWindow.Close(); }
    }

    private static bool WithinWindow(Window window, FrameworkElement control)
    {
        var root = (FrameworkElement)window.Content;
        var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
        return bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1
            && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }

    private static void VerifyCollageActions(Window collage)
    {
        var repeat = Descendants<CheckBox>(collage).Single(box => box.Content?.ToString() == "连续框选");
        Check(WithinWindow(collage, repeat), "Continuous selection fits inside the collage header");
        foreach (var action in new[] { "保存拼图", "复制拼图", "完成并编辑" })
        {
            var button = Descendants<Button>(collage).Single(candidate => System.Windows.Automation.AutomationProperties.GetName(candidate) == action);
            Check(WithinWindow(collage, button), action + " stays fully visible at " + collage.Width + " × " + collage.Height);
        }
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
