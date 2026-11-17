using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SerialPortSimulator.Controls;

/// <summary>Arranges cards from left to right and wraps them into equal-width columns.</summary>
public sealed class FlowPanel : Panel
{
    public static readonly DependencyProperty MinimumItemWidthProperty =
        DependencyProperty.Register(
            nameof(MinimumItemWidth),
            typeof(double),
            typeof(FlowPanel),
            new PropertyMetadata(360d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(FlowPanel),
            new PropertyMetadata(16d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(FlowPanel),
            new PropertyMetadata(16d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty SpanProperty =
        DependencyProperty.RegisterAttached(
            "Span",
            typeof(int),
            typeof(FlowPanel),
            new PropertyMetadata(1, OnLayoutPropertyChanged));

    public double MinimumItemWidth
    {
        get => (double)GetValue(MinimumItemWidthProperty);
        set => SetValue(MinimumItemWidthProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public static int GetSpan(DependencyObject element) =>
        (int)element.GetValue(SpanProperty);

    public static void SetSpan(DependencyObject element, int value) =>
        element.SetValue(SpanProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        double availableWidth = double.IsInfinity(availableSize.Width)
            ? Math.Max(MinimumItemWidth, 0)
            : Math.Max(0, availableSize.Width);
        int columns = GetColumnCount(availableWidth);
        double itemWidth = GetItemWidth(availableWidth, columns);
        var placements = BuildPlacements(availableWidth, columns, itemWidth);

        double rowY = 0;
        foreach (var row in placements.Rows)
        {
            foreach (var item in row)
                item.Element.Measure(new Size(item.Width, double.PositiveInfinity));
            double rowHeight = row.Count == 0
                ? 0
                : row.Max(item => item.Element.DesiredSize.Height);
            rowY += rowHeight;
        }
        if (placements.Rows.Count > 1)
            rowY += VerticalSpacing * (placements.Rows.Count - 1);

        return new Size(availableWidth, rowY);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = Math.Max(0, finalSize.Width);
        int columns = GetColumnCount(width);
        double itemWidth = GetItemWidth(width, columns);
        var placements = BuildPlacements(width, columns, itemWidth);
        double y = 0;

        foreach (var row in placements.Rows)
        {
            double rowHeight = row.Count == 0
                ? 0
                : row.Max(item => item.Element.DesiredSize.Height);
            foreach (var item in row)
            {
                double x = item.StartColumn * (itemWidth + HorizontalSpacing);
                item.Element.Arrange(new Rect(x, y, item.Width, rowHeight));
            }
            y += rowHeight + VerticalSpacing;
        }

        if (placements.Rows.Count > 0)
            y -= VerticalSpacing;
        return new Size(width, Math.Max(0, y));
    }

    private PlacementResult BuildPlacements(double width, int columns, double itemWidth)
    {
        var rows = new List<List<Placement>>();
        var currentRow = new List<Placement>();
        int column = 0;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            int span = Math.Clamp(GetSpan(child), 1, columns);
            if (column + span > columns && currentRow.Count > 0)
            {
                rows.Add(currentRow);
                currentRow = new List<Placement>();
                column = 0;
            }

            double childWidth = span * itemWidth + (span - 1) * HorizontalSpacing;
            currentRow.Add(new Placement(child, column, span, childWidth));
            column += span;
            if (column >= columns)
            {
                rows.Add(currentRow);
                currentRow = new List<Placement>();
                column = 0;
            }
        }

        if (currentRow.Count > 0) rows.Add(currentRow);
        return new PlacementResult(rows);
    }

    private int GetColumnCount(double width) =>
        Math.Max(1, (int)Math.Floor(
            (width + HorizontalSpacing) / (MinimumItemWidth + HorizontalSpacing)));

    private double GetItemWidth(double width, int columns) =>
        Math.Max(0, (width - (columns - 1) * HorizontalSpacing) / columns);

    private static void OnLayoutPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is UIElement element)
        {
            element.InvalidateMeasure();
            if (element is FrameworkElement frameworkElement)
                frameworkElement.InvalidateArrange();
        }
        else if (dependencyObject is FlowPanel panel)
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    private sealed record Placement(UIElement Element, int StartColumn, int Span, double Width);
    private sealed record PlacementResult(List<List<Placement>> Rows);
}
