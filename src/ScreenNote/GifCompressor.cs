using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace ScreenNote;

internal sealed record GifCompressionResult(string Path, long OriginalBytes, long Bytes, int Frames, TimeSpan Duration, bool MetTarget);

internal static class GifCompressor
{
    internal const long WeChatTargetBytes = 950_000;
    internal static Task<GifCompressionResult> CompressAsync(string sourcePath, string outputDirectory,
        CancellationToken cancellation, IProgress<string>? progress = null, long targetBytes = WeChatTargetBytes) =>
        Task.Run(() => Compress(sourcePath, outputDirectory, cancellation, progress, targetBytes));

    internal static GifCompressionResult Compress(string sourcePath, string outputDirectory, CancellationToken cancellation,
        IProgress<string>? progress = null, long targetBytes = WeChatTargetBytes)
    {
        cancellation.ThrowIfCancellationRequested();
        var originalBytes = new FileInfo(sourcePath).Length;
        if (originalBytes > 100_000_000) throw new IOException("请先选择小于 100 MB 的 GIF。");
        using var source = Drawing.Image.FromFile(sourcePath);
        if (source.RawFormat.Guid != Drawing.Imaging.ImageFormat.Gif.Guid) throw new FormatException("请选择 GIF 动图文件。");
        var count = source.GetFrameCount(Drawing.Imaging.FrameDimension.Time);
        if (count > 1800 || (long)source.Width * source.Height > 24_000_000) throw new IOException("GIF 帧数或尺寸过大，请先截短录制。");
        var delays = new int[count];
        byte[] timing;
        try { timing = source.GetPropertyItem(0x5100)?.Value ?? []; } catch (ArgumentException) { timing = []; }
        var durationTicks = 0;
        for (var i = 0; i < count; i++)
        { delays[i] = timing.Length >= (i + 1) * 4 ? Math.Clamp(BitConverter.ToInt32(timing, i * 4), 1, 65535) : 10; durationTicks = checked(durationTicks + delays[i]); }
        var duration = TimeSpan.FromMilliseconds(durationTicks * 10d);
        if (originalBytes <= targetBytes) return new(sourcePath, originalBytes, originalBytes, count, duration, true);
        Directory.CreateDirectory(outputDirectory);
        var destination = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(sourcePath) + "-小图-" + Guid.NewGuid().ToString("N")[..8] + ".gif");
        var temporary = destination + ".partial"; var best = destination + ".best";
        var bestBytes = originalBytes; var bestFrames = count;
        try
        {
            foreach (var (edge, fps, colors) in new[] { (720, 10, 128), (640, 8, 96), (512, 8, 96), (400, 6, 64), (320, 5, 64), (240, 4, 48), (192, 4, 32), (128, 3, 16) })
            {
                cancellation.ThrowIfCancellationRequested();
                var scale = Math.Min(1d, edge / (double)Math.Max(source.Width, source.Height));
                var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                var height = Math.Max(1, (int)Math.Round(source.Height * scale));
                progress?.Report($"正在压缩：{width} × {height} · 最多 {fps} 帧/秒 · {colors} 色\n目标小于 1 MB，完整原文件会保留。" );
                using var bitmap = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppRgb);
                using var graphics = Drawing.Graphics.FromImage(bitmap);
                graphics.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                var groups = Sample(delays, fps);
                var written = 0;
                using (var output = File.Create(temporary))
                using (var writer = new AnimatedGifWriter(output, width, height))
                {
                    byte[]? pendingPixels = null;
                    BitmapSource? pending = null;
                    var pendingDelay = 0; var patch = new Int32Rect(0, 0, width, height);
                    foreach (var (index, delay) in groups)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        source.SelectActiveFrame(Drawing.Imaging.FrameDimension.Time, index);
                        graphics.Clear(Drawing.Color.White);
                        graphics.DrawImage(source, new Drawing.Rectangle(0, 0, width, height));
                        var pixels = new byte[width * height * 4];
                        var locked = bitmap.LockBits(new Drawing.Rectangle(0, 0, width, height), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppRgb);
                        try { Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length); } finally { bitmap.UnlockBits(locked); }
                        var difference = pendingPixels == null ? patch : Difference(pendingPixels, pixels, width, height);
                        if (pending != null && difference.IsEmpty && pendingDelay + delay <= 65535) { pendingDelay += delay; continue; }
                        if (pending != null) writer.Append(new CroppedBitmap(pending, patch), pendingDelay, colors, patch.X, patch.Y);
                        pending = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
                        pending.Freeze(); pendingPixels = pixels; pendingDelay = delay;
                        patch = difference.IsEmpty ? new Int32Rect(0, 0, 1, 1) : difference;
                    }
                    if (pending != null) writer.Append(new CroppedBitmap(pending, patch), pendingDelay, colors, patch.X, patch.Y);
                    written = writer.FrameCount;
                }
                cancellation.ThrowIfCancellationRequested();
                var length = new FileInfo(temporary).Length;
                if (length < bestBytes)
                {
                    File.Move(temporary, best, overwrite: true); bestBytes = length; bestFrames = written;
                }
                else File.Delete(temporary);
                if (bestBytes <= targetBytes) break;
            }
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(best)) { File.Move(best, destination); return new(destination, originalBytes, bestBytes, bestFrames, duration, bestBytes <= targetBytes); }
            return new(sourcePath, originalBytes, originalBytes, count, duration, false);
        }
        finally { try { File.Delete(temporary); File.Delete(best); } catch (IOException) { } }
    }

    private static List<(int Index, int Delay)> Sample(int[] delays, int fps)
    {
        var groups = new List<(int, int)>();
        var elapsed = 0; var selectedAt = 0; var selected = 0; var nextAt = 100d / fps;
        for (var i = 0; i < delays.Length; i++)
        {
            if (i > 0 && (elapsed >= nextAt || i == delays.Length - 1 || elapsed - selectedAt + delays[i] > 65535))
            { groups.Add((selected, elapsed - selectedAt)); selected = i; selectedAt = elapsed; nextAt = elapsed + 100d / fps; }
            elapsed += delays[i];
        }
        groups.Add((selected, elapsed - selectedAt)); return groups;
    }

    private static Int32Rect Difference(byte[] previous, byte[] current, int width, int height)
    {
        var left = width; var right = -1; var top = height; var bottom = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            if (previous[index] == current[index] && previous[index + 1] == current[index + 1] && previous[index + 2] == current[index + 2]) continue;
            left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = y;
        }
        return right < 0 ? Int32Rect.Empty : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }
}
