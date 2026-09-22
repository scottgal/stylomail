using StyloMail.Adaptive.Learning;
using StyloMail.Adaptive.Scoring;
using StyloMail.Adaptive.Temporal;

namespace StyloMail.Adaptive;

/// <summary>
/// Tuning for the adaptive engine.
/// </summary>
/// <remarks>
/// The defaults here are engineering defaults, not tuned values. Baseline half-lives and
/// thresholds are supposed to come from representative replay data, and until that exists these
/// are placeholders that should be treated as unvalidated.
/// </remarks>
public sealed record AdaptiveOptions
{
    public RobustScaleOptions Scale { get; init; } = new();

    /// <summary>Burst window: catches a rapid change in what a principal is doing right now.</summary>
    public TrendWindow Burst { get; init; } = new()
    {
        Name = "burst",
        BucketWidth = TimeSpan.FromMinutes(1),
        BucketCount = 10,
        SmoothingTau = TimeSpan.FromMinutes(2),
        MinimumSamplesPerBucket = 1,
        MaxGap = TimeSpan.FromMinutes(5),
    };

    /// <summary>Slow window: catches a drift that no single minute reveals.</summary>
    public TrendWindow Slow { get; init; } = new()
    {
        Name = "slow",
        BucketWidth = TimeSpan.FromHours(1),
        BucketCount = 24,
        SmoothingTau = TimeSpan.FromHours(6),
        MinimumSamplesPerBucket = 2,
        MaxGap = TimeSpan.FromHours(4),
    };

    /// <summary>Version of the dimension set. A change makes old and new readings incomparable.</summary>
    public string DimensionSchemaVersion { get; init; } = "adaptive-dimensions/1";

    /// <summary>Fast trend time constant. Short enough to notice a change within minutes.</summary>
    public TimeSpan FastTau { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Slow trend time constant. Long enough that a single unusual day barely moves it.</summary>
    public TimeSpan SlowTau { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Largest weight one observation may have on a trend average.</summary>
    public double MaxEventContribution { get; init; } = 0.25;

    /// <summary>
    /// Largest distance, in units of a dimension's own spread, that one approved sample may
    /// move the trusted baseline's mean.
    /// </summary>
    /// <remarks>
    /// Slows the baseline down without freezing it. A single label is a claim; a baseline that
    /// a single claim can drag is one an attacker only has to convince once. Sustained change
    /// converges; a one-off spike does not move anything.
    /// </remarks>
    public double MaxBaselineShiftPerUpdate { get; init; } = 0.25;

    /// <summary>Trusted samples a new regime needs before it may replace the baseline.</summary>
    public int RegimePromotionMinimumSupport { get; init; } = 12;

    /// <summary>Consecutive samples whose mean movement is checked for stability.</summary>
    public int RegimeStabilitySamples { get; init; } = 3;

    /// <summary>Largest mean movement between consecutive samples that still counts as settled.</summary>
    public double RegimeStabilityTolerance { get; init; } = 0.03;

    public EwmaOptions FastEwma => new() { Tau = FastTau, MaxEventContribution = MaxEventContribution };

    public EwmaOptions SlowEwma => new() { Tau = SlowTau, MaxEventContribution = MaxEventContribution };
}
