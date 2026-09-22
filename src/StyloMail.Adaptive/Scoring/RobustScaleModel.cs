using StyloMail.Adaptive.Profiles;

namespace StyloMail.Adaptive.Scoring;

/// <summary>Tuning for robust standardization.</summary>
public sealed record RobustScaleOptions
{
    /// <summary>
    /// Smallest scale a dimension may have. A dimension that has never varied would otherwise
    /// divide by zero and turn an ordinary value into an infinite anomaly; the floor keeps
    /// "no spread observed yet" from reading as "anything is a huge deviation".
    /// </summary>
    public double VarianceFloor { get; init; } = 0.05;

    /// <summary>
    /// Cap on a single dimension's standardized distance. Without it one wild dimension
    /// dominates the aggregate and the score stops describing the message.
    /// </summary>
    public double MaxAbsoluteZ { get; init; } = 6.0;

    /// <summary>
    /// Trusted samples required before a dimension may be compared at all. Below this the
    /// dimension is unmodelled, unknown, which is not the same as normal.
    /// </summary>
    public int MinimumSupport { get; init; } = 3;
}

/// <summary>The trusted distribution of one dimension.</summary>
public sealed record DimensionScale
{
    public required string DimensionId { get; init; }

    public required double Mean { get; init; }

    public required double Variance { get; init; }

    /// <summary>Spread actually used for standardization, the variance's root, floored.</summary>
    public required double Scale { get; init; }

    public required int Support { get; init; }
}

/// <summary>
/// The outcome of comparing a probe vector against a trusted baseline.
/// </summary>
/// <remarks>
/// <see cref="Distance"/> is <see langword="null"/> when nothing could be compared. That is a
/// distinct outcome from a distance of zero, which means "compared, and unremarkable", the
/// difference between an outage and a calm message.
/// </remarks>
public sealed record StandardizedProbe
{
    /// <summary>Per-dimension standardized departure, clamped. Masked dimensions are absent.</summary>
    public required IReadOnlyDictionary<string, double> Z { get; init; }

    /// <summary>Probe dimensions that produced no value.</summary>
    public required IReadOnlyList<string> MaskedDimensionIds { get; init; }

    /// <summary>Probe dimensions with a value but no trusted distribution to compare against.</summary>
    public required IReadOnlyList<string> UnmodelledDimensionIds { get; init; }

    /// <summary>
    /// RMS of the per-dimension departures, or <see langword="null"/> when nothing was compared.
    /// </summary>
    /// <remarks>
    /// An RMS rather than a sum, so a message that happens to carry more evidence does not
    /// score as more anomalous merely for having wider coverage.
    /// </remarks>
    public required double? Distance { get; init; }

    /// <summary>Compared dimensions as a fraction of the probe's dimensions.</summary>
    public required double Coverage { get; init; }

    public required int ComparedDimensionCount { get; init; }

    /// <summary>True when at least one dimension could actually be compared.</summary>
    public bool HasComparison => ComparedDimensionCount > 0;
}

/// <summary>
/// Diagonal robust standardization against a trusted baseline.
/// </summary>
/// <remarks>
/// Diagonal variance is the starting point, not the destination: it needs roughly an order of
/// magnitude less trusted support than a full covariance estimate, and a covariance fitted on
/// a handful of samples would encode noise as structure. Full Mahalanobis scoring is a later
/// option once support is genuinely adequate.
/// </remarks>
public sealed class RobustScaleModel
{
    private readonly Dictionary<string, DimensionScale> _dimensions;

    private RobustScaleModel(IReadOnlyDictionary<string, DimensionScale> dimensions, RobustScaleOptions options)
    {
        _dimensions = new Dictionary<string, DimensionScale>(dimensions, StringComparer.Ordinal);
        Options = options;
    }

    public RobustScaleOptions Options { get; }

    public IReadOnlyDictionary<string, DimensionScale> Dimensions => _dimensions;

    /// <summary>A model that knows nothing. Every dimension is unmodelled against it.</summary>
    public static RobustScaleModel Empty(RobustScaleOptions? options = null) =>
        new(new Dictionary<string, DimensionScale>(StringComparer.Ordinal), options ?? new RobustScaleOptions());

    /// <summary>
    /// Rebuilds a model from already-derived scales, e.g. one restored from a stored snapshot.
    /// </summary>
    public static RobustScaleModel FromScales(
        IEnumerable<DimensionScale> scales,
        RobustScaleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scales);

        var effective = options ?? new RobustScaleOptions();
        var dimensions = scales.ToDictionary(scale => scale.DimensionId, StringComparer.Ordinal);

        return new RobustScaleModel(dimensions, effective);
    }

    public static RobustScaleModel FromMoments(
        IEnumerable<KeyValuePair<string, RunningMoments>> moments,
        RobustScaleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(moments);

        var effective = options ?? new RobustScaleOptions();
        var scales = new Dictionary<string, DimensionScale>(StringComparer.Ordinal);

        foreach (var (dimensionId, value) in moments)
        {
            if (value.Count < effective.MinimumSupport)
            {
                continue;
            }

            var variance = value.Variance;
            scales[dimensionId] = new DimensionScale
            {
                DimensionId = dimensionId,
                Mean = value.Mean,
                Variance = variance,
                Scale = Math.Max(Math.Sqrt(variance), effective.VarianceFloor),
                Support = value.Count,
            };
        }

        return new RobustScaleModel(scales, effective);
    }

    /// <summary>
    /// Standardizes a probe against this baseline, masking anything it cannot compare.
    /// </summary>
    public StandardizedProbe Standardize(DimensionVector probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var z = new Dictionary<string, double>(StringComparer.Ordinal);
        var unmodelled = new List<string>();

        foreach (var sample in probe.Dimensions)
        {
            if (sample.Value is null)
            {
                // Masked: deliberately not compared, and deliberately not substituted with
                // zero, the mean, or anything else that would invent an observation.
                continue;
            }

            if (!_dimensions.TryGetValue(sample.DimensionId, out var scale))
            {
                unmodelled.Add(sample.DimensionId);
                continue;
            }

            var standardized = (sample.Value.Value - scale.Mean) / scale.Scale;
            z[sample.DimensionId] = Math.Clamp(standardized, -Options.MaxAbsoluteZ, Options.MaxAbsoluteZ);
        }

        return new StandardizedProbe
        {
            Z = z,
            MaskedDimensionIds = probe.MaskedDimensionIds,
            UnmodelledDimensionIds = unmodelled,
            Distance = z.Count == 0 ? null : Math.Sqrt(z.Values.Sum(value => value * value) / z.Count),
            Coverage = probe.Dimensions.Count == 0 ? 0.0 : (double)z.Count / probe.Dimensions.Count,
            ComparedDimensionCount = z.Count,
        };
    }
}
