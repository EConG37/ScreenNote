using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace ScreenNote;

internal sealed record VideoRecordingProgress(TimeSpan Elapsed, int Frames, long Bytes);
internal sealed record VideoRecordingResult(string Path, TimeSpan Duration, int Frames, long Bytes, string StopReason, BitmapSource? PosterFrame, string AudioNote);

internal static class VideoRecorder
{
    internal const int FramesPerSecond = 30;
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(5);
    internal const long MaximumBytes = 500L * 1024 * 1024;

    internal static Drawing.Size OutputSize(Drawing.Size source) => OutputSize(source, GifRecorder.MaximumEdge);

    internal static Drawing.Size OutputSize(Drawing.Size source, int maximumEdge)
    {
        var size = GifRecorder.OutputSize(source, maximumEdge);
        // H.264 encoders require even frame dimensions.
        return new Drawing.Size(Math.Max(2, size.Width & ~1), Math.Max(2, size.Height & ~1));
    }

    internal static Task<VideoRecordingResult> RecordAsync(Drawing.Rectangle region, string dataDirectory,
        bool systemSound, bool microphone, CancellationToken stop, IProgress<VideoRecordingProgress>? progress = null) => Task.Run(() =>
    {
        var size = OutputSize(region.Size);
        using var capture = new RegionCapture(region, size);
        using var audio = new AudioCaptureSession(systemSound, microphone);
        return Record(capture.NextFrame, size, dataDirectory, audio, stop, progress, MaximumDuration, MaximumBytes);
    });

    internal static Task<VideoRecordingResult> RecordAsync(Drawing.Rectangle region, RecordOptions options,
        string dataDirectory, CancellationToken stop, IProgress<VideoRecordingProgress>? progress = null,
        Func<bool>? isPaused = null) => Task.Run(() =>
    {
        var size = OutputSize(region.Size, options.MaximumEdge);
        using var capture = new RegionCapture(region, size);
        using var audio = new AudioCaptureSession(options.SystemSound, options.Microphone);
        return Record(capture.NextFrame, size, dataDirectory, audio, stop, progress,
            options.MaximumDuration, options.RecordingBytes, options.FramesPerSecond, isPaused);
    });

    // The injectable frame source lets the complete recording path be tested without reading the desktop.
    // All Media Foundation calls stay on this thread: the sink writer's COM wrappers are apartment-bound.
    internal static VideoRecordingResult Record(Func<BitmapSource> nextFrame, Drawing.Size size, string dataDirectory,
        AudioCaptureSession? audio, CancellationToken stop, IProgress<VideoRecordingProgress>? progress,
        TimeSpan maximumDuration, long maximumBytes, int framesPerSecond = FramesPerSecond,
        Func<bool>? isPaused = null)
    {
        var fps = Math.Clamp(framesPerSecond, 1, 120);
        stop.ThrowIfCancellationRequested();
        var folder = Path.Combine(dataDirectory, "recordings");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "拾屏-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
        var partial = path + ".partial";
        Mp4Writer? writer = null;
        try
        {
            audio?.Prepare();
            var audioNote = audio?.Note.Trim() ?? "";
            writer = new Mp4Writer(partial, size.Width, size.Height, fps, audio?.Active == true ? audio.SampleRate : 0);
            var audioChunks = new ConcurrentQueue<byte[]>();
            var audioSamples = 0L;
            var audioRate = audio?.SampleRate ?? 48000;
            if (audio?.Active == true) audio.BeginPump((pcm, _) =>
            {
                // Do not accumulate minutes of audio while the recording is paused.
                if (isPaused?.Invoke() != true) audioChunks.Enqueue(pcm);
            });
            void DrainAudio()
            {
                while (audioChunks.TryDequeue(out var pcm))
                {
                    var frames = pcm.Length / 4;
                    writer.WriteAudioPcm(pcm, audioSamples * TimeSpan.TicksPerSecond / audioRate, (long)frames * TimeSpan.TicksPerSecond / audioRate);
                    audioSamples += frames;
                }
            }

            var frames = 0;
            var endedAt = TimeSpan.Zero;
            var reason = "";
            var clock = Stopwatch.StartNew();
            var frameTicks = TimeSpan.TicksPerSecond / fps;
            // Capture before encoding: slow compression reduces FPS without speeding up the playback clock.
            var pendingFrame = nextFrame();
            var poster = pendingFrame;
            var firstTicks = clock.Elapsed.Ticks;
            var pausedTicks = 0L;
            var pendingTime = 0L;
            try
            {
                while (true)
                {
                    if (isPaused?.Invoke() == true)
                    {
                        DrainAudio();
                        var held = clock.Elapsed.Ticks;
                        while (isPaused() && !stop.IsCancellationRequested) stop.WaitHandle.WaitOne(40);
                        pausedTicks += clock.Elapsed.Ticks - held;
                        continue;
                    }
                    var active = clock.Elapsed.Ticks - firstTicks - pausedTicks;
                    var wait = TimeSpan.FromTicks(frameTicks * (frames + 1) - active);
                    if (wait > TimeSpan.Zero) stop.WaitHandle.WaitOne(wait);
                    var now = clock.Elapsed.Ticks - firstTicks - pausedTicks;
                    var finishing = stop.IsCancellationRequested || now >= maximumDuration.Ticks;
                    var next = finishing ? null : nextFrame();
                    DrainAudio();
                    writer.WriteVideoFrame(pendingFrame, pendingTime, finishing ? frameTicks : Math.Max(1, now - pendingTime));
                    frames++;
                    endedAt = TimeSpan.FromTicks(now);
                    if (frames % fps == 0) progress?.Report(new VideoRecordingProgress(endedAt, frames, PartialSize(partial)));
                    if (finishing)
                    {
                        if (now >= maximumDuration.Ticks)
                        {
                            reason = "已达到 " + Math.Round(maximumDuration.TotalSeconds) + " 秒上限";
                        }
                        break;
                    }
                    if (PartialSize(partial) >= maximumBytes)
                    {
                        reason = "已达到约 " + Math.Max(1, maximumBytes / (1024 * 1024)) + " MB 上限";
                        break;
                    }
                    pendingFrame = next!;
                    pendingTime = now;
                }
            }
            finally
            {
                audio?.Stop(); // pumps the remaining captured audio into the queue
                DrainAudio();
            }
            clock.Stop();
            writer.Complete();
            var bytes = new FileInfo(partial).Length;
            writer.Dispose(); writer = null;
            File.Move(partial, path);
            return new VideoRecordingResult(path, endedAt, frames, bytes, reason, poster, audioNote);
        }
        catch
        {
            writer?.Dispose();
            try { File.Delete(partial); } catch { }
            throw;
        }
    }

    private static long PartialSize(string partial)
    {
        try { return new FileInfo(partial).Length; }
        catch { return 0; }
    }
}
