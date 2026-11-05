using System;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace ScreenNote;

/// <summary>Writes H.264/AAC MP4 through the Media Foundation sink writer that ships with Windows.
/// Video input is top-down BGRA (WPF frames); audio input is interleaved stereo PCM16.</summary>
internal sealed class Mp4Writer : IDisposable
{
    private readonly object _sync = new();
    private readonly int _width, _height, _stride;
    private Mf.IMFSinkWriter? _writer;
    private uint _videoStream;
    private uint _audioStream;
    private bool _hasAudio, _finalized, _disposed;
    private byte[]? _framePixels;

    internal Mp4Writer(string path, int width, int height, int frameRate, int audioSampleRate = 0, int audioBytesPerSecond = 24000)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "视频宽高需为不小于 2 的偶数。");
        _width = width; _height = height; _stride = width * 4;
        Mf.AcquireMf();
        Mf.IMFAttributes? attributes = null;
        Mf.IMFMediaType? videoOutput = null, videoInput = null, audioOutput = null, audioInput = null;
        try
        {
            Mf.Check(Mf.MFCreateAttributes(out attributes, 4), "MFCreateAttributes");
            Set(attributes, Mf.Keys.EnableHardwareTransforms, 1);
            Set(attributes, Mf.Keys.DisableThrottling, 1);
            SetFormat(attributes, Mf.Keys.TranscodeContainerType, Mf.Keys.ContainerTypeMpeg4);
            Mf.Check(Mf.MFCreateSinkWriterFromURL(path, 0, attributes, out _writer), "MFCreateSinkWriterFromURL");

            Mf.Check(Mf.MFCreateMediaType(out videoOutput), "MFCreateMediaType");
            SetFormat(videoOutput, Mf.Keys.MajorType, Mf.MediaTypes.Video);
            SetFormat(videoOutput, Mf.Keys.Subtype, Mf.MediaTypes.H264);
            // Screen content compresses well; scale bitrate with pixels, capped well under the 500 MB recording cap.
            Set(videoOutput, Mf.Keys.AverageBitrate, (uint)Math.Clamp((long)width * height * frameRate / 5, 2_000_000, 8_000_000));
            Set(videoOutput, Mf.Keys.InterlaceMode, 2);
            Set64(videoOutput, Mf.Keys.FrameSize, Mf.Pack((uint)width, (uint)height));
            Set64(videoOutput, Mf.Keys.FrameRate, Mf.Pack((uint)frameRate, 1));
            Set64(videoOutput, Mf.Keys.PixelAspectRatio, Mf.Pack(1, 1));
            Mf.Check(_writer.AddStream(videoOutput, out _videoStream), "AddStream(Video)");

            Mf.Check(Mf.MFCreateMediaType(out videoInput), "MFCreateMediaType");
            SetFormat(videoInput, Mf.Keys.MajorType, Mf.MediaTypes.Video);
            SetFormat(videoInput, Mf.Keys.Subtype, Mf.MediaTypes.Rgb32);
            Set(videoInput, Mf.Keys.InterlaceMode, 2);
            Set64(videoInput, Mf.Keys.FrameSize, Mf.Pack((uint)width, (uint)height));
            Set64(videoInput, Mf.Keys.FrameRate, Mf.Pack((uint)frameRate, 1));
            Set64(videoInput, Mf.Keys.PixelAspectRatio, Mf.Pack(1, 1));
            Mf.Check(_writer.SetInputMediaType(_videoStream, videoInput, null), "SetInputMediaType(Video)");

            if (audioSampleRate > 0)
            {
                Mf.Check(Mf.MFCreateMediaType(out audioOutput), "MFCreateMediaType");
                SetFormat(audioOutput, Mf.Keys.MajorType, Mf.MediaTypes.Audio);
                SetFormat(audioOutput, Mf.Keys.Subtype, Mf.MediaTypes.Aac);
                Set(audioOutput, Mf.Keys.AudioChannels, 2);
                Set(audioOutput, Mf.Keys.AudioSamplesPerSecond, (uint)audioSampleRate);
                Set(audioOutput, Mf.Keys.AudioBitsPerSample, 16);
                Set(audioOutput, Mf.Keys.AudioAverageBytesPerSecond, (uint)audioBytesPerSecond);
                Mf.Check(_writer.AddStream(audioOutput, out _audioStream), "AddStream(Audio)");

                Mf.Check(Mf.MFCreateMediaType(out audioInput), "MFCreateMediaType");
                SetFormat(audioInput, Mf.Keys.MajorType, Mf.MediaTypes.Audio);
                SetFormat(audioInput, Mf.Keys.Subtype, Mf.MediaTypes.Pcm);
                Set(audioInput, Mf.Keys.AudioChannels, 2);
                Set(audioInput, Mf.Keys.AudioSamplesPerSecond, (uint)audioSampleRate);
                Set(audioInput, Mf.Keys.AudioBitsPerSample, 16);
                Set(audioInput, Mf.Keys.AudioBlockAlignment, 4);
                Set(audioInput, Mf.Keys.AudioAverageBytesPerSecond, (uint)audioSampleRate * 4);
                Set(audioInput, Mf.Keys.AllSamplesIndependent, 1);
                Mf.Check(_writer.SetInputMediaType(_audioStream, audioInput, null), "SetInputMediaType(Audio)");
                _hasAudio = true;
            }
            Mf.Check(_writer.BeginWriting(), "BeginWriting");
            ReleaseCom(videoOutput); videoOutput = null;
            ReleaseCom(videoInput); videoInput = null;
            ReleaseCom(audioOutput); audioOutput = null;
            ReleaseCom(audioInput); audioInput = null;
            ReleaseCom(attributes); attributes = null;
        }
        catch
        {
            ReleaseCom(videoOutput); ReleaseCom(videoInput); ReleaseCom(audioOutput); ReleaseCom(audioInput); ReleaseCom(attributes);
            ReleaseCom(_writer); _writer = null;
            Mf.ReleaseMf();
            throw;
        }
    }

    internal void WriteVideoFrame(BitmapSource frame, long time, long duration)
    {
        _framePixels ??= new byte[_stride * _height];
        frame.CopyPixels(_framePixels, _stride, 0);
        WriteVideoFrame(_framePixels, time, duration);
    }

    internal void WriteVideoFrame(byte[] topDownBgra, long time, long duration)
    {
        lock (_sync) Write(topDownBgra, _videoStream, time, duration, topDown: true);
    }

    internal void WriteAudioPcm(byte[] pcm16Stereo, long time, long duration)
    {
        lock (_sync)
        {
            if (!_hasAudio || pcm16Stereo.Length == 0) return;
            Write(pcm16Stereo, _audioStream, time, duration, topDown: false);
        }
    }

    private void Write(byte[] payload, uint stream, long time, long duration, bool topDown)
    {
        if (_finalized || _writer == null) throw new InvalidOperationException("视频文件已经写入完毕。");
        Mf.IMFMediaBuffer? buffer = null;
        Mf.IMFSample? sample = null;
        try
        {
            Mf.Check(Mf.MFCreateMemoryBuffer((uint)payload.Length, out buffer), "MFCreateMemoryBuffer");
            Mf.Check(Mf.MFCreateSample(out sample), "MFCreateSample");
            Mf.Check(buffer.Lock(out var destination, out _, out _), "Lock");
            try
            {
                // Media Foundation RGB32 is a bottom-up DIB; flip the top-down WPF rows.
                if (!topDown) Marshal.Copy(payload, 0, destination, payload.Length);
                else for (var y = 0; y < _height; y++) Marshal.Copy(payload, (_height - 1 - y) * _stride, destination + y * _stride, _stride);
            }
            finally { buffer.Unlock(); }
            Mf.Check(buffer.SetCurrentLength((uint)payload.Length), "SetCurrentLength");
            Mf.Check(sample.AddBuffer(buffer), "AddBuffer");
            Mf.Check(sample.SetSampleTime(time), "SetSampleTime");
            Mf.Check(sample.SetSampleDuration(duration), "SetSampleDuration");
            Mf.Check(_writer.WriteSample(stream, sample), "WriteSample");
        }
        finally { ReleaseCom(buffer); ReleaseCom(sample); }
    }

    /// <summary>Writes the MP4 trailer. Called once by the owner after the last sample; failures leave the partial file for cleanup.</summary>
    internal void Complete()
    {
        lock (_sync)
        {
            if (_finalized || _writer == null) return;
            Mf.Check(_writer.Finalize(), "Finalize");
            _finalized = true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseCom(_writer); _writer = null;
            Mf.ReleaseMf();
        }
        GC.SuppressFinalize(this);
    }

    private static void Set(Mf.IMFAttributes attributes, Guid key, uint value) => Mf.Check(attributes.SetUINT32(ref key, value), "SetUINT32");
    private static void Set(Mf.IMFMediaType type, Guid key, uint value) => Mf.Check(type.SetUINT32(ref key, value), "SetUINT32");
    private static void Set64(Mf.IMFMediaType type, Guid key, ulong value) => Mf.Check(type.SetUINT64(ref key, value), "SetUINT64");
    private static void SetFormat(Mf.IMFAttributes attributes, Guid key, Guid value) => Mf.Check(attributes.SetGUID(ref key, ref value), "SetGUID");
    private static void SetFormat(Mf.IMFMediaType type, Guid key, Guid value) => Mf.Check(type.SetGUID(ref key, ref value), "SetGUID");
    private static void ReleaseCom(object? com)
    {
        Mf.ReleaseCom(com);
    }
}
