using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenNote;
using Drawing = System.Drawing;

internal static class CompositionValidation
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--compress-file")
        {
            var result = GifCompressor.Compress(args[1], args[2], CancellationToken.None, new InlineProgress<string>(Console.WriteLine));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result)); return 0;
        }
        var folder = Path.GetFullPath(args.FirstOrDefault() ?? ".build/composition-validation"); Directory.CreateDirectory(folder);
        try
        {
            var pixels = new byte[320 * 1800 * 4];
            for (var y = 0; y < 1800; y++) for (var x = 0; x < 320; x++)
            {
                var i = (y * 320 + x) * 4; var value = unchecked((uint)(x * 374761393 + y * 668265263)); value = (value ^ value >> 13) * 1274126177;
                pixels[i] = (byte)value; pixels[i + 1] = (byte)(value >> 8); pixels[i + 2] = (byte)(value >> 16); pixels[i + 3] = 255;
            }
            var page = BitmapSource.Create(320, 1800, 96, 96, PixelFormats.Bgra32, null, pixels, 1280); page.Freeze();
            BitmapSource View(int offset) { var frame = new CroppedBitmap(page, new Int32Rect(0, offset, 320, 240)); frame.Freeze(); return frame; }
            var stitcher = new ScrollStitcher(View(0));
            foreach (var offset in new[] { 96, 207, 291 }) Check(stitcher.Append(View(offset)) == StitchResult.Added, "Downward scroll finds a distinct overlap");
            Check(stitcher.Append(View(291)) == StitchResult.Unchanged, "Stationary page is not duplicated");
            var stitched = stitcher.Export();
            Check(stitched.PixelHeight == 531 && Bytes(stitched).SequenceEqual(Bytes(new CroppedBitmap(page, new Int32Rect(0, 0, 320, 531)))), "Scroll seams retain every source pixel exactly once");
            var height = stitcher.Height;
            Check(stitcher.Append(View(1000)) == StitchResult.NoOverlap && stitcher.Height == height, "Missing overlap never corrupts the existing long image");
            Check(stitcher.Append(View(100)) == StitchResult.NoOverlap && stitcher.Height == height, "Upward scrolling is rejected");
            var textPage = TextPage(); var textStitcher = new ScrollStitcher(new CroppedBitmap(textPage, new Int32Rect(0, 0, 400, 400)));
            Check(textStitcher.Append(new CroppedBitmap(textPage, new Int32Rect(0, 120, 400, 400))) == StitchResult.Added, "Repeated text lines still identify the correct scroll position");
            Save(stitched, Path.Combine(folder, "scroll.png"));

            var first = Solid(200, 100, Colors.Red); var second = Solid(200, 100, Colors.Lime); var third = Solid(200, 100, Colors.Blue);
            var dialogue = ImageCollage.Compose(new[] { first, second, third }, CollageLayout.Subtitles, 200, .2, 16, 0);
            Check(dialogue.PixelWidth == 216 && dialogue.PixelHeight == 140, "Subtitle layout retains the full first image plus bottom strips");
            Check(Pixel(dialogue, 20, 50) == Colors.Red && Pixel(dialogue, 20, 110) == Colors.Lime && Pixel(dialogue, 20, 130) == Colors.Blue, "Subtitle strips retain their order and colors");
            Check(Pixel(dialogue, 2, 110) == Colors.Black && Pixel(dialogue, 210, 110) == Colors.Lime, "Subtitle rows can be staggered horizontally");
            var heightChanged = ImageCollage.Compose(new[] { first, second }, CollageLayout.Subtitles, 200, .4, 0, 0);
            Check(heightChanged.PixelHeight == 140, "Subtitle crop height is adjustable");
            var gradient = View(0);
            var actualCrop = ImageCollage.Compose(new[] { gradient, gradient }, CollageLayout.Subtitles, 320, .25, 0, 0);
            Check(Bytes(new CroppedBitmap(actualCrop, new Int32Rect(0, 240, 320, 60))).SequenceEqual(Bytes(new CroppedBitmap(gradient, new Int32Rect(0, 180, 320, 60)))), "Subtitle cropping uses the bottom of the original image");
            Check(ImageCollage.Compose(new[] { first, second }, CollageLayout.Vertical, 200, gap: 0).PixelHeight == 200, "Vertical collage keeps full images");
            Check(ImageCollage.Compose(new[] { first, second }, CollageLayout.Horizontal, 200, gap: 0).PixelHeight == 50, "Horizontal collage scales images to one row");
            Check(ImageCollage.Compose(new[] { first, second, third }, CollageLayout.Grid, 200, gap: 0).PixelHeight == 100, "Two-column collage handles an incomplete final row");
            Save(dialogue, Path.Combine(folder, "dialogue.png"));

            var original = Path.Combine(folder, "moving.gif");
            using (var stream = File.Create(original)) using (var writer = new AnimatedGifWriter(stream, 320, 240))
                for (var i = 0; i < 15; i++) writer.Append(Moving(i), 10);
            var result = GifCompressor.Compress(original, folder, CancellationToken.None, targetBytes: new FileInfo(original).Length / 3);
            Check(result.Bytes < result.OriginalBytes / 2, "GIF delta frames and reduced palettes substantially reduce size");
            Check(Math.Abs(result.Duration.TotalSeconds - 1.5) < .001, "Compression preserves animation duration");
            using (var compressed = Drawing.Image.FromFile(result.Path))
            {
                var frames = compressed.GetFrameCount(Drawing.Imaging.FrameDimension.Time);
                Check(frames >= 2 && frames == result.Frames, "Compressed GIF remains animated and independently decodable");
                compressed.SelectActiveFrame(Drawing.Imaging.FrameDimension.Time, frames - 1);
                using var final = new Drawing.Bitmap(compressed);
                var pixel = final.GetPixel(final.Width * 150 / 320, final.Height * 100 / 240);
                Check(pixel.R > 180 && pixel.G < 80, "Final animation pose survives frame sampling and delta composition");
            }
            var duplicate = Path.Combine(folder, "duplicate.gif");
            using (var stream = File.Create(duplicate)) using (var writer = new AnimatedGifWriter(stream, 200, 100))
                for (var i = 0; i < 5; i++) writer.Append(first, 10);
            var merged = GifCompressor.Compress(duplicate, folder, CancellationToken.None, targetBytes: 1);
            Check(merged.Frames == 1 && merged.Duration.TotalSeconds == .5, "Identical frames merge without losing playback duration");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { GifCompressor.Compress(original, folder, cancelled.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { Check(true, "Cancelled compression does not modify the original"); }
            Check(!Directory.EnumerateFiles(folder).Any(file => file.EndsWith(".partial") || file.EndsWith(".best")), "Compression leaves no temporary files");
            Check(File.Exists(original) && result.Path != original, "Original GIF is preserved separately from the compressed copy");
            File.WriteAllText(Path.Combine(folder, "result.txt"), $"PASS: {_checks} composition checks.");
            Console.WriteLine($"PASS: {_checks} composition checks. {folder}"); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
    private static BitmapSource Moving(int index)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 320, 240));
            for (var y = 0; y < 240; y += 12) for (var x = 0; x < 320; x += 12) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(x * 7), (byte)(y * 9), (byte)(x + y))), null, new Rect(x, y, 8, 8));
            dc.DrawRectangle(Brushes.Red, null, new Rect(index * 10, 90, 20, 20));
        }
        var image = new RenderTargetBitmap(320, 240, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    private static BitmapSource TextPage()
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 400, 1400));
            for (var i = 0; i < 45; i++) dc.DrawText(new FormattedText($"第 {i:D3} 行：滚动页面的独立内容 Example {i * 79}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 15, Brushes.Black, 1), new Point(15, i * 30 + 4));
        }
        var image = new RenderTargetBitmap(400, 1400, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    private static BitmapSource Solid(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4]; for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
    }
    private static Color Pixel(BitmapSource image, int x, int y) { var pixel = new byte[4]; new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]); }
    private static byte[] Bytes(BitmapSource image) { var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels; }
    private static void Save(BitmapSource image, string path) { using var file = File.Create(path); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(file); }
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); _checks++; Console.WriteLine("PASS: " + message); }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
