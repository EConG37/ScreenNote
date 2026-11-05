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

internal static class VideoRecordingValidation
{
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        NativeErrors.SuppressDialogs();
        var folder = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(".build", "video-recording-validation"));
        Directory.CreateDirectory(folder);
        try
        {
            Check(VideoRecorder.OutputSize(new Drawing.Size(333, 257)) == new Drawing.Size(332, 256), "Odd capture sizes round down to even H.264 dimensions");
            Check(VideoRecorder.OutputSize(new Drawing.Size(3840, 2160)) == new Drawing.Size(960, 540), "Large areas keep the shared 960px cap");
            Check(VideoRecorder.OutputSize(new Drawing.Size(200, 300)) == new Drawing.Size(200, 300), "Small even areas retain their resolution");
            Expect<ArgumentOutOfRangeException>(() => VideoRecorder.OutputSize(new Drawing.Size(0, 10)), "Empty capture areas are rejected");
            Expect<ArgumentOutOfRangeException>(() => new Mp4Writer(Path.Combine(folder, "odd.mp4"), 333, 100, 30), "Odd frame dimensions are rejected before encoding");

            var path = Path.Combine(folder, "writer-check.mp4");
            using (var writer = new Mp4Writer(path, 128, 96, 30, 48000))
            {
                var frameTicks = TimeSpan.TicksPerSecond / 30;
                for (var i = 0; i < 60; i++)
                    writer.WriteVideoFrame(Frame(i % 2 == 0 ? Colors.Red : Colors.SteelBlue, 128, 96), (long)i * frameTicks, frameTicks);
                var tone = Tone(48000, 48000 * 2, 440);
                writer.WriteAudioPcm(tone, 0, (long)(tone.Length / 4) * TimeSpan.TicksPerSecond / 48000);
                writer.Complete();
            }
            var bytes = File.ReadAllBytes(path);
            Check(bytes.Length > 10_000, "Encoded MP4 carries real payload");
            Check(Ascii(bytes, 4, 4) == "ftyp", "MP4 starts with an ftyp box");
            Check(ContainsAscii(bytes, "moov") && ContainsAscii(bytes, "mdat"), "MP4 holds both the media data and its index");
            Check(ContainsAscii(bytes, "avc1") && ContainsAscii(bytes, "mp4a"), "H.264 video and AAC audio streams both landed");
            var writtenDuration = MvhdDurationSeconds(bytes);
            Check(writtenDuration >= 1.8 && writtenDuration <= 2.2, "Writer duration follows the sample timestamps");

            using (var stop = new CancellationTokenSource())
            {
                var n = 0;
                var result = VideoRecorder.Record(() => Frame(n++ % 2 == 0 ? Colors.Lime : Colors.Black, 64, 48), new Drawing.Size(64, 48), folder,
                    null, stop.Token, null, TimeSpan.FromMilliseconds(500), long.MaxValue);
                Check(result.Frames >= 10 && File.Exists(result.Path), "Timed video recording commits frames");
                Check(result.Duration.TotalMilliseconds >= 450 && result.Duration.TotalMilliseconds <= 550, "Duration limit finalizes the selected interval");
                Check(result.PosterFrame != null, "First frame is kept as the poster");
                Check(result.AudioNote.Length == 0, "No audio requested, no audio note");
                var recordedBytes = File.ReadAllBytes(result.Path);
                Check(Ascii(recordedBytes, 4, 4) == "ftyp", "Recording is a valid MP4");
                Check(Math.Abs(MvhdDurationSeconds(recordedBytes) - result.Duration.TotalSeconds) < 0.25, "MP4 duration matches the wall clock");
            }

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Expect<OperationCanceledException>(() => VideoRecorder.Record(() => Frame(Colors.White, 64, 48), new Drawing.Size(64, 48), folder,
                    null, cancelled.Token, null, TimeSpan.FromSeconds(1), long.MaxValue), "Cancelled recording stops before writing");
            }
            Check(!Directory.EnumerateFiles(folder, "*.partial").Any(), "Cancelled or failed recordings leave no partial files");

            // The production path end to end: real desktop region capture plus loopback audio, stopped early.
            using (var stopwatch = new CancellationTokenSource())
            {
                stopwatch.CancelAfter(800);
                var real = VideoRecorder.RecordAsync(new Drawing.Rectangle(0, 0, 320, 240), Path.Combine(folder, "real"),
                    systemSound: true, microphone: false, stopwatch.Token).GetAwaiter().GetResult();
                Check(real.Frames >= 5 && File.Exists(real.Path), "Real desktop region records through the full pipeline");
                Check(real.Bytes == new FileInfo(real.Path).Length, "Recorded size matches the file on disk");
                var realBytes = File.ReadAllBytes(real.Path);
                Check(Ascii(realBytes, 4, 4) == "ftyp" && ContainsAscii(realBytes, "avc1"), "Real recording is a playable H.264 MP4");
            }

            // Availability probes must never throw, with or without audio hardware.
            _ = WasapiAudio.SystemSoundAvailable;
            _ = WasapiAudio.MicrophoneAvailable;
            Check(true, "Audio device probing is safe on any machine");

            var pauseClock = Stopwatch.StartNew();
            var paused = VideoRecorder.Record(() => Frame(Colors.Teal, 128, 96), new Drawing.Size(128, 96), folder, null,
                CancellationToken.None, null, TimeSpan.FromMilliseconds(400), long.MaxValue, 30,
                () => pauseClock.ElapsedMilliseconds is >= 100 and < 400);
            Check(pauseClock.Elapsed.TotalMilliseconds >= 650, "Pause increases wall time without extending the clip");
            Check(Math.Abs(paused.Duration.TotalSeconds - .4) < .08, "Paused video retains only active duration");
            Console.WriteLine($"video-recording-validation: all {_checks} checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("video-recording-validation failed: " + ex);
            return 1;
        }
    }

    private static BitmapSource Frame(Color color, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Tone(int rate, int frames, double frequency)
    {
        var pcm = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * frequency * i / rate) * 12000);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4, 2), value);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2, 2), value);
        }
        return pcm;
    }

    private static string Ascii(byte[] bytes, int index, int count) => System.Text.Encoding.ASCII.GetString(bytes, index, count);

    private static bool ContainsAscii(byte[] bytes, string text) => IndexOfAscii(bytes, text) >= 0;

    private static int IndexOfAscii(byte[] bytes, string text)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(text);
        for (var i = 0; i + needle.Length <= bytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++) if (bytes[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private static double MvhdDurationSeconds(byte[] bytes)
    {
        var index = IndexOfAscii(bytes, "mvhd");
        if (index < 0) throw new InvalidOperationException("mvhd box missing");
        var version = bytes[index + 4];
        var timescale = ReadUInt32(bytes, index + 4 + 4 + (version == 1 ? 16 : 8));
        var duration = ReadUInt32(bytes, index + 4 + 4 + (version == 1 ? 16 : 8) + 4);
        return (double)duration / timescale;
    }

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];

    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException("Check failed: " + name);
        Console.WriteLine("ok " + _checks + " - " + name);
    }

    private static void Expect<T>(Action action, string name) where T : Exception
    {
        _checks++;
        try { action(); }
        catch (T) { Console.WriteLine("ok " + _checks + " - " + name); return; }
        throw new InvalidOperationException("Check failed: " + name);
    }
}
