using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenNote;

/// <summary>WASAPI helpers: default-endpoint probing plus the interop shared by both capture sources.</summary>
internal static class WasapiAudio
{
    internal static bool SystemSoundAvailable => HasDefaultEndpoint(0);
    internal static bool MicrophoneAvailable => HasDefaultEndpoint(1);

    private static bool HasDefaultEndpoint(int dataFlow)
    {
        var initialized = CoInitializeEx(0, 0);
        object? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = new MMDeviceEnumeratorCom();
            return ((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(dataFlow, 0, out device) >= 0;
        }
        catch { return false; }
        finally
        {
            ReleaseCom(device); ReleaseCom(enumerator);
            if (initialized == 0) CoUninitialize();
        }
    }

    internal static void ReleaseCom(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }

    [DllImport("ole32.dll", ExactSpelling = true)] internal static extern int CoInitializeEx(nint reserved, int apartmentState);
    [DllImport("ole32.dll", ExactSpelling = true)] internal static extern void CoUninitialize();

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal sealed class MMDeviceEnumeratorCom { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsctx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, nint properties);
        [PreserveSig] int GetId(out nint id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, nint format, nint audioSessionGuid);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int padding);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closestMatch);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out int frames, out int flags, nint devicePosition, nint qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WaveFormat
    {
        internal ushort Tag;
        internal ushort Channels;
        internal int SamplesPerSecond;
        internal int AverageBytesPerSecond;
        internal ushort BlockAlign;
        internal ushort BitsPerSample;
        internal ushort ExtraSize;
    }
}

/// <summary>Captures the default render device (loopback) and/or the default microphone,
/// mixes them into one interleaved stereo PCM16 stream, and pushes chunks to the recorder.</summary>
internal sealed class AudioCaptureSession : IDisposable
{
    private readonly bool _wantSystem, _wantMicrophone;
    private Endpoint? _system, _mic;
    private Thread? _pump;
    private volatile bool _stop;
    private Action<byte[], int>? _onChunk;

    internal AudioCaptureSession(bool systemSound, bool microphone) { _wantSystem = systemSound; _wantMicrophone = microphone; }

    internal bool Active { get; private set; }
    internal int SampleRate { get; private set; }
    internal string Note { get; private set; } = "";

    /// <summary>Opens the requested devices and starts filling their queues; the mixing pump starts with <see cref="BeginPump"/>.</summary>
    internal void Prepare()
    {
        if (_wantSystem)
        {
            try { _system = new Endpoint(loopback: true); _system.Start(); }
            catch (Exception ex) { Note += "系统声音未能采集：" + ex.Message + " "; }
        }
        if (_wantMicrophone)
        {
            try { _mic = new Endpoint(loopback: false); _mic.Start(); }
            catch (Exception ex) { Note += "麦克风未能采集：" + ex.Message + " "; }
        }
        if (_system == null && _mic == null) { Active = false; return; }
        Active = true;
        SampleRate = (_system ?? _mic)!.SampleRate;
    }

    internal void BeginPump(Action<byte[], int> onChunk)
    {
        if (!Active) return;
        _onChunk = onChunk;
        _stop = false;
        _pump = new Thread(Pump) { IsBackground = true, Name = "音频混音" };
        _pump.Start();
    }

    internal void Stop()
    {
        _system?.StopCapture();
        _mic?.StopCapture();
        if (_pump == null) return;
        _stop = true;
        _pump.Join();
    }

    public void Dispose() => Stop();

    private void Pump()
    {
        while (!_stop)
        {
            Thread.Sleep(15);
            Emit();
        }
        Emit();
    }

    private void Emit()
    {
        var system = _system?.DrainAll();
        var mic = _mic?.DrainAll();
        if ((system == null || system.Length == 0) && (mic == null || mic.Length == 0)) return;
        byte[] pcm;
        if (_system != null && _mic != null) pcm = Mix(system ?? [], mic ?? [], _mic.SampleRate, SampleRate);
        else pcm = (system == null || system.Length == 0) ? mic! : system!;
        if (pcm.Length > 0) _onChunk?.Invoke(pcm, pcm.Length / 4);
    }

    private static short[] ToShorts(byte[] pcm, int frames)
    {
        var samples = new short[frames * 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, frames * 4);
        return samples;
    }

    private static short[] Resample(short[] source, int sourceRate, int targetRate)
    {
        var sourceFrames = source.Length / 2;
        var targetFrames = (int)((long)sourceFrames * targetRate / sourceRate);
        if (targetFrames == sourceFrames) return source;
        var output = new short[targetFrames * 2];
        for (var i = 0; i < targetFrames; i++)
        {
            var position = (double)i * sourceRate / targetRate;
            var low = (int)position;
            var high = Math.Min(low + 1, sourceFrames - 1);
            var fraction = (float)(position - low);
            output[i * 2] = (short)(source[low * 2] + (source[high * 2] - source[low * 2]) * fraction);
            output[i * 2 + 1] = (short)(source[low * 2 + 1] + (source[high * 2 + 1] - source[low * 2 + 1]) * fraction);
        }
        return output;
    }

    private static byte[] Mix(byte[] system, byte[] mic, int micRate, int masterRate)
    {
        var systemFrames = system.Length / 4;
        var micSamples = Resample(ToShorts(mic, mic.Length / 4), micRate, masterRate);
        var micFrames = micSamples.Length / 2;
        var frames = Math.Max(systemFrames, micFrames);
        if (frames == 0) return [];
        var systemSamples = ToShorts(system, systemFrames);
        var mixed = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                var value = (i < systemFrames ? systemSamples[i * 2 + channel] : 0) + (i < micFrames ? micSamples[i * 2 + channel] : 0);
                mixed[i * 2 + channel] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            }
        }
        var pcm = new byte[frames * 4];
        Buffer.BlockCopy(mixed, 0, pcm, 0, pcm.Length);
        return pcm;
    }

    /// <summary>One capture device on its own COM thread; captured PCM16 stereo chunks land in <see cref="Chunks"/>.</summary>
    private sealed class Endpoint
    {
        private readonly bool _loopback;
        private Thread? _thread;
        private volatile bool _stop;
        internal readonly ConcurrentQueue<byte[]> Chunks = new();
        internal int SampleRate { get; private set; }
        private int _deviceChannels = 2;
        internal Exception? Failure { get; private set; }

        internal Endpoint(bool loopback) { _loopback = loopback; }

        internal void Start()
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = _loopback ? "系统声音采集" : "麦克风采集" };
            _thread.Start();
            ready.Wait();
            if (Failure != null) throw new InvalidOperationException(Failure.Message);
        }

        internal void StopCapture()
        {
            _stop = true;
            _thread?.Join();
        }

        internal byte[] DrainAll()
        {
            if (Chunks.IsEmpty) return [];
            var total = 0;
            var parts = new List<byte[]>();
            while (Chunks.TryDequeue(out var chunk)) { parts.Add(chunk); total += chunk.Length; }
            var combined = new byte[total];
            var offset = 0;
            foreach (var part in parts) { Buffer.BlockCopy(part, 0, combined, offset, part.Length); offset += part.Length; }
            return combined;
        }

        private void Run(ManualResetEventSlim ready)
        {
            WasapiAudio.CoInitializeEx(0, 2);
            object? enumerator = null, device = null, clientObject = null, captureObject = null;
            var signaled = false;
            try
            {
                enumerator = new WasapiAudio.MMDeviceEnumeratorCom();
                Check(((WasapiAudio.IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(_loopback ? 0 : 1, 0, out var deviceInterface),
                    _loopback ? "未找到系统播放设备" : "未找到麦克风");
                device = deviceInterface;
                var audioClientId = typeof(WasapiAudio.IAudioClient).GUID;
                Check(((WasapiAudio.IMMDevice)device).Activate(ref audioClientId, 1, 0, out clientObject), "音频设备激活失败");
                var client = (WasapiAudio.IAudioClient)clientObject;
                Check(client.GetMixFormat(out var formatPointer), "读取音频格式失败");
                bool isFloat, isPcm16;
                try
                {
                    var format = Marshal.PtrToStructure<WasapiAudio.WaveFormat>(formatPointer);
                    var subFormat = format.Tag == 0xFFFE ? Marshal.ReadInt16(formatPointer, 24) : (short)format.Tag;
                    isFloat = subFormat == 3; isPcm16 = subFormat == 1;
                    SampleRate = format.SamplesPerSecond;
                    _deviceChannels = Math.Max(1, (int)format.Channels);
                }
                finally { Marshal.FreeCoTaskMem(formatPointer); }
                if (!isFloat && !isPcm16) throw new InvalidOperationException("设备音频格式不是 PCM");
                Check(client.GetMixFormat(out var initFormat), "读取音频格式失败");
                try { Check(client.Initialize(0, _loopback ? 0x00020000 : 0, 1_000_000, 0, initFormat, 0), "音频采集初始化失败"); }
                finally { Marshal.FreeCoTaskMem(initFormat); }
                var captureId = typeof(WasapiAudio.IAudioCaptureClient).GUID;
                Check(client.GetService(ref captureId, out captureObject), "音频采集服务不可用");
                var capture = (WasapiAudio.IAudioCaptureClient)captureObject;
                Check(client.Start(), "音频采集启动失败");
                signaled = true; ready.Set();
                while (!_stop)
                {
                    Thread.Sleep(5);
                    while (capture.GetNextPacketSize(out var packetFrames) >= 0 && packetFrames > 0)
                    {
                        if (capture.GetBuffer(out var data, out var frames, out var flags, 0, 0) < 0) break;
                        try
                        {
                            if (frames > 0) Chunks.Enqueue(Convert(data, frames, flags, isFloat));
                        }
                        finally { capture.ReleaseBuffer(frames); }
                    }
                }
                client.Stop();
            }
            catch (Exception ex)
            {
                Failure = ex;
                if (!signaled) { signaled = true; ready.Set(); }
            }
            finally
            {
                WasapiAudio.ReleaseCom(captureObject); WasapiAudio.ReleaseCom(clientObject);
                WasapiAudio.ReleaseCom(device); WasapiAudio.ReleaseCom(enumerator);
                WasapiAudio.CoUninitialize();
            }
        }

        private byte[] Convert(nint data, int frames, int flags, bool isFloat)
        {
            var pcm = new byte[frames * 4];
            if ((flags & 0x2) != 0) return pcm; // device reported silence: zeros are already correct
            if (isFloat)
            {
                var samples = new float[frames * _deviceChannels];
                Marshal.Copy(data, samples, 0, samples.Length);
                for (var i = 0; i < frames; i++)
                {
                    var left = Math.Clamp((int)(samples[i * _deviceChannels] * 32768f), short.MinValue, short.MaxValue);
                    var right = _deviceChannels > 1 ? Math.Clamp((int)(samples[i * _deviceChannels + 1] * 32768f), short.MinValue, short.MaxValue) : left;
                    BitConverter.TryWriteBytes(pcm.AsSpan(i * 4, 2), (short)left);
                    BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2, 2), (short)right);
                }
            }
            else
            {
                var samples = new short[frames * _deviceChannels];
                Marshal.Copy(data, samples, 0, samples.Length);
                for (var i = 0; i < frames; i++)
                {
                    var left = samples[i * _deviceChannels];
                    var right = _deviceChannels > 1 ? samples[i * _deviceChannels + 1] : left;
                    BitConverter.TryWriteBytes(pcm.AsSpan(i * 4, 2), left);
                    BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2, 2), right);
                }
            }
            return pcm;
        }

        private static void Check(int hr, string message)
        {
            if (hr < 0) throw new InvalidOperationException($"{message}（0x{unchecked((uint)hr):X8}）。");
        }
    }
}
