using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenNote;
using Drawing = System.Drawing;

internal static class GifRecordingValidation
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        var folder = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(".build", "gif-recording-validation"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "palette-and-timing.gif");
            using (var stream = File.Create(path))
            using (var writer = new AnimatedGifWriter(stream, 64, 48))
            {
                writer.Append(Frame(Colors.Red), 10); writer.Append(Frame(Colors.Lime), 25); writer.Append(Frame(Colors.Blue), 7);
            }
            var decoder = Decode(path);
            Check(decoder.Frames.Count == 3, "All animation frames decode with WIC");
            for (var i = 0; i < 3; i++)
            {
                var frame = decoder.Frames[i];
                Check(frame.PixelWidth == 64 && frame.PixelHeight == 48, "Frame dimensions remain consistent");
                var pixel = new byte[4];
                new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(16, 16, 1, 1), pixel, 4, 0);
                Check(pixel[2 - i] == 255 && pixel.Where((_, index) => index < 3 && index != 2 - i).All(value => value == 0), "Per-frame palettes preserve independent RGB colors");
                Check(Convert.ToInt32(((BitmapMetadata)frame.Metadata).GetQuery("/grctlext/Delay")) == new[] { 10, 25, 7 }[i], "GIF frame delays survive encoding");
            }
            using (var gdi = Drawing.Image.FromFile(path))
                Check(gdi.GetFrameCount(new Drawing.Imaging.FrameDimension(gdi.FrameDimensionsList[0])) == 3, "Independent GDI+ decoder recognizes animation");
            var bytes = File.ReadAllBytes(path);
            Check(System.Text.Encoding.ASCII.GetString(bytes).Contains("NETSCAPE2.0") && bytes[^1] == 0x3b, "Loop extension and GIF trailer exist");

            var data = GifClipboard.CreateData(path);
            Check(data.GetFileDropList().Cast<string>().SequenceEqual(new[] { path }), "Clipboard contains the persistent GIF file");
            Check(!data.GetDataPresent(DataFormats.Bitmap, false) && !data.GetDataPresent(DataFormats.Dib, false), "Clipboard cannot silently replace animation with a still bitmap");
            Check(((MemoryStream)data.GetData("Preferred DropEffect")!).ToArray().SequenceEqual(new byte[] { 1, 0, 0, 0 }), "File transfer requests copy rather than move");
            Expect<FileNotFoundException>(() => GifClipboard.CreateData(path + ".missing"), "Missing files cannot be copied");

            var large = GifRecorder.OutputSize(new Drawing.Size(3840, 2160));
            Check(large == new Drawing.Size(960, 540), "Large areas scale proportionally to a bounded size");
            Check(GifRecorder.OutputSize(new Drawing.Size(200, 300)) == new Drawing.Size(200, 300), "Small areas retain their resolution");
            Expect<ArgumentOutOfRangeException>(() => GifRecorder.OutputSize(new Drawing.Size(0, 10)), "Empty capture areas are rejected");
            using var stop = new CancellationTokenSource();
            var n = 0;
            var timed = GifRecorder.Record(() => Frame(n++ % 2 == 0 ? Colors.Red : Colors.Blue), new Drawing.Size(64, 48), folder, stop.Token, null, TimeSpan.FromMilliseconds(480), long.MaxValue);
            Check(timed.Frames >= 3 && Decode(timed.Path).Frames.Count == timed.Frames, "Timed recording commits every frame");
            Check(timed.Duration.TotalMilliseconds >= 470 && timed.Duration.TotalMilliseconds <= 490, "Duration limit finalizes the selected interval");
            var totalDelay = Decode(timed.Path).Frames.Sum(frame => Convert.ToInt32(((BitmapMetadata)frame.Metadata).GetQuery("/grctlext/Delay")));
            Check(Math.Abs(totalDelay * 10 - timed.Duration.TotalMilliseconds) <= 20, "Animation playback follows wall clock time");
            Check(timed.Bytes == new FileInfo(timed.Path).Length && !Directory.EnumerateFiles(Path.Combine(folder, "recordings"), "*.partial").Any(), "Only complete, accurately measured files are committed");

            using var requestedStop = new CancellationTokenSource();
            var stopped = GifRecorder.Record(() => { requestedStop.Cancel(); return Frame(Colors.Green); }, new Drawing.Size(64, 48), folder, requestedStop.Token, null, TimeSpan.FromSeconds(60), long.MaxValue);
            Check(stopped.Frames == 1 && Decode(stopped.Path).Frames.Count == 1, "Stop requests still finalize the last frame");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Expect<OperationCanceledException>(() => GifRecorder.Record(() => throw new Exception("must not capture"), new Drawing.Size(64, 48), folder, cancelled.Token, null, TimeSpan.FromSeconds(60), long.MaxValue), "Cancellation before recording reads no screen");
            var limited = GifRecorder.Record(() => Frame(Colors.Blue), new Drawing.Size(64, 48), folder, CancellationToken.None, null, TimeSpan.FromSeconds(60), 1);
            Check(limited.Frames == 1 && limited.StopReason.Contains("MB"), "File size limit ends recording cleanly");
            var failureCount = Directory.EnumerateFiles(Path.Combine(folder, "recordings")).Count();
            var reads = 0;
            Expect<IOException>(() => GifRecorder.Record(() => ++reads == 1 ? Frame(Colors.Red) : throw new IOException("injected failure"), new Drawing.Size(64, 48), folder, CancellationToken.None, null, TimeSpan.FromSeconds(60), long.MaxValue), "Capture errors propagate without false success");
            Check(Directory.EnumerateFiles(Path.Combine(folder, "recordings")).Count() == failureCount, "Failed recordings leave no partial GIF");
            var slow = GifRecorder.Record(() => { Thread.Sleep(160); return Frame(Colors.Red); }, new Drawing.Size(64, 48), folder, CancellationToken.None, null, TimeSpan.FromMilliseconds(650), long.MaxValue);
            totalDelay = Decode(slow.Path).Frames.Sum(frame => Convert.ToInt32(((BitmapMetadata)frame.Metadata).GetQuery("/grctlext/Delay")));
            Check(Math.Abs(totalDelay * 10 - slow.Duration.TotalMilliseconds) <= 20, "Slow captures retain real playback timing");

            if (args.Contains("--desktop"))
            {
                using var realStop = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
                var real = GifRecorder.RecordAsync(new Drawing.Rectangle(0, 0, 160, 100), folder, realStop.Token).GetAwaiter().GetResult();
                Check(Decode(real.Path).Frames.Count >= 2, "Actual desktop capture, cursor and scaling produce an animated file");
            }
            File.WriteAllText(Path.Combine(folder, "result.txt"), $"PASS: {_checks} GIF recording checks.");
            Console.WriteLine($"PASS: {_checks} GIF recording checks. Artifacts: {folder}"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static BitmapSource Frame(Color color)
    {
        var pixels = new byte[64 * 48 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 256); image.Freeze(); return image;
    }
    private static GifBitmapDecoder Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++; Console.WriteLine("PASS: " + message);
    }
    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { Check(true, message); return; }
        throw new InvalidOperationException(message);
    }
}
