using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace ScreenNote;

internal sealed record GifRecordingProgress(TimeSpan Elapsed, int Frames, long Bytes);
internal sealed record GifRecordingResult(string Path, TimeSpan Duration, int Frames, long Bytes, string StopReason);

internal static class GifRecorder
{
    internal const int FramesPerSecond = 10;
    internal const int MaximumEdge = 960;
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(60);
    internal const long MaximumBytes = 50 * 1024 * 1024;

    internal static Drawing.Size OutputSize(Drawing.Size source)
    {
        if (source.Width < 2 || source.Height < 2) throw new ArgumentOutOfRangeException(nameof(source));
        var scale = Math.Min(1d, MaximumEdge / (double)Math.Max(source.Width, source.Height));
        return new Drawing.Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
    }

    internal static Task<GifRecordingResult> RecordAsync(Drawing.Rectangle region, string dataDirectory,
        CancellationToken stop, IProgress<GifRecordingProgress>? progress = null) => Task.Run(() =>
    {
        using var capture = new RegionCapture(region);
        return Record(capture.NextFrame, capture.Size, dataDirectory, stop, progress, MaximumDuration, MaximumBytes);
    });

    // The injectable frame source lets the complete recording path be tested without reading the desktop.
    internal static GifRecordingResult Record(Func<BitmapSource> nextFrame, Drawing.Size size, string dataDirectory,
        CancellationToken stop, IProgress<GifRecordingProgress>? progress, TimeSpan maximumDuration, long maximumBytes)
    {
        stop.ThrowIfCancellationRequested();
        var folder = Path.Combine(dataDirectory, "recordings");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "拾屏-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".gif");
        var partial = path + ".partial";
        try
        {
            GifRecordingResult result;
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var writer = new AnimatedGifWriter(output, size.Width, size.Height);
                using (writer)
                {
                    var clock = Stopwatch.StartNew();
                    var capturedAt = TimeSpan.Zero;
                    var pending = nextFrame();
                    long previousTicks = 0;
                    var reason = "";
                    TimeSpan endedAt;
                    while (true)
                    {
                        var wait = TimeSpan.FromSeconds(1d / FramesPerSecond) - (clock.Elapsed - capturedAt);
                        if (wait > TimeSpan.Zero) stop.WaitHandle.WaitOne(wait);
                        endedAt = clock.Elapsed < maximumDuration ? clock.Elapsed : maximumDuration;
                        var finishing = stop.IsCancellationRequested || endedAt >= maximumDuration || output.Position >= maximumBytes;
                        // Capture before encoding: slow compression reduces FPS without speeding up the animation.
                        var next = finishing ? null : nextFrame();
                        var ticks = (long)Math.Round(endedAt.TotalMilliseconds / 10);
                        writer.Append(pending, (int)Math.Max(1, ticks - previousTicks));
                        previousTicks = ticks;
                        progress?.Report(new GifRecordingProgress(endedAt, writer.FrameCount, output.Position));
                        if (finishing)
                        {
                            reason = endedAt >= maximumDuration ? "已达到 60 秒上限" : output.Position >= maximumBytes ? "已达到约 50 MB 上限" : "";
                            break;
                        }
                        pending = next!; capturedAt = endedAt;
                    }
                    result = new GifRecordingResult(path, endedAt, writer.FrameCount, output.Position + 1, reason);
                }
                output.Flush(true);
            }
            File.Move(partial, path);
            return result;
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
    }
}
