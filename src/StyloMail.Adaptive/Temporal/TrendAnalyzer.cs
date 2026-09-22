using StyloMail.Adaptive.Scoring;

namespace StyloMail.Adaptive.Temporal;

/// <summary>Why derivative evidence was withheld.</summary>
public enum TrendSuppressionReason
{
    None = 0,

    /// <summary>Not enough buckets yet to have a trend at all.</summary>
    InsufficientSupport = 1,

    /// <summary>Too much time passed between observations for the difference to mean anything.</summary>
    LongGap = 2,

    /// <summary>A bucket inside the window produced too little to support a reading.</summary>
    SparseBucket = 3,

    /// <summary>The behaviour is under a different regime than the baseline describes.</summary>
    RegimeChange = 4,

    /// <summary>The dimension set changed, so old and new values are not comparable.</summary>
    DimensionSchemaChange = 5,

    /// <summary>No trusted distribution exists to difference against.</summary>
    BaselineUnavailable = 6,
}

/// <summary>
/// One window of comparison: how finely time is bucketed, how far back it reaches, and how
/// much support each reading needs before it counts.
/// </summary>
public sealed record TrendWindow
{
    public required string Name { get; init; }

    public required TimeSpan BucketWidth { get; init; }

    public required int BucketCount { get; init; }

    /// <summary>EWMA time constant across buckets. Larger smooths more and lags more.</summary>
    public required TimeSpan SmoothingTau { get; init; }

    /// <summary>Samples a bucket needs before its dimensions produce a reading.</summary>
    public required int MinimumSamplesPerBucket { get; init; }

    /// <summary>Largest spacing between populated buckets that is still a comparison.</summary>
    public required TimeSpan MaxGap { get; init; }

    /// <summary>Movement below this is not worth naming.</summary>
    public double MovementThreshold { get; init; } = 1e-6;
}

/// <summary>A dimension that moved, and by how much.</summary>
public sealed record TrendMovement
{
    public required string DimensionId { get; init; }

    /// <summary>The phrase this dimension is named by in an operator-facing reason.</summary>
    public required string Label { get; init; }

    public required double VelocityPerSecond { get; init; }

    public required double AccelerationPerSecondSquared { get; init; }
}

/// <summary>What the trend analysis found, and what it declined to find.</summary>
public sealed record TrendResult
{
    public required string WindowName { get; init; }

    public required bool VelocityAvailable { get; init; }

    public required bool AccelerationAvailable { get; init; }

    public required IReadOnlyDictionary<string, double> Velocity { get; init; }

    public required IReadOnlyDictionary<string, double> Acceleration { get; init; }

    public required IReadOnlyList<TrendMovement> Movements { get; init; }

    public required IReadOnlyList<TrendSuppressionReason> Suppressions { get; init; }

    /// <summary>Velocity dimensions as a fraction of the dimensions the baseline can compare.</summary>
    public required double Coverage { get; init; }

    /// <summary>Populated buckets in the window. The minimum-support check, made visible.</summary>
    public required int Support { get; init; }

    /// <summary>Reason-shaped description, never a bare scalar.</summary>
    public required string Narrative { get; init; }

    /// <summary>True when nothing constrained the derivative.</summary>
    public bool IsUnsuppressed => Suppressions.Count == 0;
}

/// <summary>Everything needed to analyse one window.</summary>
public sealed record TrendRequest
{
    public required BucketSeries Series { get; init; }

    public required RobustScaleModel ScaleModel { get; init; }

    public required DateTimeOffset Now { get; init; }

    public required TrendWindow Window { get; init; }

    public string? DimensionSchemaVersion { get; init; }

    public string? PriorDimensionSchemaVersion { get; init; }

    public string? RegimeId { get; init; }

    public string? PriorRegimeId { get; init; }
}

/// <summary>
/// Velocity and acceleration over fixed clock buckets.
/// </summary>
/// <remarks>
/// Derivative evidence is the easiest thing in this system to get wrong, because a missing
/// observation and a quiet period look identical in the arithmetic and completely different in
/// reality. Every path here therefore prefers withholding a reading to producing one, and the
/// reasons for withholding are reported rather than swallowed.
/// </remarks>
public static class TrendAnalyzer
{
    public static TrendResult Analyze(TrendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var suppressions = new List<TrendSuppressionReason>();

        // The window owns the grid, so there is no way to analyse a grid that does not match
        // the window being reported on.
        var grid = request.Series.Window(request.Window, request.Now);
        var populated = grid.Where(bucket => bucket.SampleCount > 0).ToArray();

        if (request.ScaleModel.Dimensions.Count == 0)
        {
            suppressions.Add(TrendSuppressionReason.BaselineUnavailable);
        }

        if (request.RegimeId is not null
            && request.PriorRegimeId is not null
            && !string.Equals(request.RegimeId, request.PriorRegimeId, StringComparison.Ordinal))
        {
            suppressions.Add(TrendSuppressionReason.RegimeChange);
        }

        if (request.DimensionSchemaVersion is not null
            && request.PriorDimensionSchemaVersion is not null
            && !string.Equals(request.DimensionSchemaVersion, request.PriorDimensionSchemaVersion, StringComparison.Ordinal))
        {
            suppressions.Add(TrendSuppressionReason.DimensionSchemaChange);
        }

        // Fewer than two readings is a position, not a trend.
        //
        // Checked exactly once. There used to be two conditions that both reported
        // InsufficientSupport, "no populated buckets" and "fewer than two", and dropping
        // either left the other to add the identical reason, so a mutation of either would
        // have gone undetected. One condition, one place, one reason.
        if (populated.Length < 2)
        {
            suppressions.Add(TrendSuppressionReason.InsufficientSupport);
            return Suppressed(request, suppressions, populated.Length);
        }

        var (first, last) = Range(populated);

        // Sparse means a hole between observations. A leading or trailing empty bucket is not
        // a hole, it is the beginning or the (not yet happened) end of the window.
        var interior = grid
            .Where(bucket => bucket.Index >= first && bucket.Index <= last)
            .ToArray();

        if (interior.Any(bucket => bucket.SampleCount < request.Window.MinimumSamplesPerBucket))
        {
            suppressions.Add(TrendSuppressionReason.SparseBucket);
        }

        // Elapsed time between consecutive observations, not a count of indices: an index
        // difference is only a duration when the bucket happens to be one minute wide, and
        // the slow window's buckets are an hour each.
        for (var i = 1; i < populated.Length; i++)
        {
            if (populated[i].Start - populated[i - 1].Start > request.Window.MaxGap)
            {
                suppressions.Add(TrendSuppressionReason.LongGap);
                break;
            }
        }

        if (suppressions.Count > 0)
        {
            return Suppressed(request, suppressions, populated.Length);
        }

        return Compute(request, populated);
    }

    /// <summary>The first and last populated bucket indices. Requires at least one bucket.</summary>
    private static (long First, long Last) Range(BehaviourBucket[] populated) =>
        (populated[0].Index, populated[^1].Index);

    private static TrendResult Suppressed(
        TrendRequest request,
        List<TrendSuppressionReason> suppressions,
        int support)
    {
        var reasons = string.Join(",", suppressions.Select(CodeFor));

        return new TrendResult
        {
            WindowName = request.Window.Name,
            VelocityAvailable = false,
            AccelerationAvailable = false,
            Velocity = new Dictionary<string, double>(StringComparer.Ordinal),
            Acceleration = new Dictionary<string, double>(StringComparer.Ordinal),
            Movements = [],
            Suppressions = suppressions,
            Coverage = 0.0,
            Support = support,
            Narrative = $"derivative evidence suppressed ({reasons})",
        };
    }

    /// <summary>
    /// Differences the smoothed series. Only reached with at least two populated buckets,     /// <see cref="Analyze"/> refuses anything less before calling.
    /// </summary>
    /// <remarks>
    /// There used to be a second "fewer than two readings" guard here as well. It was
    /// unreachable (smoothing yields exactly one entry per populated bucket) and it reported
    /// the same reason as the check in <see cref="Analyze"/>, so removing either one left the
    /// other to produce an identical result, and a mutation of either would have gone
    /// undetected. One condition, one place. The index arithmetic below relies on that.
    /// </remarks>
    private static TrendResult Compute(TrendRequest request, BehaviourBucket[] populated)
    {
        var smoothed = Smooth(request, populated);

        var velocity = new Dictionary<string, double>(StringComparer.Ordinal);
        var acceleration = new Dictionary<string, double>(StringComparer.Ordinal);

        var lastStep = Step(smoothed[^2], smoothed[^1]);
        foreach (var (dimensionId, value) in lastStep)
        {
            velocity[dimensionId] = value;
        }

        if (smoothed.Count >= 3)
        {
            var priorStep = Step(smoothed[^3], smoothed[^2]);
            var elapsed = elapsedBetween(smoothed[^2], smoothed[^1]).TotalSeconds;

            foreach (var (dimensionId, value) in lastStep)
            {
                if (priorStep.TryGetValue(dimensionId, out var prior))
                {
                    acceleration[dimensionId] = (value - prior) / elapsed;
                }
            }
        }

        var movements = BuildMovements(velocity, acceleration, request.Window.MovementThreshold);

        return new TrendResult
        {
            WindowName = request.Window.Name,
            VelocityAvailable = velocity.Count > 0,
            AccelerationAvailable = acceleration.Count > 0,
            Velocity = velocity,
            Acceleration = acceleration,
            Movements = movements,
            Suppressions = [],
            Coverage = request.ScaleModel.Dimensions.Count == 0
                ? 0.0
                : (double)velocity.Count / request.ScaleModel.Dimensions.Count,
            Support = populated.Length,
            Narrative = TrendNarrative.Describe(movements, request.Window.Name),
        };

        static TimeSpan elapsedBetween(SmoothedBucket from, SmoothedBucket to) =>
            to.Bucket.Start - from.Bucket.Start;

        static Dictionary<string, double> Step(SmoothedBucket from, SmoothedBucket to)
        {
            var elapsed = (to.Bucket.Start - from.Bucket.Start).TotalSeconds;
            var step = new Dictionary<string, double>(StringComparer.Ordinal);

            if (elapsed <= 0)
            {
                return step;
            }

            foreach (var (dimensionId, value) in to.Z)
            {
                // Only dimensions present in both readings can be differenced; a dimension
                // that appeared or vanished between buckets has no velocity, not an infinite one.
                if (from.Z.TryGetValue(dimensionId, out var prior))
                {
                    step[dimensionId] = (value - prior) / elapsed;
                }
            }

            return step;
        }
    }

    /// <summary>
    /// EWMAs each dimension across the populated buckets.
    /// </summary>
    /// <remarks>
    /// A dimension masked in a bucket carries forward its last smoothed value rather than
    /// dropping out. Resetting would make the next reading look like a jump from nothing, and
    /// treating the gap as zero would make it look like a collapse.
    /// </remarks>
    private static List<SmoothedBucket> Smooth(TrendRequest request, BehaviourBucket[] populated)
    {
        var smoothed = new List<SmoothedBucket>(populated.Length);
        Dictionary<string, double>? previous = null;

        for (var i = 0; i < populated.Length; i++)
        {
            var bucket = populated[i];
            var raw = Standardize(request, bucket);

            Dictionary<string, double> current;
            if (previous is null)
            {
                current = new Dictionary<string, double>(raw, StringComparer.Ordinal);
            }
            else
            {
                // A fresh dictionary per bucket, not a mutated shared one, every smoothed
                // reading has to stay the reading it was, or the whole series collapses to
                // its final value and every velocity becomes zero.
                current = new Dictionary<string, double>(previous, StringComparer.Ordinal);

                var elapsed = (bucket.Start - populated[i - 1].Start).TotalSeconds;
                var alpha = elapsed <= 0 ? 1.0 : 1.0 - Math.Exp(-elapsed / request.Window.SmoothingTau.TotalSeconds);

                foreach (var (dimensionId, value) in raw)
                {
                    current[dimensionId] = previous.TryGetValue(dimensionId, out var prior)
                        ? prior + (alpha * (value - prior))
                        : value;
                }
            }

            smoothed.Add(new SmoothedBucket(bucket, current));
            previous = current;
        }

        return smoothed;
    }

    private static Dictionary<string, double> Standardize(TrendRequest request, BehaviourBucket bucket)
    {
        var vector = bucket.FeatureVector(request.Now, request.Window.MinimumSamplesPerBucket);
        var z = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var sample in vector.Dimensions)
        {
            if (sample.Value is null
                || !request.ScaleModel.Dimensions.TryGetValue(sample.DimensionId, out var scale))
            {
                continue;
            }

            // Deliberately unclamped here. The clamp exists to stop one dimension dominating an
            // aggregate distance; velocity is supposed to show how far something moved.
            z[sample.DimensionId] = (sample.Value.Value - scale.Mean) / scale.Scale;
        }

        return z;
    }

    private static List<TrendMovement> BuildMovements(
        IReadOnlyDictionary<string, double> velocity,
        IReadOnlyDictionary<string, double> acceleration,
        double threshold)
    {
        var movements = new List<TrendMovement>();

        foreach (var (dimensionId, value) in velocity)
        {
            if (value <= threshold)
            {
                continue;
            }

            movements.Add(new TrendMovement
            {
                DimensionId = dimensionId,
                Label = TrendNarrative.LabelFor(dimensionId),
                VelocityPerSecond = value,
                AccelerationPerSecondSquared = acceleration.TryGetValue(dimensionId, out var a) ? a : 0.0,
            });
        }

        // Rate features lead: "recipient fan-out rising" is the headline, and the semantic
        // dimension that co-moved is the corroboration.
        return
        [
            .. movements
                .OrderByDescending(m => TrendNarrative.IsRateFeature(m.DimensionId))
                .ThenByDescending(m => m.VelocityPerSecond)
                .ThenBy(m => m.DimensionId, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// The stable token a suppression is reported by, in reasons and in evidence attributes.
    /// </summary>
    /// <remarks>
    /// One spelling, used everywhere. A narrative that says <c>regime_change</c> while the
    /// evidence says <c>RegimeChange</c> is two vocabularies for one fact, and a reviewer
    /// matching them up by hand is a reviewer who will eventually mismatch them.
    /// </remarks>
    public static string CodeFor(TrendSuppressionReason reason) => reason switch
    {
        TrendSuppressionReason.InsufficientSupport => "insufficient_support",
        TrendSuppressionReason.LongGap => "long_gap",
        TrendSuppressionReason.SparseBucket => "sparse_bucket",
        TrendSuppressionReason.RegimeChange => "regime_change",
        TrendSuppressionReason.DimensionSchemaChange => "dimension_schema_change",
        TrendSuppressionReason.BaselineUnavailable => "baseline_unavailable",
        _ => "none",
    };

    private sealed record SmoothedBucket(BehaviourBucket Bucket, IReadOnlyDictionary<string, double> Z);
}
