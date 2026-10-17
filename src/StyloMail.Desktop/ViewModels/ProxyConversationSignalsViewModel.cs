using System.Collections.ObjectModel;
using System.Globalization;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.ViewModels;

/// <summary>
/// One thread's per-turn signals: warning markers by name and a tone series with explicit gaps.
/// </summary>
/// <remarks>
/// <b>Availability is per point, not per screen.</b> A turn either carries a measurement (markers
/// plus a marker count, where zero is a measured clean message) or states that no analysis was
/// produced and names the reason. This view model never fills a missing point and never carries a
/// value across a gap. Tone is a separate measurement, so a turn can be measured for warnings and
/// still have no tone; that turn is a gap in the tone series rather than a plotted zero.
///
/// <para>
/// Nothing here renders message content, a subject, an address or a login. Thread and turn tokens
/// are shortened for display and never decoded.
/// </para>
/// </remarks>
public sealed class ProxyConversationSignalsViewModel : ObservableObject
{
    private readonly ProxyConversationClient? _client;
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _failed;
    private string _threadToken = string.Empty;
    private string _provenanceLine = "No window has been read yet.";
    private string _statusLabel = "Signals are not loaded.";
    private string _emptyLabel = string.Empty;
    private ToneSeries _tone = ToneSeries.Empty;

    public ProxyConversationSignalsViewModel(ProxyConversationClient? client = null) => _client = client;

    public ObservableCollection<ProxySignalPointView> Points { get; } = [];

    /// <summary>The opaque token of the thread currently shown, or empty before anything is selected.</summary>
    public string ThreadToken => _threadToken;

    public string ThreadLabel => string.IsNullOrEmpty(_threadToken)
        ? "No thread selected"
        : "Thread " + ProxyConversationText.ShortToken(_threadToken) + " (opaque token)";

    public string ProvenanceLine
    {
        get => _provenanceLine;
        private set => Set(ref _provenanceLine, value);
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => Set(ref _statusLabel, value);
    }

    public string EmptyLabel
    {
        get => _emptyLabel;
        private set => Set(ref _emptyLabel, value);
    }

    public bool HasLoaded => _hasLoaded;

    public bool HasPoints => Points.Count > 0;

    /// <summary>An empty series is a readable answer, so this is only true after a successful read.</summary>
    public bool ShowEmpty => _hasLoaded && !_failed && Points.Count == 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public ToneSeries Tone
    {
        get => _tone;
        private set
        {
            if (Set(ref _tone, value))
            {
                Raise(nameof(HasToneSeries));
                Raise(nameof(ToneRangeLabel));
            }
        }
    }

    /// <summary>Whether any turn produced a tone value, and therefore whether a series can be drawn.</summary>
    public bool HasToneSeries => _tone.HasMeasuredTone;

    /// <summary>What the series shows, how many turns it could not measure, and the scale it is drawn on.</summary>
    public string ToneRangeLabel
    {
        get
        {
            // Before any read there is no window and no series, so a sentence about "turns in this
            // window" would be a claim the screen has not made. The runtime capture found this: the
            // unselected pane read "No turns to plot in this window." beside "Signals are not loaded."
            if (!_hasLoaded) return string.Empty;

            var measured = _tone.Points.Where(point => point.Score.HasValue).Select(point => point.Score!.Value).ToArray();

            if (measured.Length == 0)
            {
                return _tone.PointCount == 0
                    ? "No turns to plot in this window."
                    : "Tone was not produced for any of the " + _tone.PointCount.ToString(CultureInfo.InvariantCulture)
                        + " turn(s) in this window. A gap is a missing measurement, not zero.";
            }

            var minimum = measured.Min();
            var maximum = measured.Max();
            var scale = minimum.Equals(maximum)
                ? "the only measured tone is " + Format(minimum)
                : "tone scale " + Format(minimum) + " to " + Format(maximum);

            return scale + " across " + measured.Length.ToString(CultureInfo.InvariantCulture)
                + " measured turn(s); " + _tone.GapCount.ToString(CultureInfo.InvariantCulture)
                + " turn(s) not produced. The line stops at every gap and is never drawn through one.";
        }
    }

    /// <summary>
    /// Reads one thread's signals. A null or blank token is a statement about the selection, not a
    /// request, and is reported as such.
    /// </summary>
    public async Task LoadAsync(string? threadToken, CancellationToken cancellationToken = default)
    {
        if (_isLoading) return;

        if (string.IsNullOrWhiteSpace(threadToken))
        {
            _threadToken = string.Empty;
            Raise(nameof(ThreadToken));
            Raise(nameof(ThreadLabel));
            Clear();
            Fail("No thread is selected, so no signals read was attempted.");
            return;
        }

        _threadToken = threadToken;
        Raise(nameof(ThreadToken));
        Raise(nameof(ThreadLabel));

        if (_client is null)
        {
            Clear();
            Fail("The signals read is unavailable until the console is connected to a Host.");
            return;
        }

        // A new read is for a new thread (there is no paging on this route), so the previous
        // series is cleared before the read starts. Showing the old thread's points under the new
        // thread's token would be worse than showing nothing while the read is in flight.
        Clear();
        IsLoading = true;
        StatusLabel = "Reading signals for thread " + ProxyConversationText.ShortToken(threadToken)
            + " in " + ProxyConversationClient.Last24Hours + "...";

        try
        {
            var response = await _client.GetSignalsAsync(threadToken, cancellationToken);
            Apply(threadToken, response);
        }
        catch (StyloMailApiException error)
        {
            Fail("The signals read for thread " + ProxyConversationText.ShortToken(threadToken)
                + " failed: " + ProxyConversationText.Describe(error) + ".");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(string requestedToken, ProxyConversationSignalsResponse response)
    {
        if (!string.Equals(response.ThreadToken, requestedToken, StringComparison.Ordinal))
        {
            Fail("The signals read returned points for a different thread token. No points were shown.");
            return;
        }

        if (!response.Points.All(point => ProxyConversationClient.IsOpaqueToken(point.TurnToken)))
        {
            Fail("The signals read returned a value that is not an opaque turn token. No points were shown, because this screen does not render subjects, addresses or logins.");
            return;
        }

        // One point per turn, ordered by observation time. The order on the wire is the Host's to
        // choose; the series is read in time order, with the token as a stable tie-break so two
        // points at one instant cannot swap between reads.
        var ordered = response.Points
            .OrderBy(point => point.ObservedAt)
            .ThenBy(point => point.TurnToken, StringComparer.Ordinal)
            .ToArray();

        foreach (var point in ordered)
        {
            Points.Add(ProxySignalPointView.From(point));
        }

        Tone = ToneSeries.From(Points);

        var provenance = string.IsNullOrWhiteSpace(response.Provenance)
            ? ProxyConversationText.WindowLine(response.Window, response.WindowStart, response.WindowEnd)
            : ProxyConversationText.WindowLine(response.Window, response.WindowStart, response.WindowEnd)
                + " Source: " + response.Provenance + ".";
        ProvenanceLine = provenance;

        _hasLoaded = true;
        _failed = false;

        if (Points.Count == 0)
        {
            EmptyLabel = "No transport turns were recorded for this thread in " + response.Window
                + ". An empty series is not an error.";
            StatusLabel = EmptyLabel;
        }
        else
        {
            var measuredWarnings = Points.Count(point => point.WarningsAvailable);
            var measuredTone = Points.Count(point => point.ToneScore.HasValue);
            EmptyLabel = string.Empty;
            StatusLabel = "Showing " + Points.Count.ToString(CultureInfo.InvariantCulture)
                + " turn(s) for thread " + ProxyConversationText.ShortToken(_threadToken) + " in " + response.Window
                + ". Warning analysis was produced on " + measuredWarnings.ToString(CultureInfo.InvariantCulture)
                + " turn(s); tone was produced on " + measuredTone.ToString(CultureInfo.InvariantCulture) + ".";
        }

        Raise(nameof(HasPoints));
        Raise(nameof(ShowEmpty));
        Raise(nameof(HasLoaded));
        Raise(nameof(HasToneSeries));
        Raise(nameof(ToneRangeLabel));
    }

    private void Clear()
    {
        Points.Clear();
        Tone = ToneSeries.Empty;
        _hasLoaded = false;
        _failed = false;
        Raise(nameof(HasPoints));
        Raise(nameof(ShowEmpty));
        Raise(nameof(HasLoaded));
        Raise(nameof(HasToneSeries));
        Raise(nameof(ToneRangeLabel));
    }

    private void Fail(string message)
    {
        _failed = true;
        _hasLoaded = false;
        StatusLabel = message;
        Raise(nameof(ShowEmpty));
        Raise(nameof(HasLoaded));
        Raise(nameof(ToneRangeLabel));
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>One turn's measurement, rendered as its own statement.</summary>
public sealed class ProxySignalPointView
{
    public required string ObservedAtLabel { get; init; }

    public required string Summary { get; init; }

    public required string MarkerCountLabel { get; init; }

    public required string ToneLabel { get; init; }

    /// <summary>True when the series must stop here rather than carry a value across this turn.</summary>
    public required bool HasToneGap { get; init; }

    /// <summary>The sentence that explains the gap. Empty when this turn has a tone value.</summary>
    public required string ToneGapReason { get; init; }

    public required string AvailabilityLabel { get; init; }

    /// <summary>Whether warning analysis ran for this turn. Marker count zero is meaningful only here.</summary>
    public required bool WarningsAvailable { get; init; }

    /// <summary>Null when no tone was produced. Never defaulted to zero.</summary>
    public double? ToneScore { get; init; }

    public required IReadOnlyList<ProxyWarningMarkerView> Markers { get; init; }

    public bool HasMarkers => Markers.Count > 0;

    public static ProxySignalPointView From(ProxySignalPointResponse point)
    {
        ArgumentNullException.ThrowIfNull(point);

        var available = ProxyConversationText.IsAvailable(point.Availability);
        var reason = ProxyConversationText.ReasonText(point.AvailabilityReason);
        var hasToneGap = point.ToneScore is null;

        return new ProxySignalPointView
        {
            ObservedAtLabel = ProxyConversationText.Utc(point.ObservedAt),
            Summary = ProxyConversationText.Utc(point.ObservedAt)
                + " · turn " + ProxyConversationText.ShortToken(point.TurnToken),
            MarkerCountLabel = available
                ? point.MarkerCount.ToString(CultureInfo.InvariantCulture)
                    + " warning" + (point.MarkerCount == 1 ? string.Empty : "s") + ", measured"
                : "warning count not produced: " + reason,
            ToneLabel = point.ToneScore is { } tone
                ? "tone " + tone.ToString("0.###", CultureInfo.InvariantCulture)
                : available
                    ? "tone not produced for this turn (absent, not zero)"
                    : "tone not produced: " + reason,
            HasToneGap = hasToneGap,
            ToneGapReason = hasToneGap ? ToneGapReasonFor(available, reason) : string.Empty,
            AvailabilityLabel = available
                ? "analysis available"
                : "analysis not produced: " + reason,
            WarningsAvailable = available,
            ToneScore = point.ToneScore,
            Markers = [.. point.WarningMarkers.Select(kind => new ProxyWarningMarkerView(kind))],
        };
    }

    private static string ToneGapReasonFor(bool available, string reason)
    {
        return available
            ? "This turn was measured for warnings, but tone was not produced. The series shows a gap here rather than zero."
            : "No analysis was produced for this turn (" + reason + "), so tone is a gap here rather than zero.";
    }
}

/// <summary>A warning marker, shown by the kind the producer named.</summary>
public sealed record ProxyWarningMarkerView(string Kind)
{
    /// <summary>The rendered label. The kind is carried verbatim so a new kind appears rather than vanishes.</summary>
    public string Label => "warning: " + Kind;
}

/// <summary>
/// The tone series as plotted: points in turn order, runs of adjacent measured turns, and the gaps
/// in between.
/// </summary>
/// <remarks>
/// A segment is a run of <b>consecutive measured turns</b>. A missing measurement ends the run even
/// when a measured turn follows it, so there is no segment whose endpoints straddle a gap and no
/// consumer can draw a line through one. A turn with no tone contributes no score at all; nothing
/// here substitutes zero.
/// </remarks>
public sealed class ToneSeries
{
    public static ToneSeries Empty { get; } = new([], [], 0);

    private ToneSeries(
        IReadOnlyList<TonePlotPoint> points,
        IReadOnlyList<TonePlotSegment> segments,
        int gapCount)
    {
        Points = points;
        Segments = segments;
        GapCount = gapCount;
    }

    public IReadOnlyList<TonePlotPoint> Points { get; }

    public IReadOnlyList<TonePlotSegment> Segments { get; }

    /// <summary>How many turns produced no tone value.</summary>
    public int GapCount { get; }

    public int PointCount => Points.Count;

    public bool HasMeasuredTone => Points.Any(point => point.Score.HasValue);

    public static ToneSeries From(IReadOnlyList<ProxySignalPointView> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var plotted = new List<TonePlotPoint>(points.Count);
        var segments = new List<TonePlotSegment>();
        List<int>? run = null;

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            plotted.Add(new TonePlotPoint(index, point.ToneScore, point.ToneGapReason));

            if (point.ToneScore is null)
            {
                if (run is { Count: > 0 })
                {
                    segments.Add(new TonePlotSegment(run));
                }

                run = null;
                continue;
            }

            run ??= [];
            run.Add(index);
        }

        if (run is { Count: > 0 })
        {
            segments.Add(new TonePlotSegment(run));
        }

        var gaps = plotted.Count(point => point.Score is null);
        return new ToneSeries(plotted, segments, gaps);
    }
}

/// <summary>One turn's position in the series, with its score or the reason it has none.</summary>
public sealed record TonePlotPoint(int Index, double? Score, string GapReason);

/// <summary>A run of adjacent measured turns. Indices refer to <see cref="ToneSeries.Points"/>.</summary>
public sealed record TonePlotSegment(IReadOnlyList<int> Indices);
