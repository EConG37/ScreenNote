using System;

namespace ScreenNote;

internal enum RecordMode { Gif, Video }

/// <summary>What the recorder should produce, decided before the region is picked.</summary>
internal sealed record RecordOptions(RecordMode Mode, int FramesPerSecond, int MaximumEdge, long TargetBytes,
    bool SystemSound = false, bool Microphone = false)
{
    /// <summary>WeChat sends and previews GIFs up to 10 MB without opening a file view.</summary>
    internal const long WeChatBytes = 10 * 1024 * 1024;

    internal bool IsVideo => Mode == RecordMode.Video;
    internal bool Compress => !IsVideo && TargetBytes > 0;
    internal bool Audio => IsVideo && (SystemSound || Microphone);

    internal TimeSpan MaximumDuration => IsVideo ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(60);

    internal string Format => IsVideo ? "MP4" : "GIF";

    /// <summary>Hard byte ceiling while recording; the GIF stays under it before compression runs.</summary>
    internal long RecordingBytes => IsVideo ? 2L * 1024 * 1024 * 1024 : 60 * 1024 * 1024;

    internal static RecordOptions Gif(int framesPerSecond = 15, bool wechat = true) =>
        new(RecordMode.Gif, framesPerSecond, 1440, wechat ? WeChatBytes : 0);

    internal static RecordOptions Video(int framesPerSecond = 30, bool systemSound = true, bool microphone = false) =>
        new(RecordMode.Video, framesPerSecond, 1920, 0, systemSound, microphone);

    internal string Summary() => IsVideo
        ? $"MP4 · {FramesPerSecond} 帧/秒 · 最长 {MaximumDuration.TotalMinutes:F0} 分钟 · {SoundSummary}"
        : $"GIF · {FramesPerSecond} 帧/秒 · 最长 {MaximumDuration.TotalSeconds:F0} 秒" +
          (Compress ? $" · 压到 {TargetBytes / 1048576d:F0} MB 以内" : " · 保留原始体积");

    internal string SoundSummary => SystemSound && Microphone
        ? "带系统声音和麦克风"
        : SystemSound ? "带系统声音" : Microphone ? "带麦克风" : "不含声音";
}