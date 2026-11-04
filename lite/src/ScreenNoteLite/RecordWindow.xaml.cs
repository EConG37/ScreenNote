using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenNote;

/// <summary>Picker for what to record: the format, the frame rate and the size budget.</summary>
public partial class RecordWindow : Window
{
    internal RecordOptions Options { get; private set; } = RecordOptions.Gif();
    internal string CompressExistingPath { get; private set; } = "";

    internal RecordWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        if (GifSettings is null || VideoSettings is null || Subtitle is null || Hint is null || StartButton is null) return;
        var video = VideoCard.IsChecked == true;
        GifSettings.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        VideoSettings.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        if (video) RefreshAudio();
        Options = Build();
        Subtitle.Text = video
            ? "视频用 Windows 自带的 H.264 编码，不需要下载额外组件。"
            : "GIF 直接生成，不用再转码，录完就能发。";
        Hint.Text = video
            ? "下一步：在屏幕上拖出要录制的区域。视频按原始像素录制，超出 1920 像素会自动缩小。"
            : "下一步：在屏幕上拖出要录制的区域，松手后倒计时 3 秒开始。";
        StartButton.Content = video ? "选择区域并录制视频" : "选择区域并录制 GIF";
    }

    private void RefreshAudio()
    {
        if (SystemSoundBox is null || MicrophoneBox is null || SoundNote is null) return;
        if (!WasapiAudio.SystemSoundAvailable)
        {
            SystemSoundBox.IsChecked = false;
            SystemSoundBox.IsEnabled = false;
            SystemSoundBox.Content = "系统声音（未找到播放设备）";
        }
        if (!WasapiAudio.MicrophoneAvailable)
        {
            MicrophoneBox.IsChecked = false;
            MicrophoneBox.IsEnabled = false;
            MicrophoneBox.Content = "麦克风（未找到麦克风）";
        }
        SoundNote.Text = SystemSoundBox.IsChecked == true || MicrophoneBox.IsChecked == true
            ? "先点「允许」授权，录制时会把声音混进 MP4。"
            : "不录音，只录画面。";
    }

    private RecordOptions Build()
    {
        var video = VideoCard.IsChecked == true;
        if (video)
        {
            var fps = Number(VideoFps, 30);
            return RecordOptions.Video(fps, SystemSoundBox?.IsChecked == true, MicrophoneBox?.IsChecked == true);
        }
        var gifFps = Number(GifFps, 15);
        var wechat = (GifTarget.SelectedItem as ComboBoxItem)?.Tag as string != "raw";
        return RecordOptions.Gif(gifFps, wechat);
    }

    private static int Number(ComboBox box, int fallback) =>
        box.SelectedItem is ComboBoxItem item && item.Tag is string text
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void Mode_Checked(object sender, RoutedEventArgs e) => Refresh();
    private void Settings_Changed(object sender, SelectionChangedEventArgs e) => Refresh();
    private void Sound_Changed(object sender, RoutedEventArgs e)
    {
        if (SoundNote is not null && SystemSoundBox is not null && MicrophoneBox is not null)
        {
            SoundNote.Text = SystemSoundBox.IsChecked == true || MicrophoneBox.IsChecked == true
                ? "先点「允许」授权，录制时会把声音混进 MP4。"
                : "不录音，只录画面。";
        }
        Options = Build();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        Options = Build();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CompressExisting_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "GIF 动图|*.gif", Title = "选择要处理的 GIF" };
        if (picker.ShowDialog(this) != true) return;
        CompressExistingPath = picker.FileName;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}