using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenNote;

internal static class MediaRoundtrip
{
    private static readonly Assembly AppAssembly = typeof(MainWindow).Assembly;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        NativeErrors.SuppressDialogs();
        var directory = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source-with-audio.mp4");
            Fixture(source, true);
            Check(Tracks(source).Contains("soun"), "Fixture contains an audio track");
            var sourceType = AppAssembly.GetType("ScreenNote.RecordSource")!;
            using var opened = (IDisposable)sourceType.GetMethod("Open", PrivateStatic)!.Invoke(null, new object[] { source })!;
            var poster = (BitmapSource)sourceType.GetProperty("Poster", PrivateInstance)!.GetValue(opened)!;
            var top = Pixel(poster, 16, 12); var bottom = Pixel(poster, 16, 84);
            Check(top[2] > top[0] + 80 && bottom[0] > bottom[2] + 80, "Video preview preserves top and bottom orientation");
            var crop = new Int32Rect(8, 8, 96, 64);
            var editor = typeof(RecordEditorWindow);
            for (var index = 0; index < 3; index++)
            {
                var output = Path.Combine(directory, "edited-" + index + ".mp4");
                editor.GetMethod("ExportVideo", PrivateStatic)!.Invoke(null, new object?[] {source, output, TimeSpan.FromSeconds(.25), TimeSpan.FromSeconds(1.25), crop, 30, null});
                var tracks = Tracks(output);
                Check(tracks.Contains("vide") && tracks.Contains("soun"), "Trimmed MP4 retains both video and audio, pass " + (index + 1));
                using var exported = (IDisposable)sourceType.GetMethod("Open", PrivateStatic)!.Invoke(null, new object[] { output })!;
                var size = (System.Drawing.Size)sourceType.GetProperty("Size", PrivateInstance)!.GetValue(exported)!;
                var duration = (TimeSpan)sourceType.GetProperty("Duration", PrivateInstance)!.GetValue(exported)!;
                Check(size.Width == 96 && size.Height == 64, "MP4 spatial crop is applied");
                Check(Math.Abs(duration.TotalSeconds - 1) < .1, "MP4 temporal trim keeps one second");
            }
            var gif = Path.Combine(directory, "edited.gif");
            editor.GetMethod("ExportGif", PrivateStatic)!.Invoke(null, new object?[] {source, gif, TimeSpan.FromSeconds(.25), TimeSpan.FromSeconds(1.25), crop, 15, 10L * 1024 * 1024, null});
            using (var stream = File.OpenRead(gif))
            {
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check(decoder.Frames.Count is >= 14 and <= 16, "MP4 converts into a one-second 15 fps animated GIF");
                Check(decoder.Frames[0].PixelWidth == 96 && decoder.Frames[0].PixelHeight == 64, "GIF spatial crop is applied");
            }
            var silent = Path.Combine(directory, "silent.mp4");
            Fixture(silent, false);
            var silentOutput = Path.Combine(directory, "silent-edited.mp4");
            editor.GetMethod("ExportVideo", PrivateStatic)!.Invoke(null, new object?[] {silent, silentOutput, TimeSpan.Zero, TimeSpan.FromSeconds(1), crop, 24, null});
            Check(Tracks(silentOutput).SetEquals(new[] { "vide" }), "Silent recordings export without a fabricated audio track");
            var reader = sourceType.GetField("_mp4", PrivateInstance)!.GetValue(opened)!;
            foreach (var second in new[] { .9, .1, 1.5, 0.0 })
                Check(reader.GetType().GetMethod("ReadAt", PrivateInstance)!.Invoke(reader, new object[] {TimeSpan.FromSeconds(second)}) is BitmapSource, "Repeated forward/backward seeks remain usable");
            CheckUnalignedFrames(directory, sourceType, editor);
            File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: " + _checks + " media round-trip checks.");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString());
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CheckUnalignedFrames(string directory, Type sourceType, Type editor)
    {
        const int width = 1018, height = 604;
        var source = Path.Combine(directory, "unaligned.mp4");
        Fixture(source, true, width, height);
        var open = sourceType.GetMethod("Open", PrivateStatic)!;
        var poster = sourceType.GetProperty("Poster", PrivateInstance)!;
        using (var recording = (IDisposable)open.Invoke(null, new object[] { source })!)
            CheckPattern((BitmapSource)poster.GetValue(recording)!, 0, 0, "Unaligned source decodes without shifted rows");

        foreach (var crop in new[] { new Int32Rect(0, 0, width, height), new Int32Rect(16, 12, 958, 574) })
        {
            var output = Path.Combine(directory, "unaligned-" + crop.Width + ".mp4");
            editor.GetMethod("ExportVideo", PrivateStatic)!.Invoke(null,
                new object?[] { source, output, TimeSpan.FromSeconds(.25), TimeSpan.FromSeconds(1.25), crop, 30, null });
            using var exported = (IDisposable)open.Invoke(null, new object[] { output })!;
            var frame = (BitmapSource)poster.GetValue(exported)!;
            Check(frame.PixelWidth == crop.Width && frame.PixelHeight == crop.Height && Tracks(output).Contains("soun"),
                "Unaligned export preserves requested dimensions and audio: " + crop.Width);
            CheckPattern(frame, crop.X, crop.Y, "Unaligned export preserves pixel positions: " + crop.Width);
        }

        static void CheckPattern(BitmapSource frame, int offsetX, int offsetY, string description)
        {
            foreach (var y in new[] { 40, 180, 270, 350, 440, 560 })
            {
                var left = Pixel(frame, width / 2 - 16 - offsetX, y - offsetY);
                var right = Pixel(frame, width / 2 + 16 - offsetX, y - offsetY);
                var channel = y < height / 2 ? 2 : 0;
                Check(left[channel] > 200 && left[2 - channel] < 45 && left[1] < 45
                    && right[1] > 200 && right[0] < 45 && right[2] < 45, description + ", row " + y);
            }
        }
    }

    private static void Fixture(string path, bool audio, int width = 128, int height = 96)
    {
        var type = AppAssembly.GetType("ScreenNote.Mp4Writer")!;
        var writer = (IDisposable)Activator.CreateInstance(type, PrivateInstance, null, new object[] {path, width, height, 30, audio ? 48000 : 0, 24000}, null)!;
        try
        {
            var visual = new DrawingVisual();
            using (var draw = visual.RenderOpen())
            {
                draw.DrawRectangle(Brushes.Red, null, new Rect(0, 0, width, height / 2));
                draw.DrawRectangle(Brushes.Blue, null, new Rect(0, height / 2, width, height / 2));
                draw.DrawRectangle(Brushes.Lime, null, new Rect(width / 2, 0, width / 2, height));
            }
            var frame = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); frame.Render(visual); frame.Freeze();
            var video = type.GetMethod("WriteVideoFrame", PrivateInstance, null, new[] {typeof(BitmapSource), typeof(long), typeof(long)}, null)!;
            var sound = type.GetMethod("WriteAudioPcm", PrivateInstance)!;
            for (var index = 0; index < 60; index++)
            {
                var time = index * TimeSpan.TicksPerSecond / 30;
                var next = (index + 1L) * TimeSpan.TicksPerSecond / 30;
                video.Invoke(writer, new object[] {frame, time, next - time});
                if (audio)
                {
                    var pcm = new byte[1600 * 4];
                    for (var sample = 0; sample < 1600; sample++)
                    {
                        var value = (short)(Math.Sin(2 * Math.PI * 440 * (index * 1600L + sample) / 48000) * 10000);
                        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 4), value);
                        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 4 + 2), value);
                    }
                    sound.Invoke(writer, new object[] {pcm, time, next - time});
                }
            }
            type.GetMethod("Complete", PrivateInstance)!.Invoke(writer, null);
        }
        finally { writer.Dispose(); writer.Dispose(); }
    }

    private static HashSet<string> Tracks(string path)
    {
        var bytes = File.ReadAllBytes(path); var result = new HashSet<string>();
        void Walk(int start, int end)
        {
            for (var offset = start; offset + 8 <= end;)
            {
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
                if (size < 8 || offset + size > end) break;
                var kind = Encoding.ASCII.GetString(bytes, offset + 4, 4);
                if (kind == "hdlr" && size >= 20) result.Add(Encoding.ASCII.GetString(bytes, offset + 16, 4));
                if (kind is "moov" or "trak" or "mdia") Walk(offset + 8, offset + size);
                offset += size;
            }
        }
        Walk(0, bytes.Length); return result;
    }

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4]; new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel;
    }
    private static void Check(bool passed, string description)
    {
        if (!passed) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + ++_checks + ": " + description);
    }
}
