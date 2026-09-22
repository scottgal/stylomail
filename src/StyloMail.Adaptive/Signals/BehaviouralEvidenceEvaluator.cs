using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Scoring;
using StyloMail.Adaptive.Temporal;
using StyloMail.Core;

namespace StyloMail.Adaptive.Signals;

/// <summary>
/// Produces the behavioural evidence for one profile: drift, trend and fan-out.
/// </summary>
/// <remarks>
/// This is the pipeline's step five — compare against profiles and recent campaign windows,
/// compute drift and trend evidence — and it is the only place in this engine that asks what
/// time it is. Everything below it takes an explicit instant, so a replay can drive the whole
/// engine from a fixed clock and get the same answers twice.
///
/// <para>
/// The result is <see cref="Evidence"/> and nothing else. Deciding what to do about it belongs
/// to versioned deterministic policy, which is a different component with a different owner.
/// </para>
///
/// <para>
/// <b>Safe to share across threads.</b> It holds only an injected clock and an immutable options
/// record, and carries no per-call state. That is asserted by <c>SharedStateTests</c> rather than
/// assumed, so adding a field here fails the build with an explanation rather than corrupting
/// results under load. Unlike a profile, one evaluator instance may serve the whole host.
/// </para>
/// </remarks>
public sealed class BehaviouralEvidenceEvaluator
{
    private readonly TimeProvider _timeProvider;
    private readonly AdaptiveOptions _options;

    public BehaviouralEvidenceEvaluator(TimeProvider timeProvider, AdaptiveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        _options = options ?? new AdaptiveOptions();
    }

    public IReadOnlyList<Evidence> Evaluate(
        AdaptiveProfile profile,
        string observedScope,
        TrafficClassExpectation? trafficClass = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedScope);

        var now = _timeProvider.GetUtcNow();
        var scaleModel = profile.Baseline.ScaleModel ?? RobustScaleModel.Empty(_options.Scale);

        List<Evidence> evidence =
        [
            Drift(profile, scaleModel, observedScope, now),
            .. Trends(profile, scaleModel, observedScope, now),
        ];

        if (trafficClass is not null)
        {
            evidence.Add(FanOut(profile, observedScope, now, trafficClass));
        }

        return evidence;
    }

    private Evidence Drift(
        AdaptiveProfile profile,
        RobustScaleModel scaleModel,
        string observedScope,
        DateTimeOffset now)
    {
        var buckets = profile.Series.TryGetValue(_options.Burst.Name, out var burst)
            ? burst.Buckets
            : [];
        var latest = buckets.Count == 0 ? null : buckets[^1];

        // Nothing observed in this window means an empty probe, which compares nothing and
        // reports as unavailable rather than as a distance of zero.
        var probe = latest is null
            ? scaleModel.Standardize(DimensionVector.Create())
            : scaleModel.Standardize(latest.FeatureVector(now, _options.Burst.MinimumSamplesPerBucket));

        return BehaviouralEvidence.Drift(probe, observedScope, now);
    }

    private IEnumerable<Evidence> Trends(
        AdaptiveProfile profile,
        RobustScaleModel scaleModel,
        string observedScope,
        DateTimeOffset now)
    {
        foreach (var window in new[] { _options.Burst, _options.Slow })
        {
            if (!profile.Series.TryGetValue(window.Name, out var series))
            {
                continue;
            }

            // Buckets recorded before the regime changed were observed under a different
            // baseline. While any of them are still inside the window, nothing may be
            // differenced against them.
            //
            // Checked against the window's grid rather than the whole series: a bucket from
            // last week does not poison today's comparison, and treating it as though it did
            // would suppress derivative evidence forever after any regime change.
            var priorRegime = profile.CurrentRegimeId;
            if (profile.RegimeChangedAt is { } changed
                && series.Window(window, now).Any(bucket => bucket.SampleCount > 0 && bucket.Start < changed))
            {
                priorRegime = profile.PreviousRegimeId ?? profile.CurrentRegimeId;
            }

            var trend = TrendAnalyzer.Analyze(new TrendRequest
            {
                Series = series,
                Window = window,
                ScaleModel = scaleModel,
                Now = now,
                DimensionSchemaVersion = _options.DimensionSchemaVersion,
                PriorDimensionSchemaVersion = _options.DimensionSchemaVersion,
                RegimeId = profile.CurrentRegimeId,
                PriorRegimeId = priorRegime,
            });

            yield return BehaviouralEvidence.Velocity(trend, observedScope, now);
            yield return BehaviouralEvidence.Acceleration(trend, observedScope, now);
        }
    }

    private Evidence FanOut(
        AdaptiveProfile profile,
        string observedScope,
        DateTimeOffset now,
        TrafficClassExpectation trafficClass)
    {
        var series = profile.Series.TryGetValue(_options.Burst.Name, out var burst) ? burst : null;
        var buckets = series?.Buckets ?? [];
        var observed = buckets.Count == 0 ? 0.0 : buckets[^1].RecipientsPerSecondAt(now);

        return BehaviouralEvidence.FanOut(
            FanOutEvaluator.Evaluate(observed, buckets.Count, trafficClass),
            observedScope,
            now);
    }
}
