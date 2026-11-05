using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenNote;

internal static partial class RecordFlowUi
{
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);

    private static void CheckPreviewPlayback(string folder)
    {
        var gifPath = Path.Combine(folder, "preview-changing.gif");
        var writerType = AppAssembly.GetType("ScreenNote.AnimatedGifWriter")!;
        using (var stream = File.Create(gifPath))
        {
            using var writer = (IDisposable)Activator.CreateInstance(writerType, Flags, null, new object[] {stream, 160, 120}, null)!;
            var append = writerType.GetMethod("Append", Flags)!;
            append.Invoke(writer, new object[] {Frame(Colors.Red, 160, 120), 20, 256, 0, 0});
            append.Invoke(writer, new object[] {Frame(Colors.Lime, 160, 120), 30, 256, 0, 0});
            append.Invoke(writer, new object[] {Frame(Colors.Blue, 160, 120), 40, 256, 0, 0});
        }
        var gif = Editor(gifPath);
        try
        {
            gif.Show(); Pump(70);
            Call(gif, "PausePreview");
            Seek(gif, 0);
            var first = ImageColor((BitmapSource)((Image)Field(gif, "PreviewImage")).Source);
            Call(gif, "PlayPreview"); Pump(270);
            var later = ImageColor((BitmapSource)((Image)Field(gif, "PreviewImage")).Source);
            Check(first.R > 200 && later.G > 200, "GIF preview changes from the first frame to a later frame");
            Check((TimeSpan)Field(gif, "_previewPosition") > TimeSpan.FromMilliseconds(150), "GIF playback advances its timeline");
            Call(gif, "PausePreview");
            var paused = (TimeSpan)Field(gif, "_previewPosition"); Pump(120);
            Check((TimeSpan)Field(gif, "_previewPosition") == paused, "GIF pause holds its playback position");
            ((Slider)Field(gif, "PlaybackSlider")).Value = .65;
            var seeked = ImageColor((BitmapSource)((Image)Field(gif, "PreviewImage")).Source);
            Check(seeked.B > 200, "Scrubbing a paused GIF immediately displays the selected frame");
            ((Slider)Field(gif, "StartSlider")).Value = .2;
            ((Slider)Field(gif, "EndSlider")).Value = .45;
            ((CheckBox)Field(gif, "LoopPreview")).IsChecked = false;
            Seek(gif, .2); Call(gif, "PlayPreview"); Pump(380);
            Check(!(bool)Field(gif, "_previewPlaying") && Math.Abs(((TimeSpan)Field(gif, "_previewPosition")).TotalSeconds - .45) < .02,
                "GIF playback stops at the selected trim end");
            ((CheckBox)Field(gif, "LoopPreview")).IsChecked = true;
            Seek(gif, .2); Call(gif, "PlayPreview");
            var last = .2; var wrapped = false;
            for (var index = 0; index < 8; index++)
            {
                Pump(55); var now = ((TimeSpan)Field(gif, "_previewPosition")).TotalSeconds;
                wrapped |= now < last - .05; last = now;
            }
            Check(wrapped && (bool)Field(gif, "_previewPlaying"), "GIF loops within the selected trim range");
            Shot(gif, Path.Combine(folder, "07-gif-playing.png"));
        }
        finally { gif.Close(); }
        Check(!((System.Windows.Threading.DispatcherTimer)Field(gif, "_previewTimer")).IsEnabled, "Closing the GIF editor stops its animation timer");

        var mp4Path = Path.Combine(folder, "preview-changing-with-audio.mp4");
        ChangingVideo(mp4Path);
        var video = Editor(mp4Path);
        var media = (MediaElement)Field(video, "VideoPreview");
        media.Volume = 0; media.IsMuted = true;
        try
        {
            video.Topmost = true;
            video.Show();
            AwaitPreview(() => (bool)Field(video, "_previewReady"), 7000, "Video preview failed to open: " + ((TextBlock)Field(video, "StatusText")).Text);
            Check(media.HasVideo && media.NaturalVideoWidth == 320, "MP4 preview opens a real video stream");
            Check(media.HasAudio, "MP4 preview opens its audio stream");
            Call(video, "PausePreview"); Seek(video, .15);
            AwaitPreview(() => ScreenColor(media).R > 150, 3000, "Paused video did not render its red opening frame.");
            var red = ScreenColor(media);
            Seek(video, 1.25);
            AwaitPreview(() => ScreenColor(media).B > 150, 3000, "Paused video did not render its blue later frame.");
            var blue = ScreenColor(media);
            Check(red.R > red.B + 80 && blue.B > blue.R + 80, "MP4 scrubbing visibly changes the rendered video frame");
            Seek(video, .7); Call(video, "PlayPreview"); Pump(650);
            Check(media.Position.TotalSeconds > 1.1 && ScreenColor(media).B > 150, "Playing MP4 advances time and visibly changes its frame");
            Call(video, "PausePreview");
            var at = media.Position.TotalSeconds; Pump(140);
            Check(Math.Abs(media.Position.TotalSeconds - at) < .05, "MP4 pause holds its playback position");
            ((Slider)Field(video, "StartSlider")).Value = 1.1;
            ((Slider)Field(video, "EndSlider")).Value = 1.5;
            ((CheckBox)Field(video, "LoopPreview")).IsChecked = false;
            Seek(video, 1.1); Call(video, "PlayPreview"); Pump(650);
            Check(!(bool)Field(video, "_previewPlaying"), "MP4 preview respects the selected trim end");
            ((CheckBox)Field(video, "LoopPreview")).IsChecked = true;
            Seek(video, 1.1); Call(video, "PlayPreview"); Pump(650);
            Check((bool)Field(video, "_previewPlaying") && media.Position.TotalSeconds is >= 1.05 and <= 1.6, "MP4 preview loops the selected segment");
            Shot(video, Path.Combine(folder, "08-video-playing.png"));
        }
        finally { video.Close(); }
        Check(media.Source == null && !((System.Windows.Threading.DispatcherTimer)Field(video, "_previewTimer")).IsEnabled,
            "Closing the video editor releases playback and stops the timer");
    }

    private static RecordEditorWindow Editor(string path) =>
        (RecordEditorWindow)Activator.CreateInstance(typeof(RecordEditorWindow), Flags, null, new object[] {path}, null)!;
    private static void Seek(RecordEditorWindow window, double seconds) =>
        window.GetType().GetMethod("SeekPreview", Flags)!.Invoke(window, new object[] {TimeSpan.FromSeconds(seconds)});
    private static Color ImageColor(BitmapSource image)
    {
        var pixel = new byte[4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }
    private static Color ScreenColor(FrameworkElement element)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        var dc = GetDC(0);
        try { var value = GetPixel(dc, (int)point.X, (int)point.Y); return Color.FromRgb((byte)value, (byte)(value >> 8), (byte)(value >> 16)); }
        finally { ReleaseDC(0, dc); }
    }
    private static void AwaitPreview(Func<bool> ready, int milliseconds, string failure)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!ready()) { if (clock.ElapsedMilliseconds > milliseconds) throw new InvalidOperationException(failure); Pump(30); }
    }
    private static void ChangingVideo(string path)
    {
        var type = AppAssembly.GetType("ScreenNote.Mp4Writer")!;
        using var writer = (IDisposable)Activator.CreateInstance(type, Flags, null, new object[] {path, 320, 180, 30, 48000, 24000}, null)!;
        var video = type.GetMethod("WriteVideoFrame", Flags, null, new[] {typeof(BitmapSource), typeof(long), typeof(long)}, null)!;
        var audio = type.GetMethod("WriteAudioPcm", Flags)!;
        for (var index = 0; index < 60; index++)
        {
            var time = (long)index * TimeSpan.TicksPerSecond / 30;
            var duration = (long)(index + 1) * TimeSpan.TicksPerSecond / 30 - time;
            video.Invoke(writer, new object[] {Frame(index < 30 ? Colors.Red : Colors.Blue, 320, 180), time, duration});
            audio.Invoke(writer, new object[] {new byte[1600 * 4], time, duration});
        }
        type.GetMethod("Complete", Flags)!.Invoke(writer, null);
    }
}
