using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenNote;

internal enum StitchResult { Added, Unchanged, NoOverlap, LimitReached }

/// <summary>Matches a downward translation and appends only newly revealed rows.</summary>
internal sealed class ScrollStitcher
{
    private readonly List<(byte[] Pixels, int Height)> _pieces = [];
    private byte[] _previous;
    internal int Width { get; }
    internal int FrameHeight { get; }
    internal int Height { get; private set; }
    internal int Captures => _pieces.Count;
    internal const long MaximumPixels = 24_000_000;
    internal const int MaximumHeight = 30_000;

    internal ScrollStitcher(BitmapSource first)
    {
        Width = first.PixelWidth; FrameHeight = Height = first.PixelHeight;
        if (Width < 32 || Height < 64 || (long)Width * Height > MaximumPixels)
            throw new ArgumentException("请框选至少 32 × 64 像素的页面内容区域，避免过大的整屏选区。");
        _previous = Pixels(first); _pieces.Add((_previous, Height));
    }

    internal StitchResult Append(BitmapSource frame)
    {
        if (frame.PixelWidth != Width || frame.PixelHeight != FrameHeight) throw new ArgumentException("滚动过程中选区尺寸发生变化。");
        var current = Pixels(frame);
        if (Score(current, 0, out _) < 0.8) return StitchResult.Unchanged;
        var best = double.MaxValue; var second = double.MaxValue; var shift = 0; var bestTexture = 0;
        var minimumOverlap = Math.Max(40, FrameHeight / 5);
        for (var candidate = 4; candidate <= FrameHeight - minimumOverlap; candidate++)
        {
            var score = Score(current, candidate, out var texture);
            if (texture < 12) continue;
            if (score < best)
            { if (candidate - shift > 3) second = best; best = score; shift = candidate; bestTexture = texture; }
            else if (Math.Abs(candidate - shift) > 3) second = Math.Min(second, score);
        }
        if (shift == 0 || best > 8 || bestTexture < 12 || second <= Math.Max(0.005, best * 1.03)) return StitchResult.NoOverlap;
        if (Height + shift > MaximumHeight || (long)Width * (Height + shift) > MaximumPixels) return StitchResult.LimitReached;
        var strip = new byte[checked(Width * shift * 4)];
        Buffer.BlockCopy(current, (FrameHeight - shift) * Width * 4, strip, 0, strip.Length);
        _pieces.Add((strip, shift)); _previous = current; Height += shift;
        return StitchResult.Added;
    }

    private double Score(byte[] current, int shift, out int texture)
    {
        var overlap = FrameHeight - shift;
        var start = Math.Min(FrameHeight / 6, overlap / 3);
        var end = overlap - Math.Min(FrameHeight / 12, overlap / 4);
        double error = 0; var count = 0; texture = 0;
        // Ignore narrow screen edges and the top/bottom chrome when measuring overlap.
        for (var y = start; y < end; y += Math.Max(1, overlap / 100))
        for (var sample = 1; sample <= 24; sample++)
        {
            var x = Width * (sample + 1) / 28;
            var a = ((y + shift) * Width + x) * 4; var b = (y * Width + x) * 4;
            for (var channel = 0; channel < 3; channel++) error += Math.Abs(_previous[a + channel] - current[b + channel]);
            count += 3;
            var neighbor = Math.Min(_previous.Length - 4, a + Width * 2 * 4);
            if (Math.Abs(_previous[a] - _previous[neighbor]) + Math.Abs(_previous[a + 1] - _previous[neighbor + 1]) + Math.Abs(_previous[a + 2] - _previous[neighbor + 2]) > 24) texture++;
        }
        return error / Math.Max(1, count);
    }

    internal BitmapSource Export()
    {
        var pixels = new byte[checked(Width * Height * 4)]; var offset = 0;
        foreach (var piece in _pieces) { Buffer.BlockCopy(piece.Pixels, 0, pixels, offset, piece.Pixels.Length); offset += piece.Pixels.Length; }
        var image = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        image.Freeze(); return image;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(source.PixelWidth * source.PixelHeight * 4)]; converted.CopyPixels(pixels, source.PixelWidth * 4, 0); return pixels;
    }
}
