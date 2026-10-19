using System.Globalization;

namespace StyloMail.Desktop.ViewModels;

/// <summary>
/// The chart palette, in the order the control draws its lines.
/// </summary>
/// <remarks>
/// The control and the legend read the same list, so a line and its key cannot drift into different
/// colours. The model holds hex strings rather than Avalonia brushes so the view models stay
/// testable without a display.
/// </remarks>
public static class ChartPalette
{
    public static readonly string[] Hex =
    [
        "#0A84FF",
        "#34A853",
        "#5E5CE6",
        "#E8A33D",
        "#D7443E",
        "#86868B",
    ];
}

/// <summary>One bucket in a time series. A null value is a gap, not a zero.</summary>
public readonly record struct ChartPoint(DateTimeOffset BucketStart, double? Value);

/// <summary>One key in a chart legend.</summary>
public sealed record ChartLegendItem(string Name, int PaletteIndex);

/// <summary>One named line in a time chart.</summary>
public sealed class ChartLine
{
    public ChartLine(string name, IReadOnlyList<ChartPoint> points)
    {
        Name = name;
        Points = points;
    }

    public string Name { get; }

    public IReadOnlyList<ChartPoint> Points { get; }
}

/// <summary>How a time chart draws its values.</summary>
public enum ChartShape
{
    Line,
    Bar,
}

/// <summary>
/// A chart ready to render, with every label the card must show.
/// </summary>
/// <remarks>
/// The axis text is computed here rather than inside the control so it is real text in the visual
/// tree. A chart whose only axis was drawn pixels would be unreadable to a screen reader and
/// invisible to the driving scripts, and the design requires the axis label, window and coverage to
/// be visible facts.
/// </remarks>
public sealed class TimeChart
{
    public required string Title { get; init; }

    public required string Unit { get; init; }

    public required string WindowLabel { get; init; }

    public required string CoverageLabel { get; init; }

    public required IReadOnlyList<ChartLine> Lines { get; init; }

    public ChartShape Shape { get; init; } = ChartShape.Line;

    public string EmptyLabel { get; init; } = "No data for this window";

    public bool HasData => Lines.Any(line => line.Points.Any(point => point.Value.HasValue));

    /// <summary>The keys for the lines, in the order the control draws them.</summary>
    public IReadOnlyList<ChartLegendItem> Legend
        => [.. Lines.Select((line, index) => new ChartLegendItem(line.Name, index))];

    /// <summary>
    /// The top of the value axis: an even count at least as large as the data.
    /// </summary>
    /// <remarks>
    /// A count axis must not print 0.5. Rounding the top to an even number keeps
    /// the midpoint a whole count, and a floor of two means a chart with one
    /// observation still has a readable scale rather than a line at the ceiling.
    /// </remarks>
    public double Maximum
    {
        get
        {
            var maximum = Lines
                .SelectMany(line => line.Points)
                .Where(point => point.Value.HasValue)
                .Select(point => point.Value!.Value)
                .DefaultIfEmpty(0)
                .Max();

            if (maximum <= 0) return 2;

            return Math.Max(2, Math.Ceiling(maximum / 2) * 2);
        }
    }

    public string YMaximumLabel => FormatCount(Maximum);

    public string YMiddleLabel => FormatCount(Maximum / 2);

    /// <summary>The first, middle and last bucket time, in local time, for the x axis.</summary>
    public string XStartLabel => AxisTime(0);

    public string XMiddleLabel => AxisTime(1);

    public string XEndLabel => AxisTime(2);

    /// <summary>A complete sentence for the chart's accessible name.</summary>
    public string Summary
    {
        get
        {
            if (!HasData) return $"{Title}. {WindowLabel}. {EmptyLabel}.";

            var total = Lines
                .SelectMany(line => line.Points)
                .Where(point => point.Value.HasValue)
                .Sum(point => point.Value!.Value);

            return $"{Title}. {WindowLabel}. {FormatCount(total)} {Unit}. {CoverageLabel}.";
        }
    }

    private string AxisTime(int position)
    {
        if (Lines.Count == 0) return string.Empty;

        var points = Lines[0].Points;
        if (points.Count == 0) return string.Empty;

        var index = position switch
        {
            0 => 0,
            1 => points.Count / 2,
            _ => points.Count - 1,
        };

        return points[index].BucketStart.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    internal static string FormatCount(double value)
        => value.ToString(value >= 100 ? "N0" : "0.#", CultureInfo.InvariantCulture);
}

/// <summary>One horizontal bar in a categorical chart.</summary>
public sealed class CategoryBar
{
    public CategoryBar(string label, long? value, string valueLabel, double ratio)
    {
        Label = label;
        Value = value;
        ValueLabel = valueLabel;
        Ratio = ratio;
    }

    public string Label { get; }

    public long? Value { get; }

    public string ValueLabel { get; }

    /// <summary>Zero to one, relative to the largest bar. A null value has ratio zero and no bar.</summary>
    public double Ratio { get; }

    public double BarWidth => Math.Round(140 * Ratio, 1);

    public bool HasValue => Value.HasValue;
}

/// <summary>A categorical bar chart with its own window and coverage.</summary>
public sealed class CategoryChart
{
    public required string Title { get; init; }

    public required string WindowLabel { get; init; }

    public required string CoverageLabel { get; init; }

    public required IReadOnlyList<CategoryBar> Bars { get; init; }

    public string EmptyLabel { get; init; } = "No data for this window";

    public bool HasData => Bars.Any(bar => bar.Value is not null and > 0);

    public string Summary
    {
        get
        {
            if (!HasData) return $"{Title}. {WindowLabel}. {EmptyLabel}.";

            var parts = Bars
                .Where(bar => bar.Value.HasValue)
                .Select(bar => $"{bar.Label} {bar.ValueLabel}");

            return $"{Title}. {WindowLabel}. {string.Join(", ", parts)}. {CoverageLabel}.";
        }
    }
}
