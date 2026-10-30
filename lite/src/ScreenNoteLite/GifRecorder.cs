using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
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

    private sealed class RegionCapture : IDisposable
    {
        private readonly Drawing.Rectangle _region;
        private readonly Drawing.Bitmap _desktop, _scaled;
        private readonly Drawing.Graphics _graphics, _resize;
        internal Drawing.Size Size { get; }
        internal RegionCapture(Drawing.Rectangle region)
        {
            Size = OutputSize(region.Size); _region = region;
            _desktop = new Drawing.Bitmap(region.Width, region.Height, Drawing.Imaging.PixelFormat.Format32bppRgb);
            _scaled = new Drawing.Bitmap(Size.Width, Size.Height, Drawing.Imaging.PixelFormat.Format32bppRgb);
            _graphics = Drawing.Graphics.FromImage(_desktop); _resize = Drawing.Graphics.FromImage(_scaled);
            _resize.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        }
        internal BitmapSource NextFrame()
        {
            _graphics.CopyFromScreen(_region.Location, Drawing.Point.Empty, _region.Size, Drawing.CopyPixelOperation.SourceCopy);
            var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            if (GetCursorInfo(ref cursor) && (cursor.Flags & 1) != 0 && GetIconInfo(cursor.Cursor, out var icon))
            {
                try
                {
                    var hdc = _graphics.GetHdc();
                    try { DrawIconEx(hdc, cursor.Position.X - _region.X - (int)icon.HotspotX, cursor.Position.Y - _region.Y - (int)icon.HotspotY, cursor.Cursor, 0, 0, 0, 0, 3); }
                    finally { _graphics.ReleaseHdc(hdc); }
                }
                finally { if (icon.Mask != 0) DeleteObject(icon.Mask); if (icon.Color != 0) DeleteObject(icon.Color); }
            }
            _resize.DrawImage(_desktop, new Drawing.Rectangle(Drawing.Point.Empty, Size), 0, 0, _region.Width, _region.Height, Drawing.GraphicsUnit.Pixel);
            var data = _scaled.LockBits(new Drawing.Rectangle(Drawing.Point.Empty, Size), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                var image = BitmapSource.Create(Size.Width, Size.Height, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * Size.Height, data.Stride);
                image.Freeze(); return image;
            }
            finally { _scaled.UnlockBits(data); }
        }
        public void Dispose() { _resize.Dispose(); _graphics.Dispose(); _scaled.Dispose(); _desktop.Dispose(); }
        [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public nint Cursor; public Drawing.Point Position; }
        [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint HotspotX, HotspotY; public nint Mask, Color; }
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out IconInfo info);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    }
}
