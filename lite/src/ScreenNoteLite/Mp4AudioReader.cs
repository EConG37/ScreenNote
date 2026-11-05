using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ScreenNote;

internal sealed record AudioChunk(byte[] Pcm, long Time, long Duration);

/// <summary>Streams stereo PCM for the selected interval; decoded audio is never held for the whole clip.</summary>
internal sealed class Mp4AudioReader : IDisposable
{
    private readonly Mf.IMFSourceReader _reader;
    private bool _disposed;
    internal int SampleRate { get; }

    private Mp4AudioReader(Mf.IMFSourceReader reader, int sampleRate)
    {
        _reader = reader;
        SampleRate = sampleRate;
    }

    internal static Mp4AudioReader? OpenOptional(string path)
    {
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return null;
        Mf.AcquireMf();
        Mf.IMFSourceReader? reader = null;
        Mf.IMFMediaType? native = null, output = null;
        var transferred = false;
        try
        {
            Mf.Check(Mf.MFCreateSourceReaderFromURL(path, null, out reader), "OpenAudio");
            var hr = reader.GetNativeMediaType(Mf.Reader.FirstAudioStream, 0, out native);
            // A silent recording legitimately has no first audio stream.
            if (hr == unchecked((int)0xC00D36B3)) return null;
            Mf.Check(hr, "GetAudioType");
            var rate = Mf.TryGetInt32(native, Mf.Keys.AudioSamplesPerSecond) ?? 48000;
            rate = rate is 44100 or 48000 ? rate : 48000;
            Mf.Check(reader.SetStreamSelection(Mf.Reader.AllStreams, 0), "SelectAudio");
            Mf.Check(reader.SetStreamSelection(Mf.Reader.FirstAudioStream, 1), "SelectAudio");
            Mf.Check(Mf.MFCreateMediaType(out output), "CreateAudioType");
            Mf.SetFormat(output, Mf.Keys.MajorType, Mf.MediaTypes.Audio);
            Mf.SetFormat(output, Mf.Keys.Subtype, Mf.MediaTypes.Pcm);
            Mf.Set(output, Mf.Keys.AudioChannels, 2);
            Mf.Set(output, Mf.Keys.AudioSamplesPerSecond, (uint)rate);
            Mf.Set(output, Mf.Keys.AudioBitsPerSample, 16);
            Mf.Set(output, Mf.Keys.AudioBlockAlignment, 4);
            Mf.Set(output, Mf.Keys.AudioAverageBytesPerSecond, (uint)rate * 4);
            Mf.Check(reader.SetCurrentMediaType(Mf.Reader.FirstAudioStream, 0, output), "DecodeAudio");
            var result = new Mp4AudioReader(reader, rate);
            reader = null;
            transferred = true;
            return result;
        }
        finally
        {
            Mf.ReleaseCom(native); Mf.ReleaseCom(output); Mf.ReleaseCom(reader);
            if (!transferred) Mf.ReleaseMf();
        }
    }

    internal IEnumerable<AudioChunk> Read(TimeSpan start, TimeSpan end)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var format = Guid.Empty;
        var position = Mf.PositionVariant.At(start.Ticks);
        Mf.Check(_reader.SetCurrentPosition(ref format, in position), "SeekAudio");
        while (true)
        {
            var hr = _reader.ReadSample(Mf.Reader.FirstAudioStream, 0, out _, out var flags, out var time, out var sample);
            if (hr < 0) { Mf.ReleaseCom(sample); Mf.Check(hr, "ReadAudio"); }
            if (sample == null)
            {
                if ((flags & Mf.Reader.EndOfStream) != 0) yield break;
                continue;
            }
            AudioChunk? chunk = null;
            try
            {
                if (time >= end.Ticks) yield break;
                Mf.IMFMediaBuffer? buffer = null;
                try
                {
                    Mf.Check(sample.ConvertToContiguousBuffer(out buffer), "ReadAudioBuffer");
                    Mf.Check(buffer.Lock(out var pointer, out _, out var length), "LockAudio");
                    try
                    {
                        var frames = (int)(length / 4);
                        var first = (int)Math.Clamp((long)Math.Ceiling((start.Ticks - time) * (double)SampleRate / TimeSpan.TicksPerSecond), 0, frames);
                        var last = (int)Math.Clamp((long)Math.Ceiling((end.Ticks - time) * (double)SampleRate / TimeSpan.TicksPerSecond), 0, frames);
                        if (last > first)
                        {
                            var pcm = new byte[(last - first) * 4];
                            Marshal.Copy(pointer + first * 4, pcm, 0, pcm.Length);
                            var relative = Math.Max(0, time + (long)first * TimeSpan.TicksPerSecond / SampleRate - start.Ticks);
                            chunk = new AudioChunk(pcm, relative, (long)(last - first) * TimeSpan.TicksPerSecond / SampleRate);
                        }
                    }
                    finally { buffer.Unlock(); }
                }
                finally { Mf.ReleaseCom(buffer); }
            }
            finally { Mf.ReleaseCom(sample); }
            if (chunk != null) yield return chunk;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Mf.ReleaseCom(_reader);
        Mf.ReleaseMf();
    }
}
