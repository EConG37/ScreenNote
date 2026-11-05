using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ScreenNote;

public partial class RecordEditorWindow
{
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly Stopwatch _gifPreviewClock = new();
    private TimeSpan _previewPosition, _gifPreviewAnchor;
    private bool _playbackInitialized, _previewReady, _previewPlaying, _syncingPlayback;
    private bool _seekingPreview, _resumeAfterSeek;

    private void InitializePlayback()
    {
        if (_playbackInitialized || _closed) return;
        _playbackInitialized = true;
        _previewTimer.Tick += PreviewTick;
        MutePreview.Visibility = _source.Mode == RecordMode.Video ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreviewRange();
        if (_source.Mode == RecordMode.Gif)
        {
            _previewReady = true;
            SeekPreview(_start);
            PlayPreview();
        }
        else
        {
            PlayPauseButton.IsEnabled = false;
            UpdatePlayAppearance("正在加载");
            VideoPreview.Visibility = Visibility.Visible;
            VideoPreview.Source = new Uri(_source.Path, UriKind.Absolute);
            // Pre-roll is required before the native player exposes a seekable frame.
            VideoPreview.Play();
        }
    }

    private void VideoPreview_Opened(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        _previewReady = true;
        MutePreview.IsEnabled = VideoPreview.HasAudio;
        PreviewImage.Visibility = Visibility.Collapsed;
        PlayPauseButton.IsEnabled = true;
        SeekPreview(_start);
        if (PlaybackControls.IsEnabled) PlayPreview(); else PausePreview();
    }

    private void VideoPreview_Ended(object sender, RoutedEventArgs e)
    {
        if (_closed || !_previewPlaying) return;
        FinishPreviewRange();
    }

    private void VideoPreview_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        e.Handled = true;
        if (_closed) return;
        PausePreview();
        _previewReady = false;
        VideoPreview.Close();
        VideoPreview.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;
        UpdatePlayAppearance("预览不可用");
        PlayPauseButton.IsEnabled = false;
        StatusText.Text = "视频预览打开失败：" + e.ErrorException.Message;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePreview();

    private void TogglePreview()
    {
        if (_previewPlaying) PausePreview(); else PlayPreview();
    }

    private void PlayPreview()
    {
        if (!_previewReady || _closed || _end <= _start) return;
        _seekingPreview = false;
        if (_previewPosition < _start || _previewPosition >= _end - TimeSpan.FromMilliseconds(10)) SeekPreview(_start);
        _previewPlaying = true;
        _gifPreviewAnchor = _previewPosition;
        _gifPreviewClock.Restart();
        if (_source.Mode == RecordMode.Video) VideoPreview.Play();
        UpdatePlayAppearance();
        _previewTimer.Start();
    }

    private void PausePreview()
    {
        if (!_playbackInitialized) return;
        if (_previewPlaying)
            _previewPosition = _source.Mode == RecordMode.Video ? VideoPreview.Position : _gifPreviewAnchor + _gifPreviewClock.Elapsed;
        _previewPlaying = false;
        _previewTimer.Stop();
        _gifPreviewClock.Reset();
        if (_source.Mode == RecordMode.Video && _previewReady) VideoPreview.Pause();
        UpdatePlayAppearance();
        UpdatePlaybackUi();
    }

    private void RestartPreview_Click(object sender, RoutedEventArgs e)
    {
        SeekPreview(_start);
        PlayPreview();
    }

    private void PreviewTick(object? sender, EventArgs e)
    {
        if (_closed || !_previewPlaying || _seekingPreview) return;
        _previewPosition = _source.Mode == RecordMode.Video ? VideoPreview.Position : _gifPreviewAnchor + _gifPreviewClock.Elapsed;
        if (_previewPosition >= _end)
        {
            FinishPreviewRange();
            return;
        }
        if (_source.Mode == RecordMode.Gif) PreviewImage.Source = _source.PreviewGifFrame(_previewPosition);
        UpdatePlaybackUi();
    }

    private void FinishPreviewRange()
    {
        if (LoopPreview.IsChecked == true)
        {
            SeekPreview(_start);
            PlayPreview();
        }
        else
        {
            PausePreview();
            SeekPreview(_end);
        }
    }

    private void SeekPreview(TimeSpan position)
    {
        if (!_playbackInitialized) return;
        _previewPosition = TimeSpan.FromTicks(Math.Clamp(position.Ticks, _start.Ticks, Math.Max(_start.Ticks, _end.Ticks)));
        _gifPreviewAnchor = _previewPosition;
        if (_previewPlaying) _gifPreviewClock.Restart();
        if (_source.Mode == RecordMode.Video)
        {
            if (_previewReady) VideoPreview.Position = _previewPosition;
        }
        else PreviewImage.Source = _source.PreviewGifFrame(_previewPosition >= _end
            ? TimeSpan.FromTicks(Math.Max(_start.Ticks, _end.Ticks - 1)) : _previewPosition);
        UpdatePlaybackUi();
    }

    private void UpdatePreviewRange()
    {
        if (!_playbackInitialized) return;
        _syncingPlayback = true;
        try
        {
            PlaybackSlider.Minimum = 0;
            PlaybackSlider.Maximum = Math.Max(.001, _source.Duration.TotalSeconds);
        }
        finally { _syncingPlayback = false; }
        SeekPreview(_previewPosition);
    }

    private void UpdatePlaybackUi()
    {
        if (!_playbackInitialized) return;
        _syncingPlayback = true;
        try { PlaybackSlider.Value = Math.Clamp(_previewPosition.TotalSeconds, PlaybackSlider.Minimum, PlaybackSlider.Maximum); }
        finally { _syncingPlayback = false; }
        PlaybackTimeText.Text = PreviewClock(_previewPosition) + " / " + PreviewClock(_source.Duration);
        UpdateTimelineCursor();
    }

    private void PlaybackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_playbackInitialized && !_syncingPlayback) SeekPreview(TimeSpan.FromSeconds(e.NewValue));
    }

    private void PlaybackSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _seekingPreview = true;
        _resumeAfterSeek = _previewPlaying;
        PausePreview();
    }

    private void PlaybackSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _seekingPreview = false;
        if (_resumeAfterSeek) PlayPreview();
        _resumeAfterSeek = false;
    }

    private void MutePreview_Changed(object sender, RoutedEventArgs e)
    {
        if (VideoPreview != null) VideoPreview.IsMuted = MutePreview.IsChecked == true;
    }

    private static string PreviewClock(TimeSpan value) => Math.Max(0, (int)value.TotalMinutes) + ":" + value.Seconds.ToString("00") + "." + (value.Milliseconds / 100);

    private void ClosePlayback()
    {
        _previewPlaying = false;
        _previewTimer.Stop();
        _previewTimer.Tick -= PreviewTick;
        _gifPreviewClock.Stop();
        VideoPreview.Close();
        VideoPreview.Source = null;
        PreviewImage.Source = null;
    }
}
