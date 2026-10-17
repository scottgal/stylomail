using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Views;

/// <summary>
/// The per-thread signals pane. Embed this, set its <see cref="Control.DataContext"/> to a
/// <see cref="ProxyConversationSignalsViewModel"/>, and call <c>LoadAsync(token)</c> when a
/// conversation is selected.
/// </summary>
public partial class ConversationSignalsView : UserControl
{
    /// <summary>Required by the XAML compiler; the hosting page supplies the view model.</summary>
    public ConversationSignalsView() => InitializeComponent();

    private ProxyConversationSignalsViewModel? Model => DataContext as ProxyConversationSignalsViewModel;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            await model.LoadAsync(model.ThreadToken);
        }
    }
}

/// <summary>
/// Plots a <see cref="ToneSeries"/> as a broken line.
/// </summary>
/// <remarks>
/// <b>It draws only the segments the series defines.</b> There is no interpolation step here, so a
/// turn without a tone is not plotted and no line crosses it. A gap is marked with a dashed
/// vertical rule rather than a point, because a point on a zero axis would say "measured as zero",
/// which is the one thing an absent measurement must never say. The scale is the measured range,
/// printed beneath the chart by the view model, rather than an assumed -1..1.
/// </remarks>
public sealed class ToneSeriesChart : Control
{
    private static readonly IBrush SeriesBrush = new SolidColorBrush(Color.Parse("#0A84FF"));
    private static readonly IBrush GapBrush = new SolidColorBrush(Color.Parse("#86868B"));

    public static readonly StyledProperty<ToneSeries?> SeriesProperty =
        AvaloniaProperty.Register<ToneSeriesChart, ToneSeries?>(nameof(Series));

    static ToneSeriesChart()
    {
        AffectsRender<ToneSeriesChart>(SeriesProperty);
    }

    public ToneSeries? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var series = Series;
        if (series is null || series.PointCount == 0) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 8 || height <= 8) return;

        const double padding = 8;
        var innerWidth = width - (padding * 2);
        var innerHeight = height - (padding * 2);
        var scores = series.Points.Where(point => point.Score.HasValue).Select(point => point.Score!.Value).ToArray();
        if (scores.Length == 0) return;

        var minimum = scores.Min();
        var maximum = scores.Max();
        var span = maximum - minimum;

        double X(int index) => series.PointCount == 1
            ? padding + (innerWidth / 2)
            : padding + (innerWidth * index / (series.PointCount - 1));

        double Y(double score) => span <= 0
            ? padding + (innerHeight / 2)
            : padding + innerHeight - (((score - minimum) / span) * innerHeight);

        // Gaps first, so a segment drawn over the same column still reads as the measurement.
        var gapPen = new Pen(GapBrush, 1, new DashStyle(new double[] { 2, 3 }, 0));
        foreach (var point in series.Points.Where(point => !point.Score.HasValue))
        {
            var x = X(point.Index);
            context.DrawLine(gapPen, new Point(x, padding), new Point(x, padding + innerHeight));
        }

        var linePen = new Pen(SeriesBrush, 2, lineCap: PenLineCap.Round);
        foreach (var segment in series.Segments)
        {
            var points = segment.Indices
                .Select(index => new Point(X(index), Y(series.Points[index].Score!.Value)))
                .ToArray();

            for (var index = 1; index < points.Length; index++)
            {
                context.DrawLine(linePen, points[index - 1], points[index]);
            }

            foreach (var point in points)
            {
                context.DrawEllipse(SeriesBrush, null, point, 3, 3);
            }
        }
    }
}
