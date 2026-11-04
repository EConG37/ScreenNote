using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenNote;

internal sealed class CollageFrame(BitmapSource image, string label) : System.ComponentModel.INotifyPropertyChanged
{
    public BitmapSource Image { get; } = image;
    public string Label { get; } = label;
    private int _order;
    public string DisplayLabel => $"{_order:D2} · {Label}";
    internal int Order { set { if (_order == value) return; _order = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayLabel))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal SubtitleCrop? Crop { get; set; }
}

internal sealed class CollageWindow : Window
{
    private readonly ObservableCollection<CollageFrame> _frames = [];
    private readonly ListBox _list = new() { Margin = new Thickness(0, 0, 14, 0), BorderThickness = new Thickness(0), Background = Brushes.White };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) };
    private readonly ScrollViewer _previewHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = new SolidColorBrush(Color.FromRgb(229, 231, 225)) };
    private readonly ComboBox _layout = new() { Width = 124, Margin = new Thickness(0, 0, 16, 0) };
    private readonly Slider _subtitle = new() { Minimum = 5, Maximum = 60, Value = 20, Width = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _stagger = new() { Minimum = 0, Maximum = 80, Value = 16, Width = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _width = new() { Width = 90 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 10, 0, 8) };
    private readonly TextBlock _subtitleLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _staggerLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly Func<Task<BitmapSource?>> _capture;
    private readonly Button _copy, _save, _edit, _captureButton;
    private BitmapSource? _composed;
    private bool _adding, _closed, _syncCrop;
    private string? _notice;
    private readonly SubtitleCropControl _cropEditor = new();
    private readonly TextBlock _cropHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6), Foreground = Brushes.DimGray };
    private readonly Slider _cropTop = new() { Minimum = 0, Maximum = 97 };
    private readonly Slider _cropBottom = new() { Minimum = 3, Maximum = 100 };
    private readonly ComboBox _previewZoom = new() { Width = 104 };
    private Point _dragStart;
    private CollageFrame? _dragFrame;
    private bool _repeatCapture;

    internal BitmapSource? ResultImage { get; private set; }
    internal event EventHandler? EditRequested;

    internal CollageWindow(System.Collections.Generic.IEnumerable<CollageFrame> images, Func<Task<BitmapSource?>> capture)
    {
        _capture = capture; foreach (var frame in images.Take(20)) _frames.Add(frame);
        Title = "拾屏 · 多图与台词拼图"; Icon = AppIcon.WindowImage;
        Width = 1280; Height = 900; MinWidth = 900; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var add = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _captureButton = Button("继续截图", async () => await AddCaptureAsync());
        add.Children.Add(_captureButton); add.Children.Add(Button("添加图片", AddFiles));
        var repeat = new CheckBox { Content = "连续框选", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), ToolTip = "每次框选后继续下一张，按 Esc 返回拼图" };
        repeat.Checked += (_, _) => _repeatCapture = true; repeat.Unchecked += (_, _) => _repeatCapture = false; add.Children.Add(repeat);
        DockPanel.SetDock(add, Dock.Right); top.Children.Add(add);
        top.Children.Add(new TextBlock { Text = "多图与台词拼图", FontSize = 24, FontWeight = FontWeights.SemiBold });
        root.Children.Add(top);
        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var label in new[] { "台词拼图", "竖向拼接", "横向拼接", "两列拼贴" }) _layout.Items.Add(label);
        _layout.SelectedIndex = 0; options.Children.Add(_layout);
        options.Children.Add(_subtitleLabel); options.Children.Add(_subtitle);
        options.Children.Add(Button("应用到全部", () => { foreach (var frame in _frames) frame.Crop = null; SyncCrop(); ScheduleRender(); }));
        options.Children.Add(_staggerLabel); options.Children.Add(_stagger);
        options.Children.Add(new TextBlock { Text = "输出宽度", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
        foreach (var value in new[] { 640, 960, 1280, 1600 }) _width.Items.Add(value);
        _width.SelectedItem = 960; options.Children.Add(_width); Grid.SetRow(options, 1); root.Children.Add(options);
        var workspace = new Grid(); workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) }); workspace.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel();
        var order = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        order.Children.Add(Button("上移", () => Move(-1))); order.Children.Add(Button("下移", () => Move(1)));
        order.Children.Add(Button("移除", () => { if (_list.SelectedItem is CollageFrame frame) _frames.Remove(frame); }));
        DockPanel.SetDock(order, Dock.Bottom); left.Children.Add(order);
        var cropPanel = new StackPanel { Margin = new Thickness(0, 10, 14, 0) };
        cropPanel.Children.Add(new TextBlock { Text = "当前图片 · 台词范围", FontWeight = FontWeights.SemiBold });
        cropPanel.Children.Add(_cropHint); cropPanel.Children.Add(_cropEditor);
        cropPanel.Children.Add(new TextBlock { Text = "上边界", Margin = new Thickness(0, 6, 0, 0) }); cropPanel.Children.Add(_cropTop);
        cropPanel.Children.Add(new TextBlock { Text = "下边界" }); cropPanel.Children.Add(_cropBottom);
        DockPanel.SetDock(cropPanel, Dock.Bottom); left.Children.Add(cropPanel);
        _list.SelectionChanged += (_, _) => SyncCrop();
        _cropEditor.CropChanged += crop => UpdateCrop(crop);
        _cropTop.ValueChanged += (_, _) => { if (!_syncCrop) UpdateCrop(new SubtitleCrop(Math.Min(_cropTop.Value / 100, _cropBottom.Value / 100 - 0.03), _cropBottom.Value / 100)); };
        _cropBottom.ValueChanged += (_, _) => { if (!_syncCrop) UpdateCrop(new SubtitleCrop(_cropTop.Value / 100, Math.Max(_cropBottom.Value / 100, _cropTop.Value / 100 + 0.03))); };
        _list.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(_list); _dragFrame = FrameAt(e.OriginalSource as DependencyObject); };
        _list.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && _dragFrame != null && (e.GetPosition(_list) - _dragStart).Length > 8) { var frame = _dragFrame; _dragFrame = null; DragDrop.DoDragDrop(_list, new DataObject(typeof(CollageFrame), frame), DragDropEffects.Move); } };
        _list.AllowDrop = true;
        _list.Drop += (_, e) => { if (e.Data.GetData(typeof(CollageFrame)) is CollageFrame frame && FrameAt(e.OriginalSource as DependencyObject) is CollageFrame target) { var from = _frames.IndexOf(frame); var to = _frames.IndexOf(target); if (from >= 0 && to >= 0) { _frames.Move(from, to); _list.SelectedItem = frame; SyncCrop(); } e.Handled = true; } };
        _list.ItemsSource = _frames;
        var item = new FrameworkElementFactory(typeof(StackPanel)); item.SetValue(StackPanel.MarginProperty, new Thickness(4, 6, 4, 8));
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new Binding("Image")); image.SetValue(FrameworkElement.HeightProperty, 88d); image.SetValue(Image.StretchProperty, Stretch.Uniform);
        item.AppendChild(image);
        var labelBlock = new FrameworkElementFactory(typeof(TextBlock)); labelBlock.SetBinding(TextBlock.TextProperty, new Binding("DisplayLabel")); labelBlock.SetValue(TextBlock.MarginProperty, new Thickness(0, 6, 0, 0)); labelBlock.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); item.AppendChild(labelBlock);
        _list.ItemTemplate = new DataTemplate { VisualTree = item }; left.Children.Add(_list); workspace.Children.Add(left);
        _previewHost.Content = _preview; _previewHost.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        var resultPane = new DockPanel();
        var previewTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        previewTools.Children.Add(new TextBlock { Text = "拼接预览", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        foreach (var title in new[] { "适应宽度", "50%", "100%", "200%" }) _previewZoom.Items.Add(title);
        _previewZoom.SelectedIndex = 0; _previewZoom.SelectionChanged += (_, _) => ResizePreview(); previewTools.Children.Add(_previewZoom);
        previewTools.Children.Add(Button("放大查看", () => { _renderTimer.Stop(); Render(); if (_composed != null) new ImageViewerWindow(_composed) { Owner = this }.ShowDialog(); }));
        DockPanel.SetDock(previewTools, Dock.Top); resultPane.Children.Add(previewTools); resultPane.Children.Add(_previewHost);
        Grid.SetColumn(resultPane, 1); workspace.Children.Add(resultPane);
        Grid.SetRow(workspace, 2); root.Children.Add(workspace);
        var footer = new StackPanel(); footer.Children.Add(_status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Button("关闭", Close));
        _save = Button("保存拼图", Save); _copy = Button("复制拼图", Copy);
        _edit = Button("完成并编辑", () => { _renderTimer.Stop(); Render(); if (_composed == null) return; ResultImage = _composed; Close(); EditRequested?.Invoke(this, EventArgs.Empty); });
        if (TryFindResource("PrimaryButton") is Style style) _copy.Style = style;
        actions.Children.Add(_save); actions.Children.Add(_edit); actions.Children.Add(_copy); footer.Children.Add(actions);
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = new Border { Background = Brushes.White, Child = root };
        _layout.SelectionChanged += (_, _) => ScheduleRender(); _width.SelectionChanged += (_, _) => ScheduleRender();
        _subtitle.ValueChanged += (_, _) => { SyncCrop(); ScheduleRender(); }; _stagger.ValueChanged += (_, _) => ScheduleRender();
        _frames.CollectionChanged += (_, _) => { NumberFrames(); SyncCrop(); ScheduleRender(); };
        NumberFrames();
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); Render(); };
        _previewHost.SizeChanged += (_, _) => ResizePreview();
        Loaded += (_, _) =>
        {
            var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            MinWidth = Math.Min(MinWidth, area.Width / dpi.DpiScaleX - 36); MinHeight = Math.Min(MinHeight, area.Height / dpi.DpiScaleY - 36);
            Width = Math.Min(Width, area.Width / dpi.DpiScaleX - 36); Height = Math.Min(Height, area.Height / dpi.DpiScaleY - 36);
            if (_frames.Count > 0) _list.SelectedIndex = 0; SyncCrop(); Render();
        };
        Closed += (_, _) => { _closed = true; _renderTimer.Stop(); };
    }

    private static Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 7, 0), Padding = new Thickness(10, 7, 10, 7) };
        button.Click += (_, _) => action(); return button;
    }
    internal async Task AddCaptureAsync()
    {
        if (_adding || _frames.Count >= 20) return;
        _notice = null;
        _adding = true; _captureButton.IsEnabled = false; Hide();
        try
        {
            do
            {
                var image = await _capture();
                if (_closed || image == null) break;
                var frame = new CollageFrame(image, "截图 " + DateTime.Now.ToString("HH:mm:ss"));
                _frames.Add(frame); _list.SelectedItem = frame;
            } while (_repeatCapture && _frames.Count < 20);
        }
        catch (Exception ex) { _notice = "截图未加入：" + ex.Message; }
        finally { _adding = false; _captureButton.IsEnabled = true; if (!_closed) { Show(); Activate(); Render(); } }
    }
    private void AddFiles()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        _notice = null;
        foreach (var path in picker.FileNames)
        {
            if (_frames.Count >= 20) { _notice = "每次最多 20 张，请先移除部分图片。"; break; }
            try
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
                var frame = new CollageFrame(bitmap, Path.GetFileName(path));
                _frames.Add(frame); _list.SelectedItem = frame; _list.ScrollIntoView(frame);
            }
            catch (Exception ex) { _notice = "无法打开 " + Path.GetFileName(path) + "：" + ex.Message; }
        }
        ScheduleRender();
    }
    private void Move(int direction)
    {
        var index = _list.SelectedIndex; var next = index + direction;
        if (index < 0 || next < 0 || next >= _frames.Count) return;
        _frames.Move(index, next); _list.SelectedIndex = next;
    }
    private void ScheduleRender() { _renderTimer.Stop(); _renderTimer.Start(); }
    private void NumberFrames() { for (var i = 0; i < _frames.Count; i++) _frames[i].Order = i + 1; }
    private void Render()
    {
        _subtitleLabel.Text = $"默认台词 {_subtitle.Value:F0}% "; _staggerLabel.Text = $"错位 {_stagger.Value:F0}px ";
        var subtitles = _layout.SelectedIndex == 0; _subtitle.IsEnabled = _stagger.IsEnabled = subtitles;
        _composed = null;
        try
        {
            if (_frames.Count > 0) _composed = ImageCollage.Compose(_frames.Select(frame => frame.Image).ToArray(), (CollageLayout)Math.Max(0, _layout.SelectedIndex), (int)(_width.SelectedItem ?? 960), _subtitle.Value / 100, (int)_stagger.Value, crops: _frames.Select(frame => frame.Crop).ToArray());
            _preview.Source = _composed; ResizePreview(); SyncCrop();
            _status.Text = _frames.Count == 0 ? "勾选截图历史后进入，或点「继续截图」「添加图片」。第一张保留完整画面，后面的图片只露出底部台词。"
                : $"{_frames.Count} 张 · {_composed!.PixelWidth} × {_composed.PixelHeight} 像素。" + (subtitles ? "选中左侧图片可逐张裁切台词；拖动缩略图排序，第一张始终完整显示。" : "可调整图片顺序，复制或保存后分享。");
        }
        catch (Exception ex) { _preview.Source = null; _status.Text = ex.Message; }
        if (_notice != null) _status.Text = _notice + "\n" + _status.Text;
        _captureButton.IsEnabled = !_adding && _frames.Count < 20;
        _copy.IsEnabled = _save.IsEnabled = _edit.IsEnabled = _composed != null;
    }
    private CollageFrame? FrameAt(DependencyObject? source)
    {
        while (source != null && source != _list) { if (source is ListBoxItem { DataContext: CollageFrame frame }) return frame; source = VisualTreeHelper.GetParent(source); }
        return null;
    }
    private void SyncCrop()
    {
        if (_syncCrop) return;
        _syncCrop = true;
        try
        {
            var frame = _list.SelectedItem as CollageFrame;
            var index = frame == null ? -1 : _frames.IndexOf(frame);
            var enabled = frame != null && index > 0 && _layout.SelectedIndex == 0;
            _cropEditor.Source = frame?.Image;
            _cropEditor.IsEnabled = _cropTop.IsEnabled = _cropBottom.IsEnabled = enabled;
            var crop = frame?.Crop ?? new SubtitleCrop(1 - _subtitle.Value / 100, 1);
            _cropEditor.Crop = crop; _cropTop.Value = crop.Top * 100; _cropBottom.Value = crop.Bottom * 100;
            _cropHint.Text = frame == null ? "选择一张图片，调整要保留的台词。" : index == 0 ? "第 1 张保留完整画面。拖动缩略图可更换封面。" : _layout.SelectedIndex != 0 ? "切换到台词拼图后，可逐张调整裁切范围。" : $"第 {index + 1} 张 · 保留 {crop.Top:P0}–{crop.Bottom:P0}，拖动绿色边界调整。";
        }
        finally { _syncCrop = false; }
    }
    private void UpdateCrop(SubtitleCrop crop)
    {
        if (_list.SelectedItem is not CollageFrame frame || _list.SelectedIndex <= 0) return;
        frame.Crop = crop; SyncCrop(); ScheduleRender();
    }
    private void ResizePreview()
    {
        if (_composed == null) return;
        _preview.Width = _previewZoom.SelectedIndex switch { 1 => _composed.PixelWidth * 0.5, 2 => _composed.PixelWidth, 3 => _composed.PixelWidth * 2, _ => Math.Max(100, _previewHost.ActualWidth - 42) };
    }
    private void Copy()
    {
        _renderTimer.Stop(); Render();
        try { if (_composed != null) { Clipboard.SetImage(_composed); _status.Text = "拼图已复制，可直接粘贴分享。"; } }
        catch (Exception ex) { _status.Text = "复制未完成：" + ex.Message; }
    }
    private void Save()
    {
        _renderTimer.Stop(); Render(); if (_composed == null) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "台词拼图-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png" };
        try { if (picker.ShowDialog(this) == true) { MainWindow.SavePng(_composed, picker.FileName); _status.Text = "拼图已保存。"; } }
        catch (Exception ex) { _status.Text = "保存未完成：" + ex.Message; }
    }
}
