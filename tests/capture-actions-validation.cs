using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenNote;

internal static class CaptureActionsValidation
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [STAThread]
    private static int Main()
    {
        try
        {
            var pixels = new byte[64 * 48 * 4];
            for (var y = 0; y < 48; y++)
            for (var x = 0; x < 64; x++)
            {
                var index = (y * 64 + x) * 4;
                pixels[index] = (byte)x; pixels[index + 1] = (byte)y;
                pixels[index + 2] = (byte)(x + y); pixels[index + 3] = 255;
            }
            var source = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 256);
            source.Freeze();
            // Constructor also works for existing self-tests without App.xaml resources.
            var fallback = new CaptureWindow(source);
            Assert(fallback.Action == CaptureAction.None && fallback.CapturedImage is null,
                "New selector started with a completed capture.");
            Assert(Buttons(fallback).Length == 9 && Buttons(fallback).Skip(2).Take(6).Select(System.Windows.Automation.AutomationProperties.GetName).SequenceEqual(new[] { "发送给 AI", "图片 OCR", "高亮选框", "翻译", "录制 GIF", "滚动截图" }),
                "The floating selector must expose the direct actions without Summary alongside Copy/Edit/Cancel.");
            Assert(((SolidColorBrush)Buttons(fallback)[0].Background).Color == Color.FromRgb(35, 120, 75)
                && ((SolidColorBrush)Buttons(fallback)[1].Foreground).Color == Color.FromRgb(140, 97, 22)
                && ((SolidColorBrush)Buttons(fallback)[8].Foreground).Color == Color.FromRgb(181, 76, 69),
                "Copy, Edit and Cancel must use green, yellow and red.");
            var toolbar = (Border)typeof(CaptureWindow).GetField("_actions", PrivateInstance)!.GetValue(fallback)!;
            Assert(((SolidColorBrush)toolbar.Background).Color == Colors.White
                && ((SolidColorBrush)toolbar.BorderBrush).Color == Color.FromRgb(221, 223, 227)
                && toolbar.CornerRadius == new CornerRadius(10), "Toolbar does not use the neutral visual style.");
            var overlay = (Border)((Grid)fallback.Content).Children[1];
            Assert(((SolidColorBrush)overlay.Background).Color == Color.FromArgb(70, 0, 0, 0),
                "Capture mask is not translucent pure black.");
            fallback.Close();
            VerifyOriginalColorSelection(1);
            VerifyOriginalColorSelection(2);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var primaryStyle = new Style(typeof(Button));
            primaryStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Green));
            app.Resources["PrimaryButton"] = primaryStyle;
            var selection = new Rect(10, 7, 24, 18);
            var copied = Run(source, selection, window => Click(window, 0));
            CheckCrop(copied, CaptureAction.Copy, source, new Int32Rect(10, 7, 24, 18));
            var edited = Run(source, selection, window => Click(window, 1));
            CheckCrop(edited, CaptureAction.Edit, source, new Int32Rect(10, 7, 24, 18));
            var downloaded = Run(source, selection, window => Complete(window, CaptureAction.Download));
            CheckCrop(downloaded, CaptureAction.Download, source, new Int32Rect(10, 7, 24, 18));
            foreach (var item in new[] { (3, CaptureAction.Ocr), (5, CaptureAction.Translate) })
                CheckCrop(Run(source, selection, window => Click(window, item.Item1)), item.Item2, source, new Int32Rect(10, 7, 24, 18));
            foreach (var (index, target) in new[] { (0, "deepseek"), (1, "doubao"), (2, "chatgpt") })
            {
                var delivered = Run(source, selection, window =>
                {
                    Click(window, 2);
                    var menu = Buttons(window)[2].ContextMenu!;
                    Assert(menu.IsOpen && window.DialogResult is null, "Send must open a target menu instead of completing capture immediately.");
                    ((MenuItem)menu.Items[index]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                });
                CheckCrop(delivered, CaptureAction.SendAi, source, new Int32Rect(10, 7, 24, 18));
                Assert(delivered.Window.SelectedAiTarget == target, "Target chosen in the dropdown was not retained.");
            }
            var focused = Run(source, selection, window =>
            {
                Click(window, 4);
                Assert(window.DialogResult is null && window.Action == CaptureAction.None, "Highlight must work inside the selector without completing capture.");
                var surface = (AnnotationSurface)typeof(CaptureWindow).GetField("_focusSurface", PrivateInstance)!.GetValue(window)!;
                var json = System.Text.Json.Nodes.JsonNode.Parse(surface.ExportDocument())!;
                json["Marks"] = System.Text.Json.JsonSerializer.SerializeToNode(new[] { new { Id = Guid.NewGuid(), Tool = AnnotationTool.HighlightBox, Start = new { X = 5, Y = 4 }, End = new { X = 19, Y = 14 }, Points = Array.Empty<object>(), Color = "#FF4B7DEE", Width = 3, TextSize = 22, TextWidth = 400, Text = "", Number = 0 } });
                surface.RestoreDocument(surface.Source!, json.ToJsonString());
                Click(window, 0);
            });
            Assert(focused.Window.CapturedDocumentJson.Length > 0 && focused.Window.OriginalCapturedImage!.PixelWidth == 24, "Highlight capture must preserve the original crop and editable vector layers.");
            Assert(!Pixels(focused.Window.CapturedImage!).SequenceEqual(Pixels(focused.Window.OriginalCapturedImage!)), "Copied highlight must include the visible focus treatment.");
            var enter = Run(source, selection, window => SendKey(window, Key.Enter));
            CheckCrop(enter, CaptureAction.Copy, source, new Int32Rect(10, 7, 24, 18));
            var enterOverEdit = Run(source, selection, window =>
            {
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Buttons(window)[1].RaiseEvent(key);
                Assert(key.Handled, "Enter was not intercepted before a focused edit button handled it.");
            });
            CheckCrop(enterOverEdit, CaptureAction.Copy, source, new Int32Rect(10, 7, 24, 18));
            var controlE = Run(source, selection, window => SendKey(window, Key.E, ModifierKeys.Control));
            CheckCrop(controlE, CaptureAction.Edit, source, new Int32Rect(10, 7, 24, 18));
            // The double-click handler calls Complete() with its default argument.
            var doubleClickDefault = Run(source, selection, window =>
                typeof(CaptureWindow).GetMethod("Complete", PrivateInstance)!.Invoke(window, new object[] { Type.Missing }));
            CheckCrop(doubleClickDefault, CaptureAction.Copy, source, new Int32Rect(10, 7, 24, 18));
            var gif = Run(source, selection, window => Click(window, 6));
            Assert(gif.Completed == true && gif.Window.Action == CaptureAction.RecordGif && gif.Window.CapturedImage == null
                && gif.Window.SelectedScreenBounds == new System.Drawing.Rectangle(10, 7, 24, 18), "GIF must return screen bounds instead of a frozen screenshot.");
            var gifKey = Run(source, selection, window => SendKey(window, Key.G, ModifierKeys.Control));
            Assert(gifKey.Window.Action == CaptureAction.RecordGif, "Ctrl+G did not select GIF recording.");
            var negativeOrigin = Run(source, selection, window => Click(window, 6), new System.Drawing.Rectangle(-1920, -200, 64, 48));
            Assert(negativeOrigin.Window.SelectedScreenBounds == new System.Drawing.Rectangle(-1910, -193, 24, 18), "GIF bounds lost the virtual desktop's negative origin.");
            var scroll = Run(source, selection, window => Click(window, 7));
            Assert(scroll.Completed == true && scroll.Window.Action == CaptureAction.Scroll && scroll.Window.CapturedImage == null
                && scroll.Window.SelectedScreenBounds == new System.Drawing.Rectangle(10, 7, 24, 18), "Scroll action must return the selected screen region.");
            var cancelled = Run(source, selection, window => Click(window, 8));
            CheckCancelled(cancelled);
            CheckCancelled(Run(source, selection, window => SendKey(window, Key.Escape)));
            CheckCancelled(Run(source, selection, window =>
            {
                window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = Mouse.MouseDownEvent, Source = window });
            }));
            foreach (var tiny in new[] { new Rect(10, 7, 0, 0), new Rect(10, 7, 1, 18), new Rect(10, 7, 24, 1) })
            {
                foreach (var action in new[] { CaptureAction.Copy, CaptureAction.Edit })
                    CheckCancelled(Run(source, tiny, window =>
                    {
                        Complete(window, action);
                        Assert(window.DialogResult is null && window.Action == CaptureAction.None && window.CapturedImage is null,
                            "Invalid selection completed the dialog or produced a crop.");
                    }));
            }
            foreach (var action in new[] { CaptureAction.None, (CaptureAction)9, (CaptureAction)999 })
                CheckCancelled(Run(source, selection, window =>
                {
                    Complete(window, action);
                    Assert(window.DialogResult is null && window.CapturedImage is null,
                        "An unsupported action completed a capture.");
                }));
            CheckCancelled(Run(source, selection, window =>
            {
                Assert(SendKey(window, Key.E), "E should activate the ellipse tool.");
                Assert(window.Action == CaptureAction.None && window.CapturedImage is null,
                    "Selecting the ellipse tool completed capture.");
            }));
            Console.WriteLine("PASS: floating action toolbar, completion actions, colored single-row actions, GIF/scroll bounds and Ctrl+G, editable focus, original crop, 100/200-percent DPI, dispatch and cancellation; synthetic bitmap only.");
            app.Shutdown();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static Result Run(BitmapSource source, Rect selection, Action<CaptureWindow> action, System.Drawing.Rectangle? bounds = null)
    {
        var window = new CaptureWindow(source, bounds) { Opacity = 0, ShowActivated = false };
        Exception? failure = null;
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                typeof(CaptureWindow).GetField("_rect", PrivateInstance)!.SetValue(window, selection);
                typeof(CaptureWindow).GetMethod("UpdateSelection", PrivateInstance)!.Invoke(window, null);
                action(window);
            }
            catch (Exception exception) { failure = exception; }
            finally { if (window.IsVisible) window.DialogResult = false; }
        }));
        var completed = window.ShowDialog();
        if (failure is not null) throw new InvalidOperationException("Capture action validation failed.", failure);
        return new Result(window, completed);
    }

    private static Button[] Buttons(CaptureWindow window)
    {
        var actions = (System.Collections.Generic.Dictionary<CaptureAction, Button>)typeof(CaptureWindow).GetField("_actionButtons", PrivateInstance)!.GetValue(window)!;
        var tools = (StackPanel)typeof(CaptureWindow).GetField("_actionPanel", PrivateInstance)!.GetValue(window)!;
        return new[] { actions[CaptureAction.Copy], actions[CaptureAction.Edit] }.Concat(tools.Children.Cast<Button>())
            .Append((Button)typeof(CaptureWindow).GetField("_cancelButton", PrivateInstance)!.GetValue(window)!).ToArray();
    }

    private static void Click(CaptureWindow window, int index) =>
        Buttons(window)[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool SendKey(CaptureWindow window, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        (bool)typeof(CaptureWindow).GetMethod("HandleKey", PrivateInstance)!.Invoke(window, new object[] { key, modifiers })!;

    private static void Complete(CaptureWindow window, CaptureAction action) =>
        typeof(CaptureWindow).GetMethod("Complete", PrivateInstance)!.Invoke(window, new object[] { action });

    private static void VerifyOriginalColorSelection(double scale)
    {
        const int width = 320, height = 240;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            pixels[index] = (byte)(100 + x % 100);
            pixels[index + 1] = (byte)(120 + y % 100);
            pixels[index + 2] = (byte)(150 + (x + y) % 90);
            pixels[index + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        var window = new CaptureWindow(source);
        var grid = (Grid)window.Content;
        var size = new Size(width / scale, height / scale);
        grid.Measure(size); grid.Arrange(new Rect(size)); grid.UpdateLayout();
        var canvas = (Canvas)grid.Children[2];
        void Select(Rect rectangle)
        {
            typeof(CaptureWindow).GetField("_rect", PrivateInstance)!.SetValue(window, rectangle);
            typeof(CaptureWindow).GetMethod("UpdateSelection", PrivateInstance)!.Invoke(window, null);
            // Isolate the image and actual overlay, keeping selection geometry/layout active.
            // Labels, borders and handles deliberately paint their own colors over the screen.
            foreach (UIElement child in canvas.Children) child.Visibility = Visibility.Hidden;
            grid.UpdateLayout();
        }
        BitmapSource Render(bool expandedLayout = false)
        {
            var factor = expandedLayout ? 2 : 1;
            var rendered = new RenderTargetBitmap(width * factor, height * factor, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rendered.Render(grid); rendered.Freeze(); return rendered;
        }
        Rect Scaled(double x, double y, double w, double h) => new(x / scale, y / scale, w / scale, h / scale);
        Select(new Rect(0, 0, 0, 0));
        CheckVisualPixel(Render(), source, 64, 64, dark: true, "Initial screen must be dimmed.");
        var first = Scaled(32, 32, 80, 64);
        Select(first);
        var actual = Render();
        CheckVisualPixel(actual, source, 64, 64, dark: false, "Selected interior must retain original screenshot colors.");
        CheckVisualPixel(actual, source, 190, 140, dark: true, "Selection exterior must stay dimmed.");
        var moved = Scaled(150, 110, 80, 64);
        Select(moved);
        actual = Render();
        CheckVisualPixel(actual, source, 64, 64, dark: true, "Moving must dim the previous selected region.");
        CheckVisualPixel(actual, source, 190, 140, dark: false, "Moving must reveal the new region's original colors.");
        var enlarged = CaptureWindow.ResizeSelection(moved, new Vector(50 / scale, 40 / scale),
            CaptureWindow.ResizeEdges.Right | CaptureWindow.ResizeEdges.Bottom, size.Width, size.Height);
        Select(enlarged);
        CheckVisualPixel(Render(), source, 260, 200, dark: false, "Resizing outward must extend the original-color region.");
        var reduced = CaptureWindow.ResizeSelection(enlarged, new Vector(-70 / scale, -60 / scale),
            CaptureWindow.ResizeEdges.Right | CaptureWindow.ResizeEdges.Bottom, size.Width, size.Height);
        Select(reduced);
        actual = Render();
        CheckVisualPixel(actual, source, 170, 125, dark: false, "Shrinking must retain the remaining interior's original colors.");
        CheckVisualPixel(actual, source, 250, 190, dark: true, "Shrinking must dim the removed region.");
        Select(Scaled(32, 32, 1, 1));
        CheckVisualPixel(Render(), source, 170, 125, dark: true, "An invalid selection must remove the old mask hole.");
        Select(first);
        var expandedSize = new Size(size.Width * 2, size.Height * 2);
        grid.Measure(expandedSize); grid.Arrange(new Rect(expandedSize)); grid.UpdateLayout();
        // This time there is no explicit UpdateSelection: the canvas size event must refresh
        // the mask, as occurs when the screenshot window changes monitor/DPI layout.
        foreach (UIElement child in canvas.Children) child.Visibility = Visibility.Hidden;
        grid.UpdateLayout();
        var shade = (Border)grid.Children[1];
        shade.Visibility = Visibility.Hidden;
        var unshadedLayout = Render(expandedLayout: true);
        shade.Visibility = Visibility.Visible;
        var shadedLayout = Render(expandedLayout: true);
        CheckVisualPixel(shadedLayout, unshadedLayout, 64, 64, dark: false,
            "Layout changes must retain the selection's clear region without pointer movement.");
        CheckVisualPixel(shadedLayout, unshadedLayout, 500, 300, dark: true,
            "Layout changes must darken newly added screen area without pointer movement.");
        window.Close();
    }

    private static void CheckVisualPixel(BitmapSource actual, BitmapSource original, int x, int y, bool dark, string message)
    {
        var shown = new byte[4]; var expected = new byte[4];
        actual.CopyPixels(new Int32Rect(x, y, 1, 1), shown, 4, 0);
        original.CopyPixels(new Int32Rect(x, y, 1, 1), expected, 4, 0);
        for (var channel = 0; channel < 3; channel++)
        {
            var value = dark ? (int)Math.Round(expected[channel] * (255 - 70) / 255d) : expected[channel];
            Assert(Math.Abs(shown[channel] - value) <= (dark ? 1 : 0), message
                + $" Pixel ({x},{y}), channel {channel}: expected {value}, actual {shown[channel]}.");
        }
        Assert(shown[3] == 255, "Screenshot overlay changed image opacity.");
    }

    private static void CheckCrop(Result result, CaptureAction action, BitmapSource source, Int32Rect region)
    {
        Assert(result.Completed == true && result.Window.Action == action && result.Window.CapturedImage is { IsFrozen: true },
            "Completion did not return a frozen crop and the correct action.");
        var image = result.Window.CapturedImage!;
        Assert(image.PixelWidth == region.Width && image.PixelHeight == region.Height,
            "Crop dimensions differ from the selected source region.");
        var actual = new byte[region.Width * region.Height * 4];
        var expected = new byte[actual.Length];
        image.CopyPixels(actual, region.Width * 4, 0);
        source.CopyPixels(region, expected, region.Width * 4, 0);
        Assert(actual.SequenceEqual(expected), "Capture action changed the source pixels.");
    }

    private static void CheckCancelled(Result result) => Assert(result.Completed == false
        && result.Window.Action == CaptureAction.None && result.Window.CapturedImage is null,
        "Cancellation produced a capture or successful DialogResult.");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }

    private sealed record Result(CaptureWindow Window, bool? Completed);
}
