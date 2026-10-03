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
    private readonly OcrService _ocr = new();
    private HistoryService _history;
    private readonly ObservableCollection<HistoryEntry> _historyEntries;
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private HistoryEntry? _currentHistory;
    private bool _historyDirty, _switchingDocument, _syncingTarget, _changingStorage;
    private string _startupHotkeyMessage = "";
    private int _documentGeneration;
    private AiBridgeService? _bridge;
    private HotkeyService? _hotkey;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private IDisposable? _desktopActivation;
    private CancellationTokenSource? _recognition;
    private bool _ready, _quitting, _capturing, _recognizing, _sending, _syncingStyle;
    private int _documentRevision;
    private string _originalText = "";
    private string _lastRecognizedText = "";
    private string? _lastBridgeStatus;
    private readonly string[] _args;

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
        Closed += (_, _) => { _recognition?.Cancel(); _tray?.Dispose(); _trayIcon?.Dispose(); _desktopActivation?.Dispose(); _hotkey?.Dispose(); _bridge?.Dispose(); };
        _ready = true; ApplyStyles(); SelectTool(AnnotationTool.Select); RefreshHistorySummary(); RefreshHistoryActions();
    }

    private void InitializeDesktop()
    {
        if (App.IsTestMode) return;
        _desktopActivation = DesktopActivation.Attach(this, () =>
        {
            if (_capturing)
            {
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
        _tray = new Forms.NotifyIcon { Text = "拾屏 · 截图与 OCR", Icon = _trayIcon, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("截图", null, async (_, _) => await Dispatcher.InvokeAsync(CaptureAsync).Task.Unwrap());
        menu.Items.Add("截图历史", null, (_, _) => Dispatcher.Invoke(() => { ShowHistory(); ShowWorkspace(); }));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(() => { ShowWorkspace(); ShowSettings(); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出拾屏", null, (_, _) => Dispatcher.Invoke(Quit));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { ShowHistory(); ShowWorkspace(); });
        try
        {
            _bridge = new AiBridgeService(pairingFile: Path.Combine(SettingsService.ConfigurationDirectory, "browser-pairing.json"));
            _bridge.StatusChanged += (_, _) => Dispatcher.BeginInvoke(UpdateBridgeStatus);
            _bridge.Start(); UpdateBridgeStatus();
        }
        catch (Exception ex) { SetStatus("网页连接：" + ex.Message); }
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
            else { ShowEditor(true); await RecognizeAsync(false); }
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
        _tray?.ShowBalloonTip(2000, "拾屏仍在运行", "可以使用截图快捷键，或双击托盘图标打开工作台。", Forms.ToolTipIcon.Info);
    }
    private void ShowWorkspace() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Quit() { if (_changingStorage) { ShowWorkspace(); SetStatus("正在迁移资料，请完成后再退出。"); return; } if (!SaveCurrentHistory()) { ShowWorkspace(); return; } _autoSaveTimer.Stop(); _quitting = true; Close(); Application.Current.Shutdown(); }
    private void SetStatus(string message) { StatusText.Text = message; }
    private void UpdateBridgeStatus()
    {
        if (_bridge == null || _capturing || _recognizing || _historyDirty || _changingStorage) return;
        var status = _bridge.Status;
        if (_sending && !status.StartsWith("正在", StringComparison.Ordinal)) return;
        if (status != _lastBridgeStatus) { _lastBridgeStatus = status; SetStatus(status); }
    }

    private async void Capture_Click(object sender, RoutedEventArgs e) => await CaptureAsync();
    private async Task CaptureAsync()
    {
        if (_capturing || _changingStorage) return;
        if (!SaveCurrentHistory()) return;
        CancelRecognition();
        _capturing = true; RefreshHistoryActions();
        var ownsCaptureGate = true;
        var wasVisible = IsVisible; Hide();
        try
        {
            await Task.Delay(180);
            var selector = new CaptureWindow { SelectedAiTarget = CurrentTarget };
            selector.SetAiBusy(_sending);
            var completed = selector.ShowDialog();
            if (completed == true && selector.CapturedImage != null)
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
        CancelRecognition(); _switchingDocument = true;
        try
        {
            Surface.Source = bitmap; Surface.ClearSelection(); SelectTool(AnnotationTool.Select);
            WelcomePanel.Visibility = Visibility.Collapsed;
            ImageInfo.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} px";
            OcrText.IsUndoEnabled = false; OcrText.Clear(); OcrText.IsUndoEnabled = true; _originalText = ""; _lastRecognizedText = "";
            OcrHint.Text = "识别后直接修改，无需切换到记事本。";
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
    private void Zoom_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateZoom(); }
    private void UpdateZoom()
    {
        if (Surface.Source == null) return;
        var width = (double)Surface.Source.PixelWidth; var height = (double)Surface.Source.PixelHeight;
        var availableWidth = Math.Max(100, ImageScroll.ActualWidth - 65); var availableHeight = Math.Max(100, ImageScroll.ActualHeight - 65);
        var factor = ZoomChoice.SelectedIndex switch { 1 => 0.5, 2 => 1, 3 => 1.5, 4 => 2, _ => Math.Min(availableWidth / width, availableHeight / height) };
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
    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (!HasImage()) return;
        ShowEditor(true);
        if (_originalText.Length > 0 || OcrText.Text.Length > 0) { OcrText.Focus(); return; }
        await RecognizeAsync(true);
    }
    private async Task RecognizeAsync(bool confirm)
    {
        if (_recognizing || !HasImage()) return;
        if (confirm && OcrText.Text.Length > 0 && OcrText.Text != _lastRecognizedText && MessageBox.Show(this, "重新识别会替换当前文字，是否继续？", "保留文字修改", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _recognizing = true; OcrText.IsReadOnly = true; RefreshPrimaryActions();
        var generation = _documentGeneration;
        var recognition = new CancellationTokenSource(); _recognition = recognition;
        OcrHint.Text = "正在本地识别，请稍候…"; SetStatus("正在识别文字，图片不会上传。 ");
        try
        {
            var result = await _ocr.RecognizeAsync(Surface.Export(), recognition.Token, _settings.OcrEngine);
            if (recognition.IsCancellationRequested || generation != _documentGeneration || !ReferenceEquals(_recognition, recognition)) return;
            if (result.Error != null) { OcrHint.Text = result.Error; SetStatus("识别未完成，当前内容已保留。 "); return; }
            OcrText.BeginChange(); OcrText.Text = result.Text; OcrText.EndChange();
            _originalText = result.Text; _lastRecognizedText = result.Text;
            MarkHistoryDirty();
            OcrHint.Text = result.Text.Length == 0 ? "没有识别到文字，可重新选择清晰的文字区域。" : $"识别完成 · {result.Language} · 可以直接修改";
            SetStatus(result.Text.Length == 0 ? "没有识别到文字。 ": "识别完成，修改文字或选中一段进行整理。 ");
        }
        catch (OperationCanceledException) { if (generation == _documentGeneration) SetStatus("已取消识别。 "); }
        catch (Exception ex) { if (generation == _documentGeneration) OcrHint.Text = "识别未完成：" + ex.Message; }
        finally { if (ReferenceEquals(_recognition, recognition)) { _recognizing = false; OcrText.IsReadOnly = false; _recognition = null; } recognition.Dispose(); RefreshPrimaryActions(); }
    }
    private void OcrText_Changed(object sender, TextChangedEventArgs e) { if (CharacterCount != null) CharacterCount.Text = $"{OcrText.Text.Length} 字"; if (_ready) MarkHistoryDirty(); }
    private void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (_recognizing) { SetStatus("正在识别，请稍后整理文字。 "); return; }
        if (sender is not Button { Tag: string kind }) return;
        if (kind == "original")
        {
            if (_originalText.Length == 0) { SetStatus("尚未识别文字。 "); return; }
            if (OcrText.Text != _originalText && MessageBox.Show(this, "恢复识别原文会替换当前文字修改；操作后仍可撤销。继续吗？", "恢复识别原文", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            OcrText.BeginChange(); OcrText.SelectAll(); OcrText.SelectedText = _originalText; OcrText.EndChange(); SetStatus("已恢复识别原文，可撤销。 "); return;
        }
        Func<string, string> transform = kind switch { "merge" => TextCleanup.MergeLines, "empty" => TextCleanup.RemoveEmptyLines, _ => TextCleanup.CleanSpaces };
        ApplyTextTransform(transform);
    }
    internal void ApplyTextTransform(Func<string, string> transform)
    {
        if (OcrText.Text.Length == 0) { SetStatus("文字区暂无内容。 "); return; }
        var hasSelection = OcrText.SelectionLength > 0;
        var start = hasSelection ? OcrText.SelectionStart : 0;
        var value = hasSelection ? OcrText.SelectedText : OcrText.Text;
        var processed = transform(value);
        OcrText.BeginChange(); OcrText.Select(start, value.Length); OcrText.SelectedText = processed; OcrText.EndChange(); OcrText.Select(start, processed.Length); OcrText.Focus();
        SetStatus(hasSelection ? "已整理选中文字，可撤销。 ": "已整理全文，可撤销。 ");
    }
    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (OcrText.Text.Length == 0) { SetStatus("文字区暂无内容。 "); return; }
        try { Clipboard.SetText(OcrText.Text); SetStatus("已复制编辑后的文字。 "); } catch (Exception ex) { SetStatus("复制未完成：" + ex.Message); }
    }
    private void SaveText_Click(object sender, RoutedEventArgs e)
    {
        if (OcrText.Text.Length == 0) { SetStatus("文字区暂无内容。 "); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "文字文件|*.txt", FileName = "拾屏文字-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" };
        if (dialog.ShowDialog(this) == true) { try { File.WriteAllText(dialog.FileName, OcrText.Text, new UTF8Encoding(false)); SetStatus("文字已保存。 "); } catch (Exception ex) { SetStatus("保存未完成：" + ex.Message); } }
    }
    private void TextUndo_Click(object sender, RoutedEventArgs e) { if (OcrText.CanUndo) OcrText.Undo(); }

    private async void SendText_Click(object sender, RoutedEventArgs e)
    {
        if (OcrText.Text.Length == 0) { SetStatus("请先识别或输入文字。 "); return; }
        if (_sending) return;
        var text = OcrText.Text; var target = CurrentTarget; var generation = _documentGeneration;
        if (!SaveCurrentHistory()) return;
        _sending = true; RefreshHistoryActions(); SetStatus("正在把文字带到 " + AiBridgeService.TargetName(target) + "…");
        try
        {
            var result = _bridge == null ? new BridgeDeliveryResult(false, "请先在设置中连接浏览器扩展。") : await _bridge.SendTextAsync(text, target: target);
            HistoryResult.Text = result.Message;
            if (generation == _documentGeneration) SetStatus(result.Message);
        }
        catch (Exception ex) { if (generation == _documentGeneration) SetStatus("文字传递未完成：" + ex.Message); }
        finally { _sending = false; RefreshHistoryActions(); }
    }
    private async void SendImage_Click(object sender, RoutedEventArgs e)
    {
        if (!HasImage() || _sending) return;
        if (!SaveCurrentHistory()) return;
        await SendImageBatchAsync(new[] { EncodePng(Surface.Export()) }, CurrentTarget);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void OpenExtensionFolder()
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "browser-extension");
        var folder = Directory.Exists(direct) ? direct : FindProjectFolder();
        if (folder == null) { SetStatus("未找到扩展文件夹，请查看随软件提供的使用说明。 "); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = true }); } catch (Exception ex) { SetStatus(ex.Message); }
    }
    private static string? FindProjectFolder()
    {
        var parent = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; parent != null && level < 7; level++, parent = parent.Parent) { var path = Path.Combine(parent.FullName, "browser-extension"); if (Directory.Exists(path)) return path; }
        return null;
    }
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
