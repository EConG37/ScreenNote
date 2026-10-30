using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenNote;

internal enum CollageLayout { Subtitles, Vertical, Horizontal, Grid }
internal readonly record struct SubtitleCrop(double Top, double Bottom);

internal static class ImageCollage
{
    internal static BitmapSource Compose(IReadOnlyList<BitmapSource> images, CollageLayout layout,
        int outputWidth = 960, double subtitleFraction = 0.2, int stagger = 16, int gap = 3, IReadOnlyList<SubtitleCrop?>? crops = null)
    {
        if (images.Count < 1 || images.Count > 20) throw new ArgumentException("每次拼图支持 1–20 张图片。");
        outputWidth = Math.Clamp(outputWidth, 160, 2000); gap = Math.Clamp(gap, 0, 32);
        stagger = layout == CollageLayout.Subtitles ? Math.Clamp(stagger, 0, 100) : 0;
        subtitleFraction = Math.Clamp(subtitleFraction, 0.05, 0.6);
        var placements = new List<(BitmapSource Source, Rect Destination)>();
        double width = outputWidth + stagger, height = 0;
        if (layout is CollageLayout.Vertical or CollageLayout.Subtitles)
        {
            for (var i = 0; i < images.Count; i++)
            {
                var source = images[i];
                if (i > 0 && layout == CollageLayout.Subtitles)
                {
                    var crop = crops != null && i < crops.Count ? crops[i] : null;
                    var top = Math.Clamp(crop?.Top ?? 1 - subtitleFraction, 0, 0.99);
                    var bottom = Math.Clamp(crop?.Bottom ?? 1, top + 0.01, 1);
                    var start = Math.Clamp((int)Math.Round(source.PixelHeight * top), 0, source.PixelHeight - 1);
                    var end = Math.Clamp((int)Math.Round(source.PixelHeight * bottom), start + 1, source.PixelHeight);
                    source = new CroppedBitmap(source, new Int32Rect(0, start, source.PixelWidth, end - start));
                }
                var drawnHeight = source.PixelHeight * (double)outputWidth / source.PixelWidth;
                placements.Add((source, new Rect(i % 2 * stagger, height, outputWidth, drawnHeight)));
                height += drawnHeight + (i == images.Count - 1 ? 0 : gap);
            }
        }
        else
        {
            var columns = layout == CollageLayout.Grid ? Math.Min(2, images.Count) : images.Count;
            var cellWidth = (outputWidth - gap * (columns - 1d)) / columns;
            for (var row = 0; row * columns < images.Count; row++)
            {
                var rowHeight = 0d;
                for (var column = 0; column < columns && row * columns + column < images.Count; column++)
                {
                    var source = images[row * columns + column];
                    var drawnHeight = source.PixelHeight * cellWidth / source.PixelWidth;
                    placements.Add((source, new Rect(column * (cellWidth + gap), height, cellWidth, drawnHeight)));
                    rowHeight = Math.Max(rowHeight, drawnHeight);
                }
                height += rowHeight + (row * columns + columns >= images.Count ? 0 : gap);
            }
        }
        var pixelWidth = (int)Math.Ceiling(width); var pixelHeight = (int)Math.Ceiling(height);
        if (pixelHeight < 1 || pixelHeight > 30000 || (long)pixelWidth * pixelHeight > 24_000_000)
            throw new ArgumentException("拼图过长，请减少图片、降低输出宽度或缩小台词高度。");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(layout == CollageLayout.Subtitles ? Brushes.Black : Brushes.White, null, new Rect(0, 0, width, height));
            foreach (var placement in placements) drawing.DrawImage(placement.Source, placement.Destination);
        }
        var image = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }
}
