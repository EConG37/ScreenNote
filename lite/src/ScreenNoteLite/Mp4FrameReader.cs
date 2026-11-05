using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenNote;

/// <summary>Decodes an MP4 through the Media Foundation source reader so a recording can be
/// trimmed and cropped. Every call stays on the creating thread: the COM wrappers are apartment-bound.</summary>
internal sealed class Mp4FrameReader : IDisposable
{
    private readonly Mf.IMFSourceReader _reader;
    private readonly int _width, _height, _pitch;
    private readonly bool _bottomUp;
    private byte[]? _pixels;
    private bool _disposed;

    private Mp4FrameReader(Mf.IMFSourceReader reader, int width, int height, int pitch, bool bottomUp)
    {
        _reader = reader;
        _width = width; _height = height;
        _pitch = pitch; _bottomUp = bottomUp;
    }

    internal int Width => _width;
    internal int Height => _height;

    internal static Mp4FrameReader Open(string path)
    {
        Mf.AcquireMf();
        Mf.IMFAttributes? attributes = null;
        Mf.IMFSourceReader? reader = null;
        Mf.IMFMediaType? native = null, output = null, current = null;
        var transferred = false;
        try
        {
            Mf.Check(Mf.MFCreateAttributes(out attributes, 2), "MFCreateAttributes");
            Mf.Set(attributes, Mf.Keys.EnableVideoProcessing, 1);
            Mf.Check(Mf.MFCreateSourceReaderFromURL(path, attributes, out reader), "MFCreateSourceReaderFromURL");
            Mf.ReleaseCom(attributes); attributes = null;

            Mf.Check(reader.SetStreamSelection(Mf.Reader.AllStreams, 0), "SetStreamSelection(all)");
            Mf.Check(reader.SetStreamSelection(Mf.Reader.FirstVideoStream, 1), "SetStreamSelection(video)");
            Mf.Check(reader.GetNativeMediaType(Mf.Reader.FirstVideoStream, 0, out native), "GetNativeMediaType");
            var size = Mf.GetUInt64(native, Mf.Keys.FrameSize);
            var width = (int)(size >> 32);
            var height = (int)(size & 0xFFFFFFFF);
            if (width < 2 || height < 2) throw new InvalidOperationException("无法读取视频尺寸。");
            width &= ~1; height &= ~1;

            Mf.Check(Mf.MFCreateMediaType(out output), "MFCreateMediaType");
            Mf.SetFormat(output, Mf.Keys.MajorType, Mf.MediaTypes.Video);
            Mf.SetFormat(output, Mf.Keys.Subtype, Mf.MediaTypes.Rgb32);
            // Let the decoder negotiate its buffer layout. Forcing a positive RGB
            // stride can put the source reader's YUV converter on an invalid layout.
            Mf.Check(reader.SetCurrentMediaType(Mf.Reader.FirstVideoStream, 0, output), "SetCurrentMediaType");

            // MF may honor the requested top-down stride or fall back to the bottom-up
            // RGB32 default; the negotiated type decides whether rows need flipping.
            Mf.Check(reader.GetCurrentMediaType(Mf.Reader.FirstVideoStream, out current), "GetCurrentMediaType");
            var stride = Mf.TryGetInt32(current, Mf.Keys.DefaultStride);
            var bottomUp = stride is null or < 0;
            var pitch = stride is null ? width * 4 : Math.Abs(stride.Value);
            if (pitch < width * 4) pitch = width * 4;

            var result = new Mp4FrameReader(reader, width, height, pitch, bottomUp);
            transferred = true;
            reader = null;
            return result;
        }
        finally
        {
            Mf.ReleaseCom(native); Mf.ReleaseCom(output); Mf.ReleaseCom(current); Mf.ReleaseCom(attributes);
            Mf.ReleaseCom(reader);
            if (!transferred) Mf.ReleaseMf();
        }
    }

    /// <summary>Decodes the first frame at or after <paramref name="time"/>; null past the end of the clip.</summary>
    internal BitmapSource? ReadAt(TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SeekTo(time.Ticks);
        while (true)
        {
            var hr = _reader.ReadSample(Mf.Reader.FirstVideoStream, 0, out _, out var flags, out var timestamp, out var sample);
            if (hr < 0) { Mf.ReleaseCom(sample); Mf.Check(hr, "ReadSample"); }
            if (sample == null)
            {
                if ((flags & Mf.Reader.EndOfStream) != 0) return null;
                continue; // Stream ticks and media-type notices carry no frame.
            }
            try
            {
                if (timestamp >= time.Ticks) return FrameFrom(sample);
            }
            finally { Mf.ReleaseCom(sample); }
        }
    }

    // A PROPVARIANT holding a 64-bit seek position in 100 ns units (VT_I8).
    private void SeekTo(long ticks)
    {
        var variant = Mf.PositionVariant.At(ticks);
        var empty = Guid.Empty;
        Mf.Check(_reader.SetCurrentPosition(ref empty, in variant), "SetCurrentPosition");
    }

    /// <summary>Walks the frames in order, invoking <paramref name="onFrame"/> for each one inside the range.
    /// Returning false stops the walk early.</summary>
    internal void ReadFrames(TimeSpan from, TimeSpan to, Func<BitmapSource, TimeSpan, bool> onFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SeekTo(from.Ticks);
        while (true)
        {
            var hr = _reader.ReadSample(Mf.Reader.FirstVideoStream, 0, out _, out var flags, out var timestamp, out var sample);
            if (hr < 0) { Mf.ReleaseCom(sample); Mf.Check(hr, "ReadSample"); }
            if (sample == null)
            {
                if ((flags & Mf.Reader.EndOfStream) != 0) return;
                continue;
            }
            try
            {
                var time = TimeSpan.FromTicks(timestamp);
                if (time >= to) return;
                if (time >= from && !onFrame(FrameFrom(sample), time)) return;
            }
            finally { Mf.ReleaseCom(sample); }
        }
    }

    private BitmapSource FrameFrom(Mf.IMFSample sample)
    {
        _pixels ??= new byte[_width * 4 * _height];
        Mf.IMFMediaBuffer? buffer = null;
        try
        {
            Mf.Check(sample.ConvertToContiguousBuffer(out buffer), "ConvertToContiguousBuffer");
            Mf.Check(buffer.Lock(out var source, out _, out var length), "Lock");
            try
            {
                var rowBytes = _width * 4;
                if ((long)(_height - 1) * _pitch + rowBytes > length)
                    throw new InvalidOperationException("解码画面的长度与视频尺寸不一致。");
                // Bottom-up DIBs store the last row first; top-down rows copy straight across.
                for (var y = 0; y < _height; y++)
                {
                    var offset = (_bottomUp ? _height - 1 - y : y) * _pitch;
                    Marshal.Copy(source + offset, _pixels, y * rowBytes, rowBytes);
                }
            }
            finally { buffer.Unlock(); }
        }
        finally { Mf.ReleaseCom(buffer); }
        for (var i = 3; i < _pixels.Length; i += 4) _pixels[i] = 255;
        var image = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, _pixels, _width * 4);
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Mf.ReleaseCom(_reader);
        Mf.ReleaseMf();
    }
}
