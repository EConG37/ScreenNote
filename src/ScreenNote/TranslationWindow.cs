using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenNote;

public sealed class TranslationWindow : Window
{
    private readonly ITranslationService _translator;
    private readonly Func<CancellationToken, Task<string>>? _readSource;
    private readonly TextBox _source = TextArea("TranslationSourceInput");
    private readonly TextBox _result = TextArea("TranslationResultOutput");
    private readonly TextBlock _status = new() { Name = "TranslationStatus", Text = "中英互译 · 在本机完成", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(98, 105, 96)) };
    private readonly ComboBox _direction = new() { Name = "TranslationDirectionChoice", Width = 190 };
    private readonly Button _translate = new() { Name = "TranslateTextButton", Content = "翻译", MinWidth = 86 };
    private readonly Button _copy = new() { Name = "CopyTranslationButton", Content = "复制译文", IsEnabled = false };
    private readonly Button _stop = new() { Name = "StopTranslationButton", Content = "停止", Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _operation;
    private bool _closed, _settingSource, _reading, _copying;

    public TranslationWindow(string sourceText = "", Func<CancellationToken, Task<string>>? readSource = null, ITranslationService? translator = null)
    {
        _translator = translator ?? new TranslationService(); _readSource = readSource;
        Title = "拾屏 · 翻译"; Icon = AppIcon.WindowImage;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12;
        Width = 940; Height = 620; MinWidth = 640; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        Width = Math.Min(Width, area.Width / dpi.DpiScaleX - 24); Height = Math.Min(Height, area.Height / dpi.DpiScaleY - 24);
        MinWidth = Math.Min(MinWidth, Width); MinHeight = Math.Min(MinHeight, Height);
        Background = new SolidColorBrush(Color.FromRgb(247, 248, 244));
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 22) };
        _direction.Items.Add(new ComboBoxItem { Content = "自动 · 中文 ⇄ 英文", Tag = "auto" });
        _direction.Items.Add(new ComboBoxItem { Content = "英文 → 简体中文", Tag = "en_zh" });
        _direction.Items.Add(new ComboBoxItem { Content = "中文 → 英文", Tag = "zh_en" });
        _direction.SelectedIndex = 0; DockPanel.SetDock(_direction, Dock.Right); heading.Children.Add(_direction);
        var title = new StackPanel(); title.Children.Add(new TextBlock { Text = "翻译", FontSize = 24, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = "原文在左，译文在右。", Foreground = _status.Foreground, Margin = new Thickness(0, 5, 0, 0) }); heading.Children.Add(title); root.Children.Add(heading);
        var panes = new Grid(); panes.ColumnDefinitions.Add(new ColumnDefinition()); panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); panes.ColumnDefinitions.Add(new ColumnDefinition());
        panes.Children.Add(Pane("原文", "可先校对，再重新翻译", _source));
        var translatedPane = Pane("译文", "选择文字或复制全文", _result); Grid.SetColumn(translatedPane, 2); panes.Children.Add(translatedPane);
        Grid.SetRow(panes, 1); root.Children.Add(panes);
        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var close = new Button { Name = "CloseTranslationButton", Content = "关闭" }; close.Click += (_, _) => Close();
        _translate.Style = (Style)FindResource("PrimaryButton");
        actions.Children.Add(_stop); actions.Children.Add(close); actions.Children.Add(_copy); actions.Children.Add(_translate);
        DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions); _status.VerticalAlignment = VerticalAlignment.Center; _status.Margin = new Thickness(0, 0, 16, 0); footer.Children.Add(_status);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _source.Text = sourceText; _result.IsReadOnly = true;
        _source.TextChanged += (_, _) => { if (!_settingSource) MarkChanged(); };
        _direction.SelectionChanged += (_, _) => { if (!_reading) MarkChanged(); };
        _translate.Click += async (_, _) => await TranslateAsync();
        _stop.Click += (_, _) => { _operation?.Cancel(); SetBusy(false); _status.Text = "已停止，原文与已有译文保留。"; };
        _copy.Click += async (_, _) => await CopyTranslationAsync();
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { _closed = true; _operation?.Cancel(); };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { await TranslateAsync(); e.Handled = true; }
        };
    }
    private static TextBox TextArea(string name) => new()
    {
        Name = name, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 15, Padding = new Thickness(16), BorderThickness = new Thickness(0), Background = Brushes.White, IsUndoEnabled = true
    };
    private static Border Pane(string title, string hint, TextBox text)
    {
        var content = new Grid(); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition());
        var header = new StackPanel { Margin = new Thickness(16, 16, 16, 4) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(115, 120, 110)), Margin = new Thickness(0, 5, 0, 0) });
        content.Children.Add(header); Grid.SetRow(text, 1); text.Margin = new Thickness(2, 0, 2, 6); content.Children.Add(text);
        return new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(225, 228, 221)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = content };
    }
    private void MarkChanged()
    {
        _operation?.Cancel(); SetBusy(false); _copy.IsEnabled = false;
        _status.Text = "原文或方向已修改，点击翻译更新结果。";
    }
    private void SetBusy(bool busy, bool reading = false)
    {
        _reading = busy && reading; _source.IsReadOnly = _reading;
        _translate.IsEnabled = !busy; _stop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task CopyTranslationAsync()
    {
        if (_closed || _copying || !_copy.IsEnabled) return;
        _copying = true; var text = _result.Text;
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (_closed || !_copy.IsEnabled || _result.Text != text) return;
                if (TextClipboard.TrySetText(new System.Windows.Interop.WindowInteropHelper(this).Handle, text)) { _status.Text = "译文已复制。"; return; }
                if (attempt < 3) await Task.Delay(120);
            }
            if (!_closed) _status.Text = "剪贴板暂时不可用，请稍后重试。";
        }
        finally { _copying = false; }
    }
    private async Task InitializeAsync()
    {
        if (_readSource == null || !string.IsNullOrWhiteSpace(_source.Text)) { if (_source.Text.Trim().Length > 0) await TranslateAsync(); return; }
        var operation = new CancellationTokenSource(); _operation = operation; SetBusy(true, true); _status.Text = "正在识别截图文字…";
        try
        {
            var text = await _readSource(operation.Token);
            if (_closed || operation.IsCancellationRequested || !ReferenceEquals(_operation, operation)) return;
            _settingSource = true; _source.Text = text; _settingSource = false;
            SetBusy(false);
            if (text.Trim().Length == 0) { _status.Text = "没有识别到文字，可以输入原文后翻译。"; return; }
            await TranslateAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !operation.IsCancellationRequested) { SetBusy(false); _status.Text = "识别未完成：" + ex.Message; } }
        finally { if (ReferenceEquals(_operation, operation)) { _operation = null; if (!_closed) SetBusy(false); } operation.Dispose(); }
    }
    private async Task TranslateAsync()
    {
        if (_closed || _reading) return;
        _operation?.Cancel();
        var operation = new CancellationTokenSource(); _operation = operation;
        var source = _source.Text; var direction = (_direction.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        SetBusy(true); _copy.IsEnabled = false; _status.Text = "正在本地翻译…";
        try
        {
            var result = await _translator.TranslateAsync(source, direction, operation.Token);
            if (_closed || operation.IsCancellationRequested || !ReferenceEquals(_operation, operation)) return;
            if (result.Error != null) { _status.Text = result.Error; return; }
            _result.Text = result.Text; _copy.IsEnabled = result.Text.Length > 0;
            _status.Text = result.Direction == "en_zh" ? "英文 → 简体中文 · 翻译完成" : "中文 → 英文 · 翻译完成";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !operation.IsCancellationRequested) _status.Text = "翻译未完成：" + ex.Message; }
        finally { if (ReferenceEquals(_operation, operation)) { _operation = null; if (!_closed) SetBusy(false); } operation.Dispose(); }
    }
}
