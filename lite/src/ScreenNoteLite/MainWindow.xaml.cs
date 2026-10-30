using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ScreenNote;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private HistoryService _history;
    private readonly ObservableCollection<HistoryEntry> _historyEntries;
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private HistoryEntry? _currentHistory;
    private bool _historyDirty, _switchingDocument, _syncingTarget, _changingStorage;
    private string _startupHotkeyMessage = "";
    private int _documentGeneration;
    private readonly BrowserPasteService _browserPaste = new();
    private HotkeyService? _hotkey;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private IDisposable? _desktopActivation;
    private bool _ready, _quitting, _capturing, _sending, _syncingStyle;
    private int _documentRevision;
    private readonly string[] _args;
    private GifRecordingWindow? _gifRecording;

    public MainWindow(string[] args)
    {
        _args = args; _settings = SettingsService.Load();
        _history = new HistoryService(Path.Combine(App.DataDirectory, "history"));
        _historyEntries = new ObservableCollection<HistoryEntry>(_history.LoadEntries());
        InitializeComponent();
        Icon = AppIcon.WindowImage;
        ColorChoice.SelectedIndex = Math.Max(0, ColorChoice.Items.Cast<ComboBoxItem>().ToList().FindIndex(item => Equals(item.Tag, _settings.Color)));
        WidthChoice.SelectedIndex = _settings.StrokeWidth <= 2 ? 0 : _settings.StrokeWidth <= 4 ? 1 : 2;
        FontChoice.SelectedIndex = _settings.TextSize <= 18 ? 0 : _settings.TextSize <= 24 ? 1 : _settings.TextSize <= 32 ? 2 : 3;
        ZoomChoice.SelectedIndex = 0;
        HotkeyHint.Text = _settings.Hotkey;
        EditorTargetChoice.SelectedIndex = HistoryTargetChoice.SelectedIndex = _settings.AiTarget switch { "chatgpt" => 1, "doubao" => 2, _ => 0 };
        Surface.Changed += (_, _) =>
        {
            if (_documentRevision != Surface.DocumentRevision) { _documentRevision = Surface.DocumentRevision; MarkHistoryDirty(); }
            SyncStyleChoices();
        };
        ImageScroll.SizeChanged += (_, _) => UpdateZoom();
        ToolPanel.SizeChanged += (_, e) =>
        {
            var buttons = ToolPanel.Children.OfType<Button>().ToArray();
            if (buttons.Length == 0) return;
            var width = Math.Clamp(Math.Floor(e.NewSize.Width / buttons.Length) - 4, 34, 54);
            foreach (var button in buttons)
            {
                if (button.Width != width) button.Width = width;
                button.FontSize = width < 48 ? 10 : 11;
            }
        };
        HistoryList.ItemsSource = _historyEntries;
        _autoSaveTimer.Tick += (_, _) => SaveCurrentHistory();
        SourceInitialized += (_, _) => InitializeDesktop();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => { _tray?.Dispose(); _trayIcon?.Dispose(); _desktopActivation?.Dispose(); _hotkey?.Dispose(); };
        _ready = true; ApplyStyles(); SelectTool(AnnotationTool.Select); RefreshHistorySummary(); RefreshHistoryActions();
    }

    private void InitializeDesktop()
    {
        if (App.IsTestMode) return;
        _desktopActivation = DesktopActivation.Attach(this, () =>
        {
            if (_capturing)
            {
                if (ActivateCompositionWindow()) return;
                if (_gifRecording?.IsVisible == true) { _gifRecording.Activate(); return; }
                Application.Current.Windows.OfType<CaptureWindow>().FirstOrDefault(window => window.IsVisible)?.Activate();
                return;
            }
            ShowHistory(); ShowWorkspace();
        });
        _hotkey = new HotkeyService(this);
        _hotkey.Pressed += async (_, _) => await CaptureAsync();
        if (_settings.HotkeyPreferenceVersion == 0)
        {
            if (_settings.Hotkey.Count(character => character == '+') >= 3) _settings.Hotkey = "Alt+A";
            _settings.HotkeyPreferenceVersion = 1;
            try { SettingsService.Save(_settings); } catch { }
            HotkeyHint.Text = _settings.Hotkey;
        }
        if (!_hotkey.Register(_settings.Hotkey, out var error, _settings.PreferScreenshotHotkey))
        {
            var suggestion = _hotkey.FindAvailableSuggestion();
            if (suggestion != null && _hotkey.Register(suggestion, out _))
            {
                _settings.Hotkey = suggestion; HotkeyHint.Text = suggestion;
                _startupHotkeyMessage = "原快捷键无法使用，已启用 " + suggestion + "，可在设置里更换。";
                try { SettingsService.Save(_settings); } catch (Exception ex) { _startupHotkeyMessage += " 快捷键偏好未保存：" + ex.Message; }
            }
            else { HotkeyHint.Text = "点击截图或使用托盘"; _startupHotkeyMessage = error + " 可以先使用截图按钮或托盘菜单。"; }
        }
        _trayIcon = AppIcon.LoadTrayIcon();
        _tray = new Forms.NotifyIcon { Text = "拾屏 精简版", Icon = _trayIcon, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("截图", null, async (_, _) => await Dispatcher.InvokeAsync(CaptureAsync).Task.Unwrap());
        menu.Items.Add("截图历史", null, (_, _) => Dispatcher.Invoke(() => { ShowHistory(); ShowWorkspace(); }));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(() => { ShowWorkspace(); ShowSettings(); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出拾屏 精简版", null, (_, _) => Dispatcher.Invoke(Quit));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { ShowHistory(); ShowWorkspace(); });
        SetStatus("截图后可直接复制粘贴到 AI 网页，无需扩展。");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!App.IsTestMode)
        {
            var messages = new[] { App.StorageStartupMessage, _startupHotkeyMessage }.Where(message => message.Length > 0);
            var startupMessage = string.Join(" ", messages);
            if (startupMessage.Length > 0) SetStatus(startupMessage);
        }
        if (_args.Contains("--demo") || _args.Contains("--self-test") || App.PreviewPath != null) LoadImage(DemoImage.Create(), "示例");
        if (App.PreviewPath != null)
        {
            if (_args.Contains("--history-preview"))
            {
                LoadImage(DemoImage.Create(), "示例"); LoadImage(DemoImage.Create(), "示例");
                ShowHistory(); HistoryList.SelectAll();
            }
            else ShowEditor();
            await Task.Delay(300);
            UpdateLayout();
            var visual = (FrameworkElement)Content;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(visual);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            SavePng(bitmap, App.PreviewPath);
            Quit();
        }
        else if (_args.Contains("--self-test"))
        {
            try
            {
                var report = await SelfTests.RunAsync(this);
                File.WriteAllText(Path.Combine(App.DataDirectory, "self-test.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                Quit();
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(App.DataDirectory, "failure.txt"), ex.ToString()); _quitting = true; Application.Current.Shutdown(1); }
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_changingStorage) { e.Cancel = true; SetStatus("正在迁移资料，请完成后再关闭。"); return; }
        if (!SaveCurrentHistory()) { e.Cancel = true; return; }
        if (_quitting || App.IsTestMode) return;
        e.Cancel = true; Hide();
    }
    private void ShowWorkspace() { if (_gifRecording?.IsVisible == true) { _gifRecording.Activate(); return; } if (ActivateCompositionWindow()) return; Show(); WindowState = WindowState.Normal; Activate(); }
    private async void Quit() { if (_changingStorage) { ShowWorkspace(); SetStatus("正在迁移资料，请完成后再退出。"); return; } if (!SaveCurrentHistory()) { ShowWorkspace(); return; } if (_gifRecording != null) await _gifRecording.DiscardAndCloseAsync(); _scrollCapture?.Close(); _collageWindow?.Close(); _autoSaveTimer.Stop(); _quitting = true; Close(); Application.Current.Shutdown(); }
    private void SetStatus(string message) { StatusText.Text = message; }
    private async void Capture_Click(object sender, RoutedEventArgs e) => await CaptureAsync();
    private async Task CaptureAsync()
    {
        if (_collageWindow?.IsVisible == true) { await _collageWindow.AddCaptureAsync(); return; }
        if (_capturing || _changingStorage) return;
        if (!SaveCurrentHistory()) return;
        CancelPendingWork();
        _capturing = true; RefreshHistoryActions();
        var ownsCaptureGate = true;
        var wasVisible = IsVisible; Hide();
        try
        {
            await Task.Delay(180);
            var selector = new CaptureWindow { SelectedAiTarget = CurrentTarget };
            selector.SetAiBusy(_sending);
            var completed = selector.ShowDialog();
            if (completed == true && selector.Action == CaptureAction.Scroll)
            {
                await CaptureScrollAsync(selector.SelectedScreenBounds, wasVisible);
            }
            else if (completed == true && selector.Action == CaptureAction.RecordGif)
            {
                _gifRecording = new GifRecordingWindow(selector.SelectedScreenBounds, App.DataDirectory);
                try { _gifRecording.ShowDialog(); }
                finally { _gifRecording = null; }
                if (wasVisible) ShowWorkspace();
            }
            else if (completed == true && selector.CapturedImage != null)
            {
                _capturing = false; ownsCaptureGate = false; RefreshHistoryActions();
                await HandleCaptureActionAsync(selector, wasVisible);
            }
            else if (wasVisible) ShowWorkspace();
        }
        catch (Exception ex) { ShowWorkspace(); SetStatus("截图未完成：" + ex.Message); }
        finally { if (ownsCaptureGate) _capturing = false; RefreshHistoryActions(); }
    }
    internal void CompleteCapture(BitmapSource image, CaptureAction action)
    {
        if (action == CaptureAction.Edit)
        {
            LoadImage(image); ShowWorkspace(); SetStatus("截图已保留，开始编辑。");
            return;
        }
        if (action != CaptureAction.Copy) throw new ArgumentOutOfRangeException(nameof(action));
        if (!SaveCurrentHistory()) throw new IOException("当前编辑尚未保存。");
        var entry = _history.Create(image);
        _historyEntries.Insert(0, entry); RefreshHistorySummary();
        try
        {
            Clipboard.SetImage(image);
            SetStatus("截图已复制，可直接粘贴；历史已保留。");
            Hide();
        }
        catch (Exception ex)
        {
            ShowHistory(); ShowWorkspace();
            HistoryResult.Text = "截图已保留，但剪贴板暂时不可用。打开这张截图后可重试复制：" + ex.Message;
            SetStatus(HistoryResult.Text);
        }
    }
    internal void LoadImage(BitmapSource bitmap, string origin = "截图")
    {
        if (!SaveCurrentHistory()) throw new IOException("当前编辑尚未保存，请重试后再打开新图片。");
        var nextHistory = _history.Create(bitmap, origin);
        CancelPendingWork(); _switchingDocument = true;
        try
        {
            Surface.Source = bitmap; Surface.ClearSelection(); SelectTool(AnnotationTool.Select);
            WelcomePanel.Visibility = Visibility.Collapsed;
            ImageInfo.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} px";
            _legacyText = ""; _legacyOriginalText = ""; _legacyLastRecognized = "";
            _currentHistory = nextHistory; _historyEntries.Insert(0, nextHistory);
        }
        finally { _switchingDocument = false; }
        _historyDirty = true; SaveCurrentHistory(); RefreshHistorySummary(); ShowEditor(); UpdateZoom();
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.tif;*.tiff|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) ImportPath(dialog.FileName);
    }
    private void ImportPath(string path)
    {
        try
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(Path.GetFullPath(path)); image.EndInit(); image.Freeze(); LoadImage(image, "导入"); SetStatus("图片已导入并保留在历史中。 ");
        }
        catch (Exception ex) { SetStatus("这张图片无法打开：" + ex.Message); }
    }
    private void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) ImportPath(paths[0]); }
    private bool HasImage() { if (Surface.Source != null) return true; SetStatus("请先截图或导入图片。 "); return false; }
    private void Tool_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string tool } && Enum.TryParse<AnnotationTool>(tool, out var parsed)) SelectTool(parsed); }
    private void SelectTool(AnnotationTool tool)
    {
        Surface.Tool = tool;
        foreach (var button in ToolPanel.Children.OfType<Button>())
        {
            var selected = Equals(button.Tag, tool.ToString());
            button.Background = new SolidColorBrush(selected ? Color.FromRgb(32, 35, 31) : Colors.Transparent);
            button.Foreground = selected ? Brushes.White : (Brush)FindResource("Ink");
            button.BorderBrush = Brushes.Transparent;
        }
        PrimaryHighlightButton.Background = tool == AnnotationTool.HighlightBox ? new SolidColorBrush(Color.FromRgb(235, 242, 255)) : Brushes.Transparent;
        PrimaryHighlightButton.BorderBrush = tool == AnnotationTool.HighlightBox ? new SolidColorBrush(Color.FromRgb(183, 206, 249)) : Brushes.Transparent;
    }
    private void Style_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingStyle) return;
        if (sender == ColorChoice && ColorChoice.SelectedItem is ComboBoxItem { Tag: string color }) { Surface.InkColor = (Color)ColorConverter.ConvertFromString(color); _settings.Color = color; }
        if (sender == WidthChoice && WidthChoice.SelectedItem is ComboBoxItem { Tag: string width }) { Surface.StrokeWidth = double.Parse(width, CultureInfo.InvariantCulture); _settings.StrokeWidth = Surface.StrokeWidth; }
        if (sender == FontChoice && FontChoice.SelectedItem is ComboBoxItem { Content: string size }) { Surface.TextSize = double.Parse(size, CultureInfo.InvariantCulture); _settings.TextSize = Surface.TextSize; }
        SaveStylePreferences();
    }
    private void SyncStyleChoices()
    {
        if (!_ready) return;
        _syncingStyle = true;
        try
        {
            var colorIndex = ColorChoice.Items.Cast<ComboBoxItem>().ToList().FindIndex(item => item.Tag is string value && (Color)ColorConverter.ConvertFromString(value) == Surface.InkColor);
            if (colorIndex >= 0) ColorChoice.SelectedIndex = colorIndex;
            WidthChoice.SelectedIndex = Surface.StrokeWidth <= 2 ? 0 : Surface.StrokeWidth <= 4 ? 1 : 2;
            FontChoice.SelectedIndex = Surface.TextSize <= 18 ? 0 : Surface.TextSize <= 24 ? 1 : Surface.TextSize <= 32 ? 2 : 3;
        }
        finally { _syncingStyle = false; }
    }
    private void ApplyStyles()
    {
        if (ColorChoice.SelectedItem is ComboBoxItem { Tag: string color }) { Surface.InkColor = (Color)ColorConverter.ConvertFromString(color); _settings.Color = color; }
        if (WidthChoice.SelectedItem is ComboBoxItem { Tag: string width }) { Surface.StrokeWidth = double.Parse(width, CultureInfo.InvariantCulture); _settings.StrokeWidth = Surface.StrokeWidth; }
        if (FontChoice.SelectedItem is ComboBoxItem { Content: string size }) { Surface.TextSize = double.Parse(size, CultureInfo.InvariantCulture); _settings.TextSize = Surface.TextSize; }
        SaveStylePreferences();
    }
    private void SaveStylePreferences() { if (!App.IsTestMode) { try { SettingsService.Save(_settings); } catch (Exception ex) { SetStatus("偏好未保存：" + ex.Message); } } }
    private void Undo_Click(object sender, RoutedEventArgs e) => Surface.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Surface.Redo();
    private void Delete_Click(object sender, RoutedEventArgs e) => Surface.DeleteSelected();
    private void Zoom_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) { _zoomOverride = null; UpdateZoom(); } }
    private void UpdateZoom()
    {
        if (Surface.Source == null) return;
        var width = (double)Surface.Source.PixelWidth; var height = (double)Surface.Source.PixelHeight;
        var availableWidth = Math.Max(100, ImageScroll.ActualWidth - 40); var availableHeight = Math.Max(100, ImageScroll.ActualHeight - 40);
        var factor = _zoomOverride ?? (ZoomChoice.SelectedIndex switch { 1 => 0.5, 2 => 1, 3 => 1.5, 4 => 2, 5 => 4, _ => Math.Min(availableWidth / width, availableHeight / height) });
        ZoomValue.Text = $"{factor:P0}";
        ImageView.Width = width * factor; ImageView.Height = height * factor;
    }
    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (!HasImage()) return;
        try { Clipboard.SetImage(Surface.Export()); SetStatus("已复制标注后的图片。 "); }
        catch (Exception ex) { SetStatus("剪贴板暂时不可用，请重试：" + ex.Message); }
    }
    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (!HasImage()) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "保存图片", Filter = "PNG 图片|*.png", FileName = "拾屏-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png" };
        if (dialog.ShowDialog(this) != true) return;
        try { SavePng(Surface.Export(), dialog.FileName); SetStatus("图片已保存。 "); }
        catch (Exception ex) { SetStatus("保存未完成：" + ex.Message); }
    }
    internal static void SavePng(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private async void SendImage_Click(object sender, RoutedEventArgs e)
    {
        if (!HasImage() || _sending) return;
        if (!SaveCurrentHistory()) return;
        await SendImageBatchAsync(new[] { EncodePng(Surface.Export()) }, CurrentTarget);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (e.Key)
            {
                case Key.Z: if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) Surface.Redo(); else Surface.Undo(); e.Handled = true; return;
                case Key.Y: Surface.Redo(); e.Handled = true; return;
                case Key.C: CopyImage_Click(this, new()); e.Handled = true; return;
                case Key.S: SaveImage_Click(this, new()); e.Handled = true; return;
                case Key.V:
                    try { if (Clipboard.ContainsImage()) { var image = Clipboard.GetImage(); if (image != null) LoadImage(image, "粘贴"); } } catch (Exception ex) { SetStatus("粘贴未完成：" + ex.Message); }
                    e.Handled = true; return;
            }
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (EditorPanel.Visibility != Visibility.Visible) return;
        if (e.Key == Key.Delete) { Surface.DeleteSelected(); e.Handled = true; return; }
        AnnotationTool? tool = e.Key switch { Key.V => AnnotationTool.Select, Key.P => AnnotationTool.Pen, Key.A => AnnotationTool.Arrow, Key.L => AnnotationTool.Line, Key.R => AnnotationTool.Rectangle, Key.E => AnnotationTool.Ellipse, Key.T => AnnotationTool.Text, Key.H => AnnotationTool.Highlighter, Key.F => AnnotationTool.HighlightBox, Key.N => AnnotationTool.Number, Key.M => AnnotationTool.Mosaic, Key.B => AnnotationTool.Cover, _ => null };
        if (tool.HasValue) { SelectTool(tool.Value); e.Handled = true; }
    }
}
