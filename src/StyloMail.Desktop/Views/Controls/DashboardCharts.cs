using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views.Controls;

/// <summary>
/// Draws a <see cref="TimeChart"/> as bars or broken lines.
/// </summary>
/// <remarks>
/// <para>
/// The control draws the plot only. Every axis label, window and coverage note is real text in the
/// XAML around it, so a screen reader and a driving script can read them. The existing
/// <c>ToneSeriesChart</c> is the precedent for the drawing style and for the gap rule: no segment
/// crosses a missing value.
/// </para>
/// <para>
/// A zero is a value and is drawn. This matters for the flow route, which sends a measured zero for
/// an empty bucket inside a complete window; treating that as a gap would break the line exactly
/// where the operator most needs to see the quiet hour.
/// </para>
/// </remarks>
public sealed class TimeChartControl : Control
{
    private static readonly IBrush BaselineBrush = new SolidColorBrush(Color.Parse("#D4D4D9"));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#EFEFF2"));

    private static readonly IBrush[] Palette =
        [.. ChartPalette.Hex.Select(hex => (IBrush)new SolidColorBrush(Color.Parse(hex)))];

    public static readonly StyledProperty<TimeChart?> SeriesProperty =
        AvaloniaProperty.Register<TimeChartControl, TimeChart?>(nameof(Series));

    static TimeChartControl()
    {
        AffectsRender<TimeChartControl>(SeriesProperty);
    }

    public TimeChart? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var series = Series;
        if (series is null || !series.HasData) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 8 || height <= 8) return;

        const double padding = 6;
        var innerWidth = width - (padding * 2);
        var innerHeight = height - (padding * 2);
        var maximum = series.Maximum;

        // Three horizontal rules: top, middle and baseline. The labels beside
        // them are XAML text bound to the same numbers.
        var gridPen = new Pen(GridBrush, 1);
        context.DrawLine(gridPen, new Point(padding, padding), new Point(width - padding, padding));
        context.DrawLine(gridPen, new Point(padding, padding + (innerHeight / 2)), new Point(width - padding, padding + (innerHeight / 2)));

        var baselinePen = new Pen(BaselineBrush, 1);
        context.DrawLine(baselinePen, new Point(padding, padding + innerHeight), new Point(width - padding, padding + innerHeight));

        if (series.Shape == ChartShape.Bar)
        {
            DrawBars(context, series, padding, innerWidth, innerHeight, maximum);
            return;
        }

        DrawLines(context, series, padding, innerWidth, innerHeight, maximum);
    }

    private static void DrawBars(
        DrawingContext context,
        TimeChart series,
        double padding,
        double innerWidth,
        double innerHeight,
        double maximum)
    {
        if (series.Lines.Count == 0) return;

        var line = series.Lines[0];
        if (line.Points.Count == 0) return;

        var slot = innerWidth / line.Points.Count;
        var barWidth = Math.Max(1, slot * 0.62);
        var brush = Palette[0];

        for (var index = 0; index < line.Points.Count; index++)
        {
            if (line.Points[index].Value is not { } value || value <= 0) continue;

            var x = padding + (slot * index) + ((slot - barWidth) / 2);
            var barHeight = innerHeight * (value / maximum);
            var top = padding + innerHeight - barHeight;
            context.DrawRectangle(brush, null, new Rect(x, top, barWidth, barHeight));
        }
    }

    private static void DrawLines(
        DrawingContext context,
        TimeChart series,
        double padding,
        double innerWidth,
        double innerHeight,
        double maximum)
    {
        for (var lineIndex = 0; lineIndex < series.Lines.Count; lineIndex++)
        {
            var line = series.Lines[lineIndex];
            var points = line.Points;
            if (points.Count == 0) continue;

            var brush = Palette[lineIndex % Palette.Length];
            var pen = new Pen(brush, 2, lineCap: PenLineCap.Round);

            double X(int index) => points.Count == 1
                ? padding + (innerWidth / 2)
                : padding + (innerWidth * index / (points.Count - 1));

            double Y(double value) => padding + innerHeight - (innerHeight * (value / maximum));

            for (var index = 1; index < points.Count; index++)
            {
                if (points[index - 1].Value is not { } previous || points[index].Value is not { } current)
                {
                    continue;
                }

                context.DrawLine(pen, new Point(X(index - 1), Y(previous)), new Point(X(index), Y(current)));
            }

            for (var index = 0; index < points.Count; index++)
            {
                if (points[index].Value is not { } value) continue;
                context.DrawEllipse(brush, null, new Point(X(index), Y(value)), 2.5, 2.5);
            }
        }
    }
}

/// <summary>
/// A small shape beside a sentence. It is a supplement and never the only expression of a fact.
/// </summary>
public sealed class SparklineControl : Control
{
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#0A84FF"));

    public static readonly StyledProperty<TimeChart?> SeriesProperty =
        AvaloniaProperty.Register<SparklineControl, TimeChart?>(nameof(Series));

    static SparklineControl()
    {
        AffectsRender<SparklineControl>(SeriesProperty);
    }

    public TimeChart? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var series = Series;
        if (series is null || !series.HasData) return;

        if (series.Lines.Count == 0) return;

        var line = series.Lines[0];
        if (line.Points.Count < 2) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 8 || height <= 8) return;

        const double padding = 2;
        var innerWidth = width - (padding * 2);
        var innerHeight = height - (padding * 2);
        var maximum = series.Maximum;

        var pen = new Pen(LineBrush, 1.5, lineCap: PenLineCap.Round);

        for (var index = 1; index < line.Points.Count; index++)
        {
            if (line.Points[index - 1].Value is not { } previous || line.Points[index].Value is not { } current)
            {
                continue;
            }

            var x1 = padding + (innerWidth * (index - 1) / (line.Points.Count - 1));
            var x2 = padding + (innerWidth * index / (line.Points.Count - 1));
            var y1 = padding + innerHeight - (innerHeight * (previous / maximum));
            var y2 = padding + innerHeight - (innerHeight * (current / maximum));

            context.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
        }
    }
}
