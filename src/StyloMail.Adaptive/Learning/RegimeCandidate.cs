using StyloMail.Adaptive.Profiles;

namespace StyloMail.Adaptive.Learning;

/// <summary>
/// A behaviour pattern that might be the new normal, not yet trusted.
/// </summary>
/// <remarks>
/// A regime change is the honest way to absorb a genuine shift — a company rebrands, a mailing
/// list doubles, a sender's business changes shape. Learning it by dragging the existing
/// baseline is how an attacker gets their behaviour adopted: they only have to send enough
/// mail in the new shape and the baseline follows. A candidate must instead earn support from
/// trusted labels <em>and</em> settle down before it replaces anything.
/// </remarks>
public sealed class RegimeCandidate
{
    private readonly Queue<double> _recentMeanShifts = new();
    private readonly AdaptiveOptions _options;

    internal RegimeCandidate(string regimeId, TrustedBaseline provisional, AdaptiveOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regimeId);

        RegimeId = regimeId;
        Provisional = provisional;
        _options = options;
    }

    public string RegimeId { get; }

    public int TrustedSupport => Provisional.TrustedSupport;

    /// <summary>What the baseline would become if this candidate were promoted.</summary>
    public TrustedBaseline Provisional { get; private set; }

    /// <summary>True when the candidate's own mean has stopped moving.</summary>
    public bool IsStable =>
        _recentMeanShifts.Count >= _options.RegimeStabilitySamples
        && _recentMeanShifts.All(shift => shift <= _options.RegimeStabilityTolerance);

    /// <summary>True when the candidate has both the support and the stability to replace the baseline.</summary>
    public bool IsPromotable =>
        IsStable && TrustedSupport >= _options.RegimePromotionMinimumSupport;

    internal void Add(TrustedSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var before = Provisional.Dimensions;
        Provisional = Provisional.WithSample(sample, sample.RecordedAt).WithRebuiltScale(_options.Scale);

        _recentMeanShifts.Enqueue(MeanShift(before, Provisional.Dimensions));
        while (_recentMeanShifts.Count > _options.RegimeStabilitySamples)
        {
            _recentMeanShifts.Dequeue();
        }
    }

    /// <summary>
    /// Mean absolute movement across the dimensions the candidate knows about.
    /// </summary>
    /// <remarks>
    /// A candidate still swinging wildly has not seen enough to define anything, however much
    /// support it has accumulated.
    /// </remarks>
    private static double MeanShift(
        IReadOnlyDictionary<string, Scoring.RunningMoments> before,
        IReadOnlyDictionary<string, Scoring.RunningMoments> after)
    {
        double total = 0;
        var count = 0;

        foreach (var (dimensionId, moments) in after)
        {
            if (before.TryGetValue(dimensionId, out var prior))
            {
                total += Math.Abs(moments.Mean - prior.Mean);
                count++;
            }
        }

        return count == 0 ? double.MaxValue : total / count;
    }
}
