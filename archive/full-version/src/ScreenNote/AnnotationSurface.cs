using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenNote;

public enum AnnotationTool
{
    Select, Pen, Arrow, Line, Rectangle, Ellipse, Highlighter, Number, Text, Mosaic, Cover, HighlightBox
}

/// <summary>An editable, image-pixel-coordinate annotation canvas. Selection adorners never enter exports.</summary>
public sealed class AnnotationSurface : FrameworkElement
{
    private readonly List<Mark> _marks = [];
    private readonly Stack<Snapshot> _undo = [];
    private readonly Stack<Snapshot> _redo = [];
    private BitmapSource? _source;
    private DrawingGroup? _drawing;
    private Mark? _draft;
    private Mark? _selected;
    private Mark? _moveOriginal;
    private Snapshot? _gestureBefore;
    private Point _dragOrigin;
    private bool _moving;
    private Color _inkColor = Color.FromRgb(238, 73, 83);
    private double _strokeWidth = 3;
    private double _textSize = 22;
    private AnnotationTool _tool = AnnotationTool.Arrow;

    public AnnotationSurface()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
    }

    public event EventHandler? Changed;

    public BitmapSource? Source
    {
        get => _source;
        set
        {
            CancelGesture();
            _source = value;
            _marks.Clear();
            _undo.Clear();
            _redo.Clear();
            _selected = null;
            Width = value?.PixelWidth ?? 1;
            Height = value?.PixelHeight ?? 1;
            NotifyChange(true);
        }
    }

    public AnnotationTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            CancelGesture();
            _tool = value;
            Cursor = value == AnnotationTool.Select ? Cursors.Arrow : Cursors.Cross;
            if (value != AnnotationTool.Select) SetSelected(null);
        }
    }

    public Color InkColor
    {
        get => _inkColor;
        set
        {
            _inkColor = value;
            if (_selected is null || _selected.Color == value) return;
            var before = CaptureSnapshot();
            _selected.Color = value;
            RecordChange(before);
        }
    }

    public double StrokeWidth
    {
        get => _strokeWidth;
        set
        {
            if (!double.IsFinite(value)) return;
            _strokeWidth = Math.Clamp(value, 1, 100);
            if (_selected is null || _selected.Width == _strokeWidth) return;
            var before = CaptureSnapshot();
            _selected.Width = _strokeWidth;
            RecordChange(before);
        }
    }

    public double TextSize
    {
        get => _textSize;
        set
        {
            if (!double.IsFinite(value)) return;
            _textSize = Math.Clamp(value, 8, 150);
            if (_selected is null || _selected.Tool is not (AnnotationTool.Text or AnnotationTool.Number)
                || _selected.TextSize == _textSize) return;
            var before = CaptureSnapshot();
            _selected.TextSize = _textSize;
            RecordChange(before);
        }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool HasAnnotations => _marks.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasSelection => _selected is not null;
    public AnnotationTool? SelectedTool => _selected?.Tool;
    public int DocumentRevision { get; private set; }

    public void ClearSelection() => SetSelected(null);

    public void Undo()
    {
        CancelGesture();
        if (_undo.Count == 0) return;
        _redo.Push(CaptureSnapshot());
        RestoreSnapshot(_undo.Pop());
        NotifyChange(true);
    }

    public void Redo()
    {
        CancelGesture();
        if (_redo.Count == 0) return;
        _undo.Push(CaptureSnapshot());
        RestoreSnapshot(_redo.Pop());
        NotifyChange(true);
    }

    public void DeleteSelected()
    {
        CancelGesture();
        if (_selected is null) return;
        var before = CaptureSnapshot();
        _marks.Remove(_selected);
        _selected = null;
        RecordChange(before);
    }

    public BitmapSource Export()
    {
        if (_source is null) throw new InvalidOperationException("请先截取或打开图片。");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawDrawing(GetDocumentDrawing());
        var bitmap = new RenderTargetBitmap(_source.PixelWidth, _source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Stores the editable vector layers, independently of the immutable source image.</summary>
    public string ExportDocument()
    {
        if (_source is null) throw new InvalidOperationException("请先截取或打开图片。");
        return JsonSerializer.Serialize(new SavedDocument
        {
            Version = 1,
            Width = _source.PixelWidth,
            Height = _source.PixelHeight,
            Color = _inkColor.ToString(CultureInfo.InvariantCulture),
            StrokeWidth = _strokeWidth,
            TextSize = _textSize,
            Tool = _tool,
            Marks = _marks.Select(mark => new SavedMark
            {
                Id = mark.Id,
                Tool = mark.Tool,
                Start = new SavedPoint(mark.Start.X, mark.Start.Y),
                End = new SavedPoint(mark.End.X, mark.End.Y),
                Points = mark.Points.Select(point => new SavedPoint(point.X, point.Y)).ToList(),
                Color = mark.Color.ToString(CultureInfo.InvariantCulture),
                Width = mark.Width,
                TextSize = mark.TextSize,
                TextWidth = mark.TextWidth,
                Text = mark.Text,
                Number = mark.Number
            }).ToList()
        });
    }

    /// <summary>Restores selectable/editable layers; a malformed document leaves the current canvas intact.</summary>
    public void RestoreDocument(BitmapSource source, string documentJson)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(documentJson)) { Source = source; return; }
        if (documentJson.Length > 16_000_000) throw new FormatException("历史标注文档过大。");
        var saved = JsonSerializer.Deserialize<SavedDocument>(documentJson)
            ?? throw new FormatException("历史标注文档为空。");
        if (saved.Version != 1 || saved.Width != source.PixelWidth || saved.Height != source.PixelHeight
            || saved.Marks is null || saved.Marks.Count > 10_000 || !Enum.IsDefined(saved.Tool))
            throw new FormatException("历史标注文档格式不正确。");
        var inkColor = ParseSavedColor(saved.Color);
        RequireRange(saved.StrokeWidth, 1, 100);
        RequireRange(saved.TextSize, 8, 150);
        var marks = new List<Mark>(saved.Marks.Count);
        var ids = new HashSet<Guid>();
        var totalPoints = 0;
        foreach (var mark in saved.Marks)
        {
            if (mark is null || mark.Id == Guid.Empty || !ids.Add(mark.Id) || !Enum.IsDefined(mark.Tool)
                || mark.Tool == AnnotationTool.Select || mark.Points is null || mark.Text is null
                || mark.Text.Length > 1_000_000 || mark.Number < 0)
                throw new FormatException("历史标注内容不正确。");
            totalPoints = checked(totalPoints + mark.Points.Count);
            if (totalPoints > 500_000) throw new FormatException("历史标注笔画过多。");
            RequireRange(mark.Width, 1, 100);
            RequireRange(mark.TextSize, 8, 150);
            RequireRange(mark.TextWidth, 1, 1_000_000);
            if (mark.Tool is AnnotationTool.Pen or AnnotationTool.Highlighter && mark.Points.Count == 0)
                throw new FormatException("历史标注缺少笔画坐标。");
            marks.Add(new Mark
            {
                Id = mark.Id, Tool = mark.Tool, Start = RestorePoint(mark.Start), End = RestorePoint(mark.End),
                Points = mark.Points.Select(RestorePoint).ToList(), Color = ParseSavedColor(mark.Color),
                Width = mark.Width, TextSize = mark.TextSize, TextWidth = mark.TextWidth,
                Text = mark.Text, Number = mark.Number
            });
        }
        Source = source;
        _marks.AddRange(marks);
        _inkColor = inkColor;
        _strokeWidth = saved.StrokeWidth;
        _textSize = saved.TextSize;
        Tool = saved.Tool;
        NotifyChange(true);
    }

    private static Point RestorePoint(SavedPoint point)
    {
        RequireRange(point.X, -1_000_000, 1_000_000);
        RequireRange(point.Y, -1_000_000, 1_000_000);
        return new Point(point.X, point.Y);
    }

    private static void RequireRange(double value, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new FormatException("历史标注尺寸或坐标不正确。");
    }

    private static Color ParseSavedColor(string? value)
    {
        if (value is null || value.Length != 9 || value[0] != '#'
            || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            throw new FormatException("历史标注颜色不正确。");
        return Color.FromArgb((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var bounds = new Rect(0, 0, _source?.PixelWidth ?? ActualWidth, _source?.PixelHeight ?? ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (_source is null) return;
        dc.PushClip(new RectangleGeometry(bounds));
        dc.DrawDrawing(GetDocumentDrawing());
        if (_selected is not null && _draft is null)
        {
            var box = BoundsOf(_selected);
            box.Inflate(5, 5);
            var border = new Pen(new SolidColorBrush(Color.FromRgb(65, 130, 246)), 1)
            {
                DashStyle = new DashStyle([4, 3], 0)
            };
            dc.DrawRectangle(null, border, box);
        }
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_source is null) return;
        Focus();
        var point = ClampPoint(e.GetPosition(this));
        var hit = HitMark(point);
        if (e.ClickCount == 2 && hit?.Tool == AnnotationTool.Text && Tool is AnnotationTool.Select or AnnotationTool.Text)
        {
            SetSelected(hit);
            EditText(hit);
            e.Handled = true;
            return;
        }
        if (Tool == AnnotationTool.Select)
        {
            SetSelected(hit);
            if (hit is not null)
            {
                _gestureBefore = CaptureSnapshot();
                _moveOriginal = hit.Clone();
                _dragOrigin = point;
                _moving = true;
                CaptureMouse();
                Cursor = Cursors.SizeAll;
            }
            e.Handled = true;
            return;
        }
        if (Tool == AnnotationTool.Text)
        {
            var text = AskForText("");
            if (!string.IsNullOrWhiteSpace(text))
            {
                var before = CaptureSnapshot();
                var mark = NewMark(point);
                mark.Text = text;
                mark.TextWidth = Math.Max(1, Math.Min(Width, Math.Max(64, Width - point.X - 4)));
                KeepInside(mark);
                _marks.Add(mark);
                SetSelected(mark, false);
                RecordChange(before);
            }
            e.Handled = true;
            return;
        }
        if (Tool == AnnotationTool.Number)
        {
            var before = CaptureSnapshot();
            var mark = NewMark(point);
            mark.Number = _marks.Where(x => x.Tool == AnnotationTool.Number).Select(x => x.Number).DefaultIfEmpty(0).Max() + 1;
            KeepInside(mark);
            _marks.Add(mark);
            SetSelected(mark, false);
            RecordChange(before);
            e.Handled = true;
            return;
        }
        _gestureBefore = CaptureSnapshot();
        _draft = NewMark(point);
        SetSelected(null, false);
        CaptureMouse();
        InvalidateDocument();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || _source is null) return;
        var point = ClampPoint(e.GetPosition(this));
        if (_draft is not null)
        {
            _draft.End = point;
            if (_draft.Tool is AnnotationTool.Pen or AnnotationTool.Highlighter)
            {
                if ((point - _draft.Points[^1]).Length >= .5) _draft.Points.Add(point);
            }
            InvalidateDocument();
            e.Handled = true;
        }
        else if (_moving && _selected is not null && _moveOriginal is not null)
        {
            var delta = LimitDelta(_moveOriginal, point - _dragOrigin);
            _selected.CopyPositionFrom(_moveOriginal, delta);
            InvalidateDocument();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        FinishGesture();
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        FinishGesture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (_gestureBefore is not null) CancelGesture();
            else ClearSelection();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Z)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) Redo();
            else Undo();
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
    }

    private Mark NewMark(Point point) => new()
    {
        Tool = Tool,
        Start = point,
        End = point,
        Points = [point],
        Color = _inkColor,
        Width = _strokeWidth,
        TextSize = _textSize
    };

    private Point ClampPoint(Point point) => new(Math.Clamp(point.X, 0, Width), Math.Clamp(point.Y, 0, Height));

    private void FinishGesture()
    {
        var before = _gestureBefore;
        if (before is null) return;
        var changed = false;
        if (_draft is not null)
        {
            if (IsUseful(_draft))
            {
                _marks.Add(_draft);
                SetSelected(_draft, false);
                changed = true;
            }
            _draft = null;
        }
        else if (_moving && _selected is not null && _moveOriginal is not null)
        {
            changed = (_selected.Start - _moveOriginal.Start).Length > .01;
        }
        _gestureBefore = null;
        _moveOriginal = null;
        _moving = false;
        Cursor = Tool == AnnotationTool.Select ? Cursors.Arrow : Cursors.Cross;
        if (changed) RecordChange(before);
        else NotifyChange();
    }

    private void CancelGesture()
    {
        var before = _gestureBefore;
        _gestureBefore = null;
        _draft = null;
        _moveOriginal = null;
        _moving = false;
        if (before is not null) RestoreSnapshot(before);
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = Tool == AnnotationTool.Select ? Cursors.Arrow : Cursors.Cross;
        InvalidateDocument();
    }

    private static bool IsUseful(Mark mark)
    {
        if (mark.Tool is AnnotationTool.Pen or AnnotationTool.Highlighter) return true;
        if (mark.Tool is AnnotationTool.Line or AnnotationTool.Arrow) return (mark.End - mark.Start).Length >= 2;
        var box = ShapeRect(mark);
        return box.Width >= 2 && box.Height >= 2;
    }

    private void SetSelected(Mark? mark, bool notify = true)
    {
        _selected = mark;
        if (mark is not null)
        {
            _inkColor = mark.Color;
            _strokeWidth = mark.Width;
            _textSize = mark.TextSize;
        }
        InvalidateVisual();
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    private Snapshot CaptureSnapshot() => new(_marks.Select(x => x.Clone()).ToList(), _selected?.Id);

    private void RestoreSnapshot(Snapshot snapshot)
    {
        _marks.Clear();
        _marks.AddRange(snapshot.Marks.Select(x => x.Clone()));
        SetSelected(_marks.FirstOrDefault(x => x.Id == snapshot.Selection), false);
        InvalidateDocument();
    }

    private void RecordChange(Snapshot before)
    {
        _undo.Push(before);
        _redo.Clear();
        NotifyChange(true);
    }

    private void NotifyChange(bool documentChanged = false)
    {
        if (documentChanged) DocumentRevision = unchecked(DocumentRevision + 1);
        InvalidateDocument();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateDocument()
    {
        _drawing = null;
        InvalidateVisual();
    }

    private DrawingGroup GetDocumentDrawing()
    {
        if (_drawing is not null) return _drawing;
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, Width, Height)) };
        if (_source is not null) drawing.Children.Add(new ImageDrawing(_source, new Rect(0, 0, Width, Height)));
        var marks = _marks.Concat(_draft is null ? [] : new[] { _draft }).ToArray();
        var focusBoxes = marks.Where(mark => mark.Tool == AnnotationTool.HighlightBox).ToArray();
        if (focusBoxes.Length > 0)
        {
            var clear = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var mark in focusBoxes) clear.Children.Add(new RectangleGeometry(ShapeRect(mark), 7, 7));
            var outside = Geometry.Combine(new RectangleGeometry(new Rect(0, 0, Width, Height)), clear, GeometryCombineMode.Exclude, null);
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(76, 16, 22, 32)), null, outside));
        }
        foreach (var mark in marks)
        {
            var layer = new DrawingGroup();
            using (var dc = layer.Open())
            {
                if (mark.Tool == AnnotationTool.Mosaic) DrawMosaic(dc, drawing, mark);
                else DrawMark(dc, mark);
            }
            drawing.Children.Add(layer);
        }
        _drawing = drawing;
        return drawing;
    }

    private static SolidColorBrush Ink(Mark mark)
    {
        var brush = new SolidColorBrush(mark.Color);
        brush.Freeze();
        return brush;
    }

    private static Pen Stroke(Mark mark, double? width = null)
    {
        var pen = new Pen(Ink(mark), width ?? mark.Width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static void DrawMark(DrawingContext dc, Mark mark)
    {
        var stroke = Stroke(mark);
        var box = ShapeRect(mark);
        switch (mark.Tool)
        {
            case AnnotationTool.Pen:
            case AnnotationTool.Highlighter:
                var width = mark.Tool == AnnotationTool.Highlighter ? Math.Max(12, mark.Width * 5) : mark.Width;
                if (mark.Tool == AnnotationTool.Highlighter) dc.PushOpacity(.32);
                if (mark.Points.Count == 1)
                    dc.DrawEllipse(Ink(mark), null, mark.Points[0], width / 2, width / 2);
                else
                {
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        context.BeginFigure(mark.Points[0], false, false);
                        context.PolyLineTo(mark.Points.Skip(1).ToArray(), true, false);
                    }
                    geometry.Freeze();
                    dc.DrawGeometry(null, Stroke(mark, width), geometry);
                }
                if (mark.Tool == AnnotationTool.Highlighter) dc.Pop();
                break;
            case AnnotationTool.Line:
                dc.DrawLine(stroke, mark.Start, mark.End);
                break;
            case AnnotationTool.Arrow:
                dc.DrawLine(stroke, mark.Start, mark.End);
                var vector = mark.End - mark.Start;
                if (vector.Length < .01) break;
                vector.Normalize();
                var perpendicular = new Vector(-vector.Y, vector.X);
                var length = Math.Max(12, mark.Width * 4);
                var tail = mark.End - vector * length;
                var arrow = new StreamGeometry();
                using (var context = arrow.Open())
                {
                    context.BeginFigure(mark.End, true, true);
                    context.LineTo(tail + perpendicular * length * .45, true, false);
                    context.LineTo(tail - perpendicular * length * .45, true, false);
                }
                arrow.Freeze();
                dc.DrawGeometry(Ink(mark), null, arrow);
                break;
            case AnnotationTool.Rectangle:
                dc.DrawRectangle(null, stroke, box);
                break;
            case AnnotationTool.HighlightBox:
            {
                var focusRadius = Math.Min(7, Math.Min(box.Width, box.Height) / 4);
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(65, 0, 0, 0)), 4), box, focusRadius, focusRadius);
                dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 2), box, focusRadius, focusRadius);
                var accent = new Rect(box.Left - 1.5, box.Top + box.Height * .3, 3, box.Height * .4);
                dc.DrawRoundedRectangle(Ink(mark), null, accent, 1.5, 1.5);
                break;
            }
            case AnnotationTool.Ellipse:
                dc.DrawEllipse(null, stroke, new Point(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2, box.Height / 2);
                break;
            case AnnotationTool.Cover:
                dc.DrawRectangle(Ink(mark), null, box);
                break;
            case AnnotationTool.Text:
                dc.DrawText(Formatted(mark), mark.Start);
                break;
            case AnnotationTool.Number:
                var radius = NumberRadius(mark);
                dc.DrawEllipse(Ink(mark), null, mark.Start, radius, radius);
                var number = new FormattedText(mark.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), mark.TextSize, Brushes.White, 1);
                dc.DrawText(number, new Point(mark.Start.X - number.WidthIncludingTrailingWhitespace / 2, mark.Start.Y - number.Height / 2));
                break;
        }
    }

    private void DrawMosaic(DrawingContext dc, DrawingGroup preceding, Mark mark)
    {
        var box = ShapeRect(mark);
        box.Intersect(new Rect(0, 0, Width, Height));
        if (box.IsEmpty || box.Width < 1 || box.Height < 1) return;
        var x = (int)Math.Floor(box.X);
        var y = (int)Math.Floor(box.Y);
        var width = Math.Min((int)Width - x, (int)Math.Ceiling(box.Right) - x);
        var height = Math.Min((int)Height - y, (int)Math.Ceiling(box.Bottom) - y);
        if (width <= 0 || height <= 0) return;

        // Sample the complete visible prefix, including earlier text, arrows and covers.
        // Sampling just Source would expose original content behind an earlier annotation.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-x, -y));
            context.DrawDrawing(preceding);
            context.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bitmap.CopyPixels(pixels, stride, 0);
        var blockSize = Math.Max(10, (int)Math.Round(mark.Width * 4));
        for (var top = 0; top < height; top += blockSize)
        for (var left = 0; left < width; left += blockSize)
        {
            var right = Math.Min(left + blockSize, width);
            var bottom = Math.Min(top + blockSize, height);
            long b = 0, g = 0, r = 0, a = 0;
            var count = (right - left) * (bottom - top);
            for (var row = top; row < bottom; row++)
            for (var col = left; col < right; col++)
            {
                var offset = row * stride + col * 4;
                b += pixels[offset]; g += pixels[offset + 1]; r += pixels[offset + 2]; a += pixels[offset + 3];
            }
            var blue = (byte)(b / count); var green = (byte)(g / count);
            var red = (byte)(r / count); var alpha = (byte)(a / count);
            for (var row = top; row < bottom; row++)
            for (var col = left; col < right; col++)
            {
                var offset = row * stride + col * 4;
                pixels[offset] = blue; pixels[offset + 1] = green;
                pixels[offset + 2] = red; pixels[offset + 3] = alpha;
            }
        }
        var mosaic = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        mosaic.Freeze();
        dc.PushClip(new RectangleGeometry(box));
        dc.DrawImage(mosaic, new Rect(x, y, width, height));
        dc.Pop();
    }

    private static Rect ShapeRect(Mark mark) => new(mark.Start, mark.End);

    private static FormattedText Formatted(Mark mark)
    {
        var text = new FormattedText(mark.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), mark.TextSize, Ink(mark), 1);
        text.MaxTextWidth = Math.Max(1, mark.TextWidth);
        return text;
    }

    private static double NumberRadius(Mark mark) => Math.Max(13, mark.TextSize * .8);

    private static Rect BoundsOf(Mark mark)
    {
        if (mark.Tool == AnnotationTool.Text)
        {
            var text = Formatted(mark);
            return new Rect(mark.Start, new Size(Math.Max(1, text.WidthIncludingTrailingWhitespace), text.Height));
        }
        if (mark.Tool == AnnotationTool.Number)
        {
            var radius = NumberRadius(mark);
            return new Rect(mark.Start.X - radius, mark.Start.Y - radius, radius * 2, radius * 2);
        }
        if (mark.Tool is AnnotationTool.Pen or AnnotationTool.Highlighter)
        {
            var left = mark.Points.Min(x => x.X); var top = mark.Points.Min(x => x.Y);
            var right = mark.Points.Max(x => x.X); var bottom = mark.Points.Max(x => x.Y);
            var box = new Rect(new Point(left, top), new Point(right, bottom));
            var width = mark.Tool == AnnotationTool.Highlighter ? Math.Max(12, mark.Width * 5) : mark.Width;
            box.Inflate(width / 2, width / 2);
            return box;
        }
        var bounds = ShapeRect(mark);
        if (mark.Tool is AnnotationTool.Line or AnnotationTool.Arrow or AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.HighlightBox)
        {
            var margin = mark.Tool == AnnotationTool.Arrow ? Math.Max(6, mark.Width * 2) : mark.Width / 2;
            bounds.Inflate(margin, margin);
        }
        return bounds;
    }

    private Mark? HitMark(Point point)
    {
        for (var i = _marks.Count - 1; i >= 0; i--)
        {
            var mark = _marks[i];
            if (mark.Tool is AnnotationTool.Pen or AnnotationTool.Highlighter)
            {
                var tolerance = mark.Tool == AnnotationTool.Highlighter ? Math.Max(12, mark.Width * 5) / 2 + 4 : mark.Width / 2 + 5;
                if (mark.Points.Count == 1 && (point - mark.Points[0]).Length <= tolerance) return mark;
                for (var j = 1; j < mark.Points.Count; j++)
                    if (DistanceToSegment(point, mark.Points[j - 1], mark.Points[j]) <= tolerance) return mark;
            }
            else if (mark.Tool is AnnotationTool.Arrow or AnnotationTool.Line)
            {
                if (DistanceToSegment(point, mark.Start, mark.End) <= Math.Max(6, mark.Width * 2)) return mark;
            }
            else
            {
                var box = BoundsOf(mark);
                box.Inflate(3, 3);
                if (box.Contains(point)) return mark;
            }
        }
        return null;
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        var vector = end - start;
        if (vector.LengthSquared < .0001) return (point - start).Length;
        var ratio = Math.Clamp(Vector.Multiply(point - start, vector) / vector.LengthSquared, 0, 1);
        return (point - (start + vector * ratio)).Length;
    }

    private Vector LimitDelta(Mark mark, Vector delta)
    {
        var box = BoundsOf(mark);
        var minX = -box.Left; var maxX = Width - box.Right;
        var minY = -box.Top; var maxY = Height - box.Bottom;
        // Oversized text can still be moved; drawing remains clipped to the image.
        delta.X = minX <= maxX ? Math.Clamp(delta.X, minX, maxX) : delta.X;
        delta.Y = minY <= maxY ? Math.Clamp(delta.Y, minY, maxY) : delta.Y;
        return delta;
    }

    private void KeepInside(Mark mark) => mark.CopyPositionFrom(mark.Clone(), LimitDelta(mark, new Vector(0, 0)));

    private void EditText(Mark mark)
    {
        var text = AskForText(mark.Text);
        if (text is null || text == mark.Text) return;
        var before = CaptureSnapshot();
        if (string.IsNullOrWhiteSpace(text))
        {
            _marks.Remove(mark);
            _selected = null;
        }
        else
        {
            mark.Text = text;
            KeepInside(mark);
        }
        RecordChange(before);
    }

    private string? AskForText(string initial)
    {
        var dialog = new Window
        {
            Title = initial.Length == 0 ? "添加文字" : "编辑文字",
            Width = 460,
            Height = 290,
            MinWidth = 340,
            MinHeight = 230,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            Background = Brushes.White
        };
        var owner = Window.GetWindow(this);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 76, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 12, 8, 0) };
        var accept = new Button { Content = "确定", MinWidth = 76, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var input = new TextBox
        {
            Text = initial,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            Padding = new Thickness(8)
        };
        panel.Children.Add(input);
        dialog.Content = panel;
        accept.Click += (_, _) => dialog.DialogResult = true;
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                dialog.DialogResult = true;
                e.Handled = true;
            }
        };
        dialog.Loaded += (_, _) => { input.Focus(); input.CaretIndex = input.Text.Length; };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    /// <summary>Runs on an STA thread; verifies compositing and reversible edits without mouse automation.</summary>
    internal static void VerifyRenderingAndHistory()
    {
        var pixels = new byte[32 * 32 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
        var surface = new AnnotationSurface { Source = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128) };
        var before = surface.CaptureSnapshot();
        var cover = new Mark { Tool = AnnotationTool.Cover, Start = new Point(0, 0), End = new Point(32, 32), Color = Colors.Blue };
        surface._marks.Add(cover);
        surface.SetSelected(cover, false);
        surface.RecordChange(before);
        surface._marks.Add(new Mark { Tool = AnnotationTool.Mosaic, Start = new Point(0, 0), End = new Point(32, 32), Width = 3 });
        surface.NotifyChange(true);
        var actual = new byte[4];
        surface.Export().CopyPixels(new Int32Rect(16, 16, 1, 1), actual, 4, 0);
        if (actual[0] != 255 || actual[2] != 0 || actual[3] != 255)
            throw new InvalidOperationException("马赛克未使用完整的前序标注图层。");
        surface.Undo();
        if (surface._marks.Count != 0 || !surface.CanRedo) throw new InvalidOperationException("撤销未恢复标注快照。");
        surface.Redo();
        if (surface._marks.Count != 2 || surface._selected?.Id != cover.Id) throw new InvalidOperationException("重做未恢复标注与选区。");
        surface.InkColor = Colors.Green;
        surface.Undo();
        if (surface._selected?.Color != Colors.Blue) throw new InvalidOperationException("颜色修改未进入撤销历史。");
        surface.DeleteSelected();
        surface.Undo();
        if (surface._marks.Count != 2) throw new InvalidOperationException("删除标注未进入撤销历史。");

        var revision = surface.DocumentRevision;
        surface.ClearSelection();
        surface.SetSelected(surface._marks[0]);
        if (surface.DocumentRevision != revision) throw new InvalidOperationException("选择标注改变了文档版本。");
        var beforeMove = surface.CaptureSnapshot();
        var moving = surface._marks[0];
        moving.CopyPositionFrom(moving.Clone(), new Vector(2, 3));
        surface.RecordChange(beforeMove);
        surface.Undo();
        if (surface._marks[0].Start != new Point(0, 0)) throw new InvalidOperationException("移动标注未正确撤销。");
        surface.Redo();
        if (surface._marks[0].Start != new Point(2, 3)) throw new InvalidOperationException("移动标注未正确重做。");

        var textMark = new Mark { Tool = AnnotationTool.Text, Start = new Point(2, 2), Text = "测试\n文字", TextSize = 8, TextWidth = 28, Color = Colors.White };
        surface._marks.Add(textMark);
        surface.SetSelected(textMark, false);
        var beforeTextEdit = surface.CaptureSnapshot();
        textMark.Text = "修改文字";
        surface.RecordChange(beforeTextEdit);
        surface.Undo();
        if (surface._selected?.Text != "测试\n文字") throw new InvalidOperationException("文字编辑未正确撤销。");
        surface.Redo();
        if (surface._selected?.Text != "修改文字") throw new InvalidOperationException("文字编辑未正确重做。");

        foreach (var tool in new[] { AnnotationTool.Pen, AnnotationTool.Highlighter, AnnotationTool.Arrow, AnnotationTool.Line, AnnotationTool.Rectangle, AnnotationTool.Ellipse, AnnotationTool.Number })
            surface._marks.Add(new Mark
            {
                Tool = tool, Start = new Point(5, 5), End = new Point(25, 25),
                Points = [new Point(5, 5), new Point(12, 19), new Point(25, 25)],
                Color = Colors.Yellow, Width = 2, TextSize = 8, Number = 1
            });
        surface.NotifyChange(true);
        var withSelection = new byte[pixels.Length];
        surface.Export().CopyPixels(withSelection, 128, 0);
        surface.ClearSelection();
        var withoutSelection = new byte[pixels.Length];
        surface.Export().CopyPixels(withoutSelection, 128, 0);
        if (!withSelection.SequenceEqual(withoutSelection)) throw new InvalidOperationException("导出包含了选择边框。");
    }

    private sealed record Snapshot(List<Mark> Marks, Guid? Selection);

    private sealed class SavedDocument
    {
        public int Version { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Color { get; set; } = "#FFEE4953";
        public double StrokeWidth { get; set; } = 3;
        public double TextSize { get; set; } = 22;
        public AnnotationTool Tool { get; set; } = AnnotationTool.Arrow;
        public List<SavedMark> Marks { get; set; } = [];
    }

    private readonly record struct SavedPoint(double X, double Y);

    private sealed class SavedMark
    {
        public Guid Id { get; set; }
        public AnnotationTool Tool { get; set; }
        public SavedPoint Start { get; set; }
        public SavedPoint End { get; set; }
        public List<SavedPoint> Points { get; set; } = [];
        public string Color { get; set; } = "#FF000000";
        public double Width { get; set; } = 3;
        public double TextSize { get; set; } = 22;
        public double TextWidth { get; set; } = 400;
        public string Text { get; set; } = "";
        public int Number { get; set; }
    }

    private sealed class Mark
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public AnnotationTool Tool { get; init; }
        public Point Start { get; set; }
        public Point End { get; set; }
        public List<Point> Points { get; set; } = [];
        public Color Color { get; set; }
        public double Width { get; set; } = 3;
        public double TextSize { get; set; } = 22;
        public double TextWidth { get; set; } = 400;
        public string Text { get; set; } = "";
        public int Number { get; set; }

        public Mark Clone() => new()
        {
            Id = Id, Tool = Tool, Start = Start, End = End, Points = new List<Point>(Points),
            Color = Color, Width = Width, TextSize = TextSize, TextWidth = TextWidth, Text = Text, Number = Number
        };

        public void CopyPositionFrom(Mark original, Vector delta)
        {
            Start = original.Start + delta;
            End = original.End + delta;
            Points = original.Points.Select(point => point + delta).ToList();
        }
    }
}
