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
    private readonly int _width, _height, _stride, _nativeWidth, _nativeHeight;
    private byte[]? _pixels;
    private bool _disposed;

    private Mp4FrameReader(Mf.IMFSourceReader reader, int width, int height)
    {
        _reader = reader;
        _width = width; _height = height; _stride = width * 4;
        _nativeWidth = width; _nativeHeight = height;
    }

    internal int Width => _width;
    internal int Height => _height;

    internal static Mp4FrameReader Open(string path)
    {
        Mf.Check(Mf.MFStartup(Mf.Version, 0), "MFStartup");
        Mf.IMFAttributes? attributes = null;
        Mf.IMFSourceReader? reader = null;
        Mf.IMFMediaType? native = null, output = null;
        try
        {
            Mf.Check(Mf.MFCreateAttributes(out attributes, 2), "MFCreateAttributes");
            Mf.Set(attributes, Mf.Keys.EnableVideoProcessing, 1);
            Mf.Check(Mf.MFCreateSourceReaderFromURL(path, attributes, out reader), "MFCreateSourceReaderFromURL");
            Mf.ReleaseCom(attributes); attributes = null;

            Mf.Check(reader.SetStreamSelection(Mf.Reader.FirstVideoStream, 1), "SetStreamSelection");
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
            Mf.Set64(output, Mf.Keys.FrameSize, Mf.Pack((uint)width, (uint)height));
            Mf.Set(output, Mf.Keys.DefaultStride, (uint)(width * 4));
            Mf.Set(output, Mf.Keys.InterlaceMode, 2);
            Mf.Check(reader.SetCurrentMediaType(Mf.Reader.FirstVideoStream, 0, output), "SetCurrentMediaType");
            Mf.Check(reader.SetStreamSelection(Mf.Reader.FirstVideoStream, 1), "SetStreamSelection");

            var result = new Mp4FrameReader(reader, width, height);
            reader = null;
            return result;
        }
        finally
        {
            Mf.ReleaseCom(native); Mf.ReleaseCom(output); Mf.ReleaseCom(attributes);
            if (reader != null) { Mf.ReleaseCom(reader); Mf.MFShutdown(); }
        }
    }

    /// <summary>Walks the frames in order, invoking <paramref name="onFrame"/> for each one inside the range.
    /// Returning false stops the walk early.</summary>
    internal void ReadFrames(TimeSpan from, TimeSpan to, Func<BitmapSource, TimeSpan, bool> onFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fromTicks = from.Ticks;
        var toTicks = to.Ticks;
        while (true)
        {
            var hr = _reader.ReadSample(Mf.Reader.FirstVideoStream, 0, out _, out var flags, out var timestamp, out var sample);
            if (sample == null)
            {
                if (hr < 0) Mf.Check(hr, "ReadSample");
                if ((flags & Mf.Reader.EndOfStream) != 0) return;
                continue;
            }
            try
            {
                var time = TimeSpan.FromTicks(timestamp);
                if (time > to) return;
                if (time >= from && !onFrame(FrameFrom(sample), time)) return;
            }
            finally { Mf.ReleaseCom(sample); }
        }
    }

    private BitmapSource FrameFrom(Mf.IMFSample sample)
    {
        _pixels ??= new byte[_stride * _height];
        Mf.IMFMediaBuffer? buffer = null;
        try
        {
            Mf.Check(sample.ConvertToContiguousBuffer(out buffer), "ConvertToContiguousBuffer");
            Mf.Check(buffer.Lock(out var source, out _, out var length), "Lock");
            try
            {
                // Media Foundation RGB32 is a bottom-up DIB; WPF wants top-down rows and an opaque alpha.
                for (var y = 0; y < _height; y++)
                    Marshal.Copy(source + (_height - 1 - y) * _stride, _pixels, y * _stride, _stride);
            }
            finally { buffer.Unlock(); }
        }
        finally { Mf.ReleaseCom(buffer); }
        for (var i = 3; i < _pixels.Length; i += 4) _pixels[i] = 255;
        var image = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, _pixels, _stride);
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Mf.ReleaseCom(_reader);
        Mf.MFShutdown();
    }
}