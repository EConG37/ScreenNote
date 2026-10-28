using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;

namespace ScreenNote;

public partial class MainWindow
{
    private Window? _settingsWindow;

    internal async Task<int> ChangeStorageLocationAsync(string directory, AppSettings? candidate = null)
    {
        if (_changingStorage || _capturing || _sending) throw new IOException("当前操作尚未结束，请完成后再修改资料位置。");
        var sourceSaved = SaveCurrentHistory();
        var unsavedImage = !sourceSaved ? Surface.Export() : null;
        unsavedImage?.Freeze();
        var unsavedDocument = !sourceSaved ? Surface.ExportDocument() : "";
        var unsavedText = OcrText.Text;
        var originalOcr = _originalText; var lastRecognized = _lastRecognizedText;
        var destination = StorageLocationService.NormalizeDataDirectory(directory);
        var previous = App.DataDirectory;
        var proposed = candidate ?? CopySettings();
        proposed.StorageDirectory = StorageLocationService.IsSameDirectory(destination, App.DefaultDataDirectory) ? "" : destination;
        proposed.LastDataDirectory = destination; proposed.StorageInitialized = true;
        if (StorageLocationService.IsSameDirectory(previous, destination))
        {
            if (!sourceSaved) throw new IOException("当前编辑尚未保存，请选择其他可写的资料文件夹。");
            SettingsService.Save(proposed); ApplySettings(proposed); return 0;
        }
        var currentId = _currentHistory?.Id;
        var selectedIds = HistoryList.SelectedItems.Cast<HistoryEntry>().Select(entry => entry.Id).ToHashSet();
        var requiredIds = _historyEntries.Select(entry => entry.Id).ToHashSet();
        _changingStorage = true; CancelRecognition();
        try
        {
            var prepared = await Task.Run(() =>
            {
                var count = StorageLocationService.CopyHistory(previous, destination);
                var store = new HistoryService(Path.Combine(destination, "history"));
                var entries = store.LoadEntries();
                if (!requiredIds.IsSubsetOf(entries.Select(entry => entry.Id).ToHashSet()))
                    throw new IOException("部分资料在新位置无法重新打开，继续使用原位置。");
                if (unsavedImage != null)
                {
                    var active = entries.FirstOrDefault(entry => entry.Id == currentId)
                        ?? throw new IOException("当前图片在新位置无法恢复，资料位置没有改变。");
                    store.Update(active, unsavedImage, unsavedDocument, unsavedText, originalOcr, lastRecognized);
                }
                return (count, store, entries);
            });
            // The active library changes only after all records reopen and settings commit.
            await Dispatcher.InvokeAsync(() =>
            {
                SettingsService.Save(proposed);
                App.SetDataDirectory(destination); ApplySettings(proposed); _history = prepared.store;
                _historyEntries.Clear();
                foreach (var entry in prepared.entries) _historyEntries.Add(entry);
                _currentHistory = _historyEntries.FirstOrDefault(entry => entry.Id == currentId);
                foreach (var entry in _historyEntries.Where(entry => selectedIds.Contains(entry.Id))) HistoryList.SelectedItems.Add(entry);
                _historyDirty = false; RefreshHistorySummary();
            });
            return prepared.count;
        }
        finally { _changingStorage = false; }
    }

    private AppSettings CopySettings() => new()
    {
        Hotkey = _settings.Hotkey, StartWithWindows = _settings.StartWithWindows,
        PreferScreenshotHotkey = _settings.PreferScreenshotHotkey, HotkeyPreferenceVersion = _settings.HotkeyPreferenceVersion, OcrEngine = _settings.OcrEngine,
        AiTarget = _settings.AiTarget,
        Color = _settings.Color, StrokeWidth = _settings.StrokeWidth, TextSize = _settings.TextSize,
        StorageDirectory = _settings.StorageDirectory, StorageInitialized = _settings.StorageInitialized,
        LastDataDirectory = _settings.LastDataDirectory
    };

    private void ApplySettings(AppSettings settings)
    {
        _settings.Hotkey = settings.Hotkey; _settings.StartWithWindows = settings.StartWithWindows;
        _settings.PreferScreenshotHotkey = settings.PreferScreenshotHotkey; _settings.OcrEngine = settings.OcrEngine;
        _settings.HotkeyPreferenceVersion = settings.HotkeyPreferenceVersion; _settings.AiTarget = settings.AiTarget;
        _settings.StorageDirectory = settings.StorageDirectory; _settings.StorageInitialized = settings.StorageInitialized;
        _settings.LastDataDirectory = settings.LastDataDirectory;
    }

    private void ShowSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        var area = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var dialog = new Window
        {
            Title = "拾屏设置", Owner = this, Width = Math.Min(560, area.Width / dpi.DpiScaleX - 32),
            Height = Math.Min(860, area.Height / dpi.DpiScaleY - 32), ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = AppIcon.WindowImage
        };
        _settingsWindow = dialog;
        var grid = new Grid { Background = Brushes.White };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        grid.Children.Add(scroll);
        TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 6, 0, 6) };
        void Heading(string text) => panel.Children.Add(new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 10) });
        void Divider() => panel.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 16) });

        Heading("截图快捷键");
        panel.Children.Add(Hint("点击下方后直接按快捷键，不需要输入名称。推荐 Alt+A，也可只用 PrintScreen。"));
        var combination = new TextBox { Name = "ShortcutInput", Text = _hotkey?.CurrentCombination ?? _settings.Hotkey, IsReadOnly = true, FontSize = 18, MinHeight = 44, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "点击，然后按下想使用的快捷键；Esc 放弃本次录入" };
        panel.Children.Add(combination);
        var hotkeyFeedback = Hint(""); panel.Children.Add(hotkeyFeedback);
        var priority = new CheckBox { Name = "PriorityShortcutChoice", Content = "此快捷键优先使用拾屏", IsChecked = _settings.PreferScreenshotHotkey, Margin = new Thickness(0, 6, 0, 8) };
        panel.Children.Add(priority);
        void CheckHotkey()
        {
            if (!HotkeyService.TryNormalize(combination.Text, out _)) { hotkeyFeedback.Text = "请直接按下组合，例如 Alt+A 或 Ctrl+Alt+A。"; return; }
            if (_hotkey == null) { hotkeyFeedback.Text = "快捷键格式正确。"; return; }
            hotkeyFeedback.Text = _hotkey.Probe(combination.Text, out var error) ? "按键已记录，保存后生效。" : priority.IsChecked == true && error.Contains("占用") ? "其他软件登记了这个组合；优先模式可以沿用，无需增加按键。" : error;
        }
        var hotkeyActions = new StackPanel { Orientation = Orientation.Horizontal };
        var check = new Button { Content = "检测占用" }; check.Click += (_, _) => CheckHotkey();
        var suggest = new Button { Name = "SuggestShortcutButton", Content = "推荐可用组合" };
        suggest.Click += (_, _) =>
        {
            var recommendation = _hotkey?.FindAvailableSuggestion();
            if (recommendation == null) { hotkeyFeedback.Text = "暂未找到可用建议，可以输入其他组合检测。"; return; }
            combination.Text = recommendation; CheckHotkey();
        };
        hotkeyActions.Children.Add(check); hotkeyActions.Children.Add(suggest); panel.Children.Add(hotkeyActions);
        panel.Children.Add(Hint("优先模式只处理选定的截图组合，其他按键照常使用。建议使用 1–2 个按键，最多两个辅助键加一个主键。"));
        var beforeRecording = combination.Text;
        combination.GotKeyboardFocus += (_, _) => { beforeRecording = combination.Text; hotkeyFeedback.Text = "请按下想使用的快捷键…"; if (_hotkey != null && !_hotkey.BeginRecording(out var error)) hotkeyFeedback.Text = error; };
        combination.LostKeyboardFocus += (_, _) => _hotkey?.EndRecording();
        void RecordKey(Key key, ModifierKeys modifiers)
        {
            if (key == Key.Escape) { combination.Text = beforeRecording; Keyboard.ClearFocus(); CheckHotkey(); return; }
            if (HotkeyService.TryFromKey(key, modifiers, out var recorded)) { combination.Text = recorded; CheckHotkey(); }
            else hotkeyFeedback.Text = "请用 Alt+A、Ctrl+Alt+A，或 PrintScreen 等独立功能键；最多三个按键。";
        }
        EventHandler<RecordedShortcut> onRecorded = (_, shortcut) => RecordKey(shortcut.Key, shortcut.Modifiers);
        if (_hotkey != null) _hotkey.Recorded += onRecorded;
        combination.PreviewKeyDown += (_, keyEvent) =>
        {
            keyEvent.Handled = true;
            var key = keyEvent.Key == Key.System ? keyEvent.SystemKey : keyEvent.Key == Key.ImeProcessed ? keyEvent.ImeProcessedKey : keyEvent.Key;
            if (key == Key.Escape) { RecordKey(key, Keyboard.Modifiers); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) { hotkeyFeedback.Text = "辅助键已按下，再按一个主键。"; return; }
            RecordKey(key, Keyboard.Modifiers);
        };
        priority.Checked += (_, _) => CheckHotkey(); priority.Unchecked += (_, _) => CheckHotkey();
        var hotkeyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        hotkeyTimer.Tick += (_, _) => { hotkeyTimer.Stop(); CheckHotkey(); };
        combination.TextChanged += (_, _) => { hotkeyTimer.Stop(); hotkeyTimer.Start(); };
        var initialStartup = _settings.StartWithWindows;
        try { initialStartup = !App.IsTestMode && SettingsService.StartupEnabled(); }
        catch { panel.Children.Add(Hint("暂时无法读取自启状态，保存时会再次检查。")); }
        var startup = new CheckBox { Content = "开机自动启动，并留在托盘", IsChecked = initialStartup };
        panel.Children.Add(startup); Divider();

        Heading("文字识别");
        var ocrChoice = new ComboBox { Name = "OcrEngineChoice" };
        ocrChoice.Items.Add(new ComboBoxItem { Content = "中文增强 · 本地离线识别", Tag = "rapid" });
        ocrChoice.Items.Add(new ComboBoxItem { Content = "Windows 系统识别 · 备用", Tag = "windows" });
        ocrChoice.SelectedIndex = _settings.OcrEngine == "windows" ? 1 : 0;
        panel.Children.Add(ocrChoice); panel.Children.Add(Hint("中文增强引擎随软件提供，无需下载配置或 API。文字仅在本机识别；小字会自动增强，长图分段识别。")); Divider();

        Heading("截图资料存放位置");
        var storage = new TextBox { Name = "StorageDirectoryInput", Text = App.DataDirectory, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(storage);
        var storageActions = new StackPanel { Orientation = Orientation.Horizontal };
        var choose = new Button { Name = "ChooseStorageButton", Content = "选择文件夹" };
        choose.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择截图资料存放文件夹", Multiselect = false };
            if (picker.ShowDialog(dialog) == true) storage.Text = picker.FolderName;
        };
        var open = new Button { Content = "打开当前文件夹" };
        var defaultFolder = new Button { Content = "恢复默认" }; defaultFolder.Click += (_, _) => storage.Text = App.DefaultDataDirectory;
        storageActions.Children.Add(choose); storageActions.Children.Add(open); storageActions.Children.Add(defaultFolder); panel.Children.Add(storageActions);
        panel.Children.Add(Hint("截图、标注和 OCR 文字保存在 history，GIF 动图保存在 recordings。修改并保存后一起迁移，原文件夹保留。"));
        var warning = Hint("默认资料位于软件安装文件夹内。卸载软件会同步删除该文件夹，导致资料丢失。重要资料建议另选文件夹。");
        warning.Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14)); panel.Children.Add(warning); Divider();

        Heading("发送到 AI 网页");
        panel.Children.Add(Hint("无需安装扩展或配对。点击发送后，截图会先复制到剪贴板，再打开所选 AI 网页并尝试粘贴。若尚未登录或未找到输入框，登录后点击聊天输入框按 Ctrl+V 即可。内容只放入草稿，由你检查并发送。"));

        var footer = new StackPanel { Margin = new Thickness(24, 8, 24, 20) };
        var feedback = new TextBlock { Name = "SettingsFeedback", Foreground = new SolidColorBrush(Color.FromRgb(191, 89, 68)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        footer.Children.Add(feedback);
        var footerActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Name = "CloseSettingsButton", Content = "关闭" }; cancel.Click += (_, _) => dialog.Close();
        var save = new Button { Name = "SaveSettingsButton", Content = "保存设置", Style = (Style)FindResource("PrimaryButton") };
        footerActions.Children.Add(cancel); footerActions.Children.Add(save); footer.Children.Add(footerActions);
        Grid.SetRow(footer, 1); grid.Children.Add(footer);
        var saving = false;
        dialog.Closing += (_, args) => { if (saving) args.Cancel = true; };
        open.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { Path.Combine(App.DataDirectory, "history") }, UseShellExecute = true }); }
            catch (Exception ex) { feedback.Text = "文件夹未打开：" + ex.Message; }
        };
        save.Click += async (_, _) =>
        {
            if (saving) return;
            feedback.Foreground = new SolidColorBrush(Color.FromRgb(191, 89, 68));
            if (!HotkeyService.TryNormalize(combination.Text, out var normalized) || normalized.Count(character => character == '+') > 2) { feedback.Text = "请点击快捷键框，按下想使用的组合，最多三个按键。"; return; }
            bool oldStartup;
            try { oldStartup = !App.IsTestMode && SettingsService.StartupEnabled(); }
            catch (Exception ex) { feedback.Text = "无法读取自启状态，设置未改变：" + ex.Message; return; }
            var oldHotkey = _hotkey?.CurrentCombination;
            var oldPriority = _hotkey?.UsesPriorityCapture ?? _settings.PreferScreenshotHotkey;
            if (_hotkey != null && !_hotkey.Register(normalized, out var error, priority.IsChecked == true)) { feedback.Text = error; return; }
            saving = true; panel.IsEnabled = false; footerActions.IsEnabled = false;
            feedback.Text = "正在保存设置并检查资料位置…";
            try
            {
                var candidate = CopySettings(); candidate.Hotkey = normalized; candidate.StartWithWindows = startup.IsChecked == true;
                candidate.PreferScreenshotHotkey = priority.IsChecked == true; candidate.HotkeyPreferenceVersion = 1; candidate.OcrEngine = (ocrChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? "rapid";
                if (!App.IsTestMode) SettingsService.SetStartup(candidate.StartWithWindows);
                var count = await ChangeStorageLocationAsync(storage.Text, candidate);
                HotkeyHint.Text = normalized; combination.Text = normalized; storage.Text = App.DataDirectory;
                feedback.Foreground = new SolidColorBrush(Color.FromRgb(24, 117, 107));
                feedback.Text = count > 0 ? $"设置已保存，{count} 张历史截图已迁移，原资料仍保留。" : "设置已保存。";
                SetStatus(feedback.Text);
            }
            catch (Exception ex)
            {
                feedback.Text = "设置未保存，继续使用原资料位置：" + ex.Message;
                if (oldHotkey != null && _hotkey != null && !_hotkey.Register(oldHotkey, out _, oldPriority))
                    feedback.Text += " 原快捷键暂无法恢复，当前使用 " + _hotkey.CurrentCombination + "。";
                HotkeyHint.Text = _hotkey?.CurrentCombination ?? _settings.Hotkey;
                if (!App.IsTestMode) { try { SettingsService.SetStartup(oldStartup); } catch (Exception rollback) { feedback.Text += " 自启设置未恢复：" + rollback.Message; } }
            }
            finally { saving = false; panel.IsEnabled = true; footerActions.IsEnabled = true; }
        };
        dialog.Content = grid; CheckHotkey();
        try { if (_hotkey != null) _hotkey.Suspended = true; dialog.ShowDialog(); }
        finally { if (_hotkey != null) { _hotkey.EndRecording(); _hotkey.Recorded -= onRecorded; _hotkey.Suspended = false; } hotkeyTimer.Stop(); _settingsWindow = null; }
    }
}
