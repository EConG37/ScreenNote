using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace ScreenNote;

/// <summary>
/// Reads a finished recording (GIF or MP4) frame by frame without holding the whole
/// clip in memory, so a long screen recording can still be trimmed and exported.
/// </summary>
internal sealed class RecordSource : IDisposable
{
    private const int ThumbnailCount = 12;

    private GifBitmapDecoder? _gif;
    private Mp4FrameReader? _mp4;
    private readonly List<TimeSpan> _gifTimes = new();
    private readonly List<BitmapSource> _thumbnails = new();

    private RecordSource(string path, RecordMode mode) { Path = path; Mode = mode; }

    internal string Path { get; }
    internal RecordMode Mode { get; }
    internal Drawing.Size Size { get; private set; }
    internal TimeSpan Duration { get; private set; }
    internal BitmapSource? Poster { get; private set; }
    internal IReadOnlyList<BitmapSource> Thumbnails => _thumbnails;

    internal static RecordSource Open(string path)
    {
        var mode = string.Equals(System.IO.Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)
            ? RecordMode.Video
            : RecordMode.Gif;
        var source = new RecordSource(path, mode);
        if (mode == RecordMode.Video) source.LoadVideo(); else source.LoadGif();
        return source;
    }

    private void LoadGif()
    {
        _gif = new GifBitmapDecoder(new Uri(Path), BitmapCreateOptions.None, BitmapCacheOption.None);
        if (_gif!.Frames.Count == 0) throw new InvalidOperationException("这个 GIF 里没有可用的画面。");
        Size = new Drawing.Size(_gif.Frames[0].PixelWidth, _gif.Frames[0].PixelHeight);
        var total = TimeSpan.Zero;
        foreach (var item in _gif.Frames)
        {
            _gifTimes.Add(total);
            total += Delay(item);
        }
        Duration = total > TimeSpan.Zero ? total : TimeSpan.FromSeconds(_gif.Frames.Count / 10.0);
        Poster = _gif.Frames[0];
        // One ascending pass: grab the frame covering each thumbnail slot.
        var slot = 0;
        for (var index = 0; index < _gif.Frames.Count && slot < ThumbnailCount; index++)
        {
            if (_gifTimes[index] < TimeSpan.FromTicks(Duration.Ticks * slot / ThumbnailCount)) continue;
            _thumbnails.Add(Scale(_gif.Frames[index], 132));
            slot++;
        }
    }

    private void LoadVideo()
    {
        _mp4 = Mp4FrameReader.Open(Path);
        Size = new Drawing.Size(_mp4!.Width, _mp4.Height);
        Duration = ReadMp4Duration(Path);
        if (Duration <= TimeSpan.Zero) Duration = TimeSpan.FromSeconds(1);
        var first = _mp4.ReadAt(TimeSpan.Zero);
        Poster = first == null ? null : Scale(first, 1280);
    }

    /// <summary>Loads the filmstrip frames. GIF strips are already in memory; video frames are
    /// sampled on a background thread through a dedicated reader (COM wrappers are thread-bound).</summary>
    internal Task<IReadOnlyList<BitmapSource>> LoadThumbnailsAsync()
    {
        if (Mode == RecordMode.Gif) return Task.FromResult<IReadOnlyList<BitmapSource>>(_thumbnails);
        var path = Path;
        var duration = Duration;
        return Task.Run<IReadOnlyList<BitmapSource>>(() =>
        {
            var frames = new List<BitmapSource>();
            using var reader = Mp4FrameReader.Open(path);
            for (var slot = 0; slot < ThumbnailCount; slot++)
            {
                var target = TimeSpan.FromTicks(duration.Ticks * slot / ThumbnailCount);
                var frame = reader.ReadAt(target);
                if (frame == null) break;
                frames.Add(Scale(frame, 132));
            }
            return frames;
        });
    }

    private static TimeSpan ReadMp4Duration(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ReadBoxDuration(stream, stream.Length);
        }
        catch { }
        return TimeSpan.Zero;
    }

    // mvhd lives inside the moov container; walking only the top level never finds it.
    private static TimeSpan ReadBoxDuration(Stream stream, long end)
    {
        var head = new byte[16];
        while (stream.Position + 8 <= end)
        {
            var boxStart = stream.Position;
            if (stream.Read(head, 0, 8) != 8) break;
            var size = (long)((uint)head[0] << 24 | (uint)head[1] << 16 | (uint)head[2] << 8 | head[3]);
            var headerBytes = 8;
            if (size == 1)
            {
                if (stream.Read(head, 8, 8) != 8) break;
                size = (long)(((ulong)head[8] << 56) | ((ulong)head[9] << 48) | ((ulong)head[10] << 40) | ((ulong)head[11] << 32)
                    | ((ulong)head[12] << 24) | ((ulong)head[13] << 16) | ((ulong)head[14] << 8) | head[15]);
                headerBytes = 16;
            }
            else if (size == 0) size = end - boxStart;
            if (size < headerBytes) break;
            var isMvhd = head[4] == 'm' && head[5] == 'v' && head[6] == 'h' && head[7] == 'd';
            if (isMvhd)
            {
                var body = new byte[32];
                var read = stream.Read(body, 0, body.Length);
                if (read >= 20)
                {
                    if (body[0] == 1 && read >= 32)
                    {
                        var scale = Bytes(body, 20, 4);
                        var duration = ((ulong)Bytes(body, 24, 4) << 32) | Bytes(body, 28, 4);
                        return scale > 0 ? TimeSpan.FromSeconds((double)duration / scale) : TimeSpan.Zero;
                    }
                    var scale0 = Bytes(body, 12, 4);
                    var duration0 = Bytes(body, 16, 4);
                    return scale0 > 0 ? TimeSpan.FromSeconds((double)duration0 / scale0) : TimeSpan.Zero;
                }
                return TimeSpan.Zero;
            }
            if (head[4] == 'm' && head[5] == 'o' && head[6] == 'o' && head[7] == 'v')
            {
                var found = ReadBoxDuration(stream, boxStart + size);
                if (found > TimeSpan.Zero) return found;
            }
            stream.Position = boxStart + size;
        }
        return TimeSpan.Zero;
    }

    private static uint Bytes(byte[] buffer, int offset, int count)
    {
        uint value = 0;
        for (var index = 0; index < count; index++) value = (value << 8) | buffer[offset + index];
        return value;
    }

    private static TimeSpan Delay(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                var delay = metadata.GetQuery("/grctlext/Delay");
                if (delay is ushort centiseconds && centiseconds > 0) return TimeSpan.FromMilliseconds(centiseconds * 10.0);
            }
        }
        catch { }
        return TimeSpan.FromMilliseconds(100);
    }

    internal BitmapSource? PreviewGifFrame(TimeSpan time)
    {
        if (_gif == null) return null;
        var index = _gifTimes.BinarySearch(Clamp(time));
        if (index < 0) index = ~index - 1;
        return _gif.Frames[Math.Clamp(index, 0, _gif.Frames.Count - 1)];
    }

    /// <summary>Streams frames inside the range to the callback; return false to stop early.</summary>
    internal void Read(TimeSpan from, TimeSpan to, Func<BitmapSource, TimeSpan, bool> onFrame)
    {
        from = Clamp(from);
        to = Clamp(to);
        if (to <= from) return;
        if (_gif != null)
        {
            for (var index = 0; index < _gif.Frames.Count; index++)
            {
                var time = _gifTimes[index];
                var next = index + 1 < _gifTimes.Count ? _gifTimes[index + 1] : Duration;
                if (next <= from) continue;
                if (time >= to) return;
                if (!onFrame(_gif.Frames[index], (time < from ? from : time) - from)) return;
            }
            return;
        }
        _mp4?.ReadFrames(from, to, (image, time) => onFrame(image, time - from));
    }

    internal TimeSpan Clamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        return value > Duration ? Duration : value;
    }

    internal static BitmapSource Scale(BitmapSource image, int maximumEdge)
    {
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var scale = Math.Min(1.0, (double)maximumEdge / Math.Max(width, height));
        if (scale >= 1) return image;
        // A TransformedBitmap retains its full-resolution source. Render a detached
        // thumbnail so twelve filmstrip frames do not keep twelve screen-sized buffers.
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawImage(image, new Rect(0, 0, targetWidth, targetHeight));
        var scaled = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
        scaled.Render(visual);
        scaled.Freeze();
        return scaled;
    }

    public void Dispose()
    {
        _mp4?.Dispose();
        _thumbnails.Clear();
        Poster = null;
    }
}
