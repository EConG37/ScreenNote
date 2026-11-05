using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ScreenNote;

internal static partial class StudioUi
{
    internal const double InterfaceScale = 0.8;
    private static bool _compactSizingEnabled;
    private static readonly DependencyProperty CompactedProperty = DependencyProperty.RegisterAttached(
        "Compacted", typeof(bool), typeof(StudioUi), new PropertyMetadata(false));
    private static readonly DependencyProperty ChromeScopeProperty = DependencyProperty.RegisterAttached(
        "ChromeScope", typeof(bool), typeof(StudioUi), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    internal static void EnableCompactSizing()
    {
        if (_compactSizingEnabled) return;
        _compactSizingEnabled = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(CompactLoaded), handledEventsToo: true);
    }

    internal static void MarkCaptureChrome(FrameworkElement element)
    {
        element.SetValue(ChromeScopeProperty, true);
        element.IsVisibleChanged += (_, _) =>
        {
            if (element.IsVisible) element.Dispatcher.BeginInvoke(() => CompactTree(element), DispatcherPriority.Loaded);
        };
    }

    private static void CompactTree(DependencyObject root)
    {
        if (root is FrameworkElement element)
            CompactLoaded(element, new RoutedEventArgs(FrameworkElement.LoadedEvent, element));
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) CompactTree(VisualTreeHelper.GetChild(root, i));
        if (root is ItemsControl items)
            for (var i = 0; i < items.Items.Count; i++)
                if (items.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container) CompactTree(container);
    }

    private static void CompactLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement element || !ReferenceEquals(args.OriginalSource, element)
            || (bool)element.GetValue(CompactedProperty)) return;
        element.SetValue(CompactedProperty, true);
        var window = Window.GetWindow(element);
        if (element is Window shell)
        {
            ScaleValue(shell, TextElement.FontSizeProperty);
            if (shell.Content is DependencyObject content) CompactTree(content);
            EventHandler? rendered = null;
            rendered = (_, _) =>
            {
                shell.ContentRendered -= rendered;
                if (shell.Content is DependencyObject loadedContent) CompactTree(loadedContent);
            };
            shell.ContentRendered += rendered;
            if (shell is CaptureWindow) return;
            ScaleValue(shell, FrameworkElement.MinWidthProperty);
            ScaleValue(shell, FrameworkElement.MinHeightProperty);
            if (shell.ResizeMode == ResizeMode.NoResize)
            {
                ScaleValue(shell, FrameworkElement.WidthProperty);
                ScaleValue(shell, FrameworkElement.HeightProperty);
            }
            return;
        }

        // Screen selections and image-pixel annotations must stay in their original
        // coordinate system. Only the explicitly marked capture tool panels compact.
        if (window is CaptureWindow && !(bool)element.GetValue(ChromeScopeProperty)) return;
        if (IsDocumentVisual(element)) return;

        if (element is ItemsControl items)
            items.ItemContainerGenerator.StatusChanged += (_, _) =>
            {
                if (items.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                    items.Dispatcher.BeginInvoke(() => CompactTree(items), DispatcherPriority.Loaded);
            };

        ScaleValue(element, TextElement.FontSizeProperty);
        ScaleValue(element, FrameworkElement.MarginProperty);
        ScaleValue(element, FrameworkElement.MinWidthProperty);
        ScaleValue(element, FrameworkElement.MinHeightProperty);
        ScaleValue(element, FrameworkElement.MaxWidthProperty);
        ScaleValue(element, FrameworkElement.MaxHeightProperty);
        if (element is not Image image || ReferenceEquals(image.Source, AppIcon.WindowImage))
        {
            ScaleValue(element, FrameworkElement.WidthProperty);
            ScaleValue(element, FrameworkElement.HeightProperty);
        }
        if (element is Control)
        {
            ScaleValue(element, Control.PaddingProperty);
            ScaleValue(element, Control.BorderThicknessProperty);
        }
        if (element is Border)
        {
            ScaleValue(element, Border.PaddingProperty);
            ScaleValue(element, Border.BorderThicknessProperty);
            ScaleValue(element, Border.CornerRadiusProperty);
        }
        if (element is TextBlock) ScaleValue(element, TextBlock.LineHeightProperty);
        if (element is Grid grid)
        {
            foreach (var row in grid.RowDefinitions)
            {
                if (row.Height.IsAbsolute) row.Height = new GridLength(row.Height.Value * InterfaceScale);
                if (row.MinHeight > 0) row.MinHeight *= InterfaceScale;
                if (double.IsFinite(row.MaxHeight)) row.MaxHeight *= InterfaceScale;
            }
            foreach (var column in grid.ColumnDefinitions)
            {
                if (column.Width.IsAbsolute) column.Width = new GridLength(column.Width.Value * InterfaceScale);
                if (column.MinWidth > 0) column.MinWidth *= InterfaceScale;
                if (double.IsFinite(column.MaxWidth)) column.MaxWidth *= InterfaceScale;
            }
        }
    }

    private static bool IsDocumentVisual(FrameworkElement element)
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Name: "ImageView" or "FilmStrip" }) return true;
            if (current is AnnotationSurface or SubtitleCropControl or MediaElement) return true;
            if (current is Canvas)
            {
                if ((bool)element.GetValue(ChromeScopeProperty) && !(bool)current.GetValue(ChromeScopeProperty)) break;
                return true;
            }
            if (current is Window) break;
        }
        return false;
    }

    private static void ScaleValue(DependencyObject target, DependencyProperty property)
    {
        var source = DependencyPropertyHelper.GetValueSource(target, property);
        // Inherited fonts and template bindings already follow their compact parent.
        // Keeping bindings intact also preserves the image/annotation data model.
        if (source.IsExpression || source.BaseValueSource is BaseValueSource.Default or BaseValueSource.Inherited) return;
        var value = target.GetValue(property);
        object? compact = value switch
        {
            double number when double.IsFinite(number) && number > 0 => number * InterfaceScale,
            Thickness inset => new Thickness(inset.Left * InterfaceScale, inset.Top * InterfaceScale,
                inset.Right * InterfaceScale, inset.Bottom * InterfaceScale),
            CornerRadius radius => new CornerRadius(radius.TopLeft * InterfaceScale, radius.TopRight * InterfaceScale,
                radius.BottomRight * InterfaceScale, radius.BottomLeft * InterfaceScale),
            _ => null
        };
        if (compact != null) target.SetValue(property, compact);
    }
}
