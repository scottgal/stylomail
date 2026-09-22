using System.Globalization;
using StyloMail.Adaptive.Scoring;
using StyloMail.Adaptive.Temporal;
using StyloMail.Core;

namespace StyloMail.Adaptive.Signals;

/// <summary>Signal ids this engine produces.</summary>
public static class BehaviouralEvidenceIds
{
    public const string FanOutNovel = "behavioural.fanout.novel";
    public const string DriftDistance = "behavioural.drift.distance";
    public const string Velocity = "behavioural.trend.velocity";
    public const string Acceleration = "behavioural.trend.acceleration";
}

/// <summary>
/// Turns adaptive findings into Core evidence.
/// </summary>
/// <remarks>
/// Everything here is <see cref="Evidence"/>. Nothing here is an action, and nothing here can
/// become one: policy is a separate, versioned component that reads evidence and decides.
/// Keeping that boundary in the type system rather than in a convention is what stops a noisy
/// acceleration reading from ending up as a block.
/// </remarks>
public static class BehaviouralEvidence
{
    /// <summary>
    /// Attribute name marking whether a signal may justify a hard block on its own.
    /// </summary>
    /// <remarks>
    /// Every behavioural signal sets this to <c>false</c>. Behavioural evidence is a hint that
    /// something changed, and change is not the same as malice — a sender that suddenly doubles
    /// its volume may have been acquired, won a contract, or been compromised, and only policy
    /// with more context can tell those apart.
    /// </remarks>
    public const string AloneSufficientAttribute = "alone_sufficient";

    /// <summary>Attribute carrying the reason-shaped description.</summary>
    public const string NarrativeAttribute = "narrative";

    /// <summary>Attribute carrying how much of the vector was actually compared.</summary>
    public const string CoverageAttribute = "coverage";

    /// <summary>Repeated once per suppressed derivative; a window can be suppressed for several reasons.</summary>
    public const string SuppressionAttribute = "suppression";

    /// <summary>Repeated once per dimension that produced no value, so the count survives.</summary>
    public const string MaskedDimensionAttribute = "masked_dimension";

    /// <summary>Producer version stamped onto everything this engine emits.</summary>
    public const string SourceVersion = "adaptive/1";

    public static Evidence FanOut(FanOutEvidence fanOut, string scope, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(fanOut);

        List<EvidenceAttribute> attributes =
        [
            Attribute(AloneSufficientAttribute, "false"),
            Attribute("traffic_class", fanOut.TrafficClass),
        ];

        if (fanOut.Reason is not null)
        {
            attributes.Add(Attribute(NarrativeAttribute, fanOut.Reason));
        }

        return new Evidence
        {
            SignalId = BehaviouralEvidenceIds.FanOutNovel,
            Origin = EvidenceOrigin.Behavioural,
            Availability = fanOut.Availability,
            Value = fanOut.Ratio,
            SampleSupport = fanOut.SampleSupport,
            SourceVersion = SourceVersion,
            ObservedAt = observedAt,
            ObservedScope = scope,
            Attributes = attributes,
        };
    }

    public static Evidence Drift(StandardizedProbe probe, string scope, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(probe);

        List<EvidenceAttribute> attributes =
        [
            Attribute(AloneSufficientAttribute, "false"),
            Attribute(CoverageAttribute, Format(probe.Coverage)),
            Attribute("compared_dimensions", probe.ComparedDimensionCount.ToString(CultureInfo.InvariantCulture)),
            // One entry per masked dimension rather than a joined string: a count of five
            // reasons is not the same fact as a single reason called "a,b,c,d,e".
            .. probe.MaskedDimensionIds.Select(id => Attribute(MaskedDimensionAttribute, id)),
        ];

        return new Evidence
        {
            SignalId = BehaviouralEvidenceIds.DriftDistance,
            Origin = EvidenceOrigin.Behavioural,
            // A probe that compared nothing is unknown, not a distance of zero. This is the
            // difference between a cold-start profile and a well-behaved message.
            Availability = probe.HasComparison
                ? EvidenceAvailability.Available
                : EvidenceAvailability.Unavailable,
            Value = probe.Distance,
            SampleSupport = probe.ComparedDimensionCount,
            SourceVersion = SourceVersion,
            ObservedAt = observedAt,
            ObservedScope = scope,
            Attributes = attributes,
        };
    }

    public static Evidence Velocity(TrendResult trend, string scope, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(trend);

        return Trend(
            trend,
            BehaviouralEvidenceIds.Velocity,
            trend.VelocityAvailable,
            Magnitude(trend.Velocity),
            scope,
            observedAt);
    }

    public static Evidence Acceleration(TrendResult trend, string scope, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(trend);

        return Trend(
            trend,
            BehaviouralEvidenceIds.Acceleration,
            trend.AccelerationAvailable,
            Magnitude(trend.Acceleration),
            scope,
            observedAt);
    }

    private static Evidence Trend(
        TrendResult trend,
        string signalId,
        bool available,
        double magnitude,
        string scope,
        DateTimeOffset observedAt)
    {
        List<EvidenceAttribute> attributes =
        [
            Attribute(AloneSufficientAttribute, "false"),
            Attribute(NarrativeAttribute, trend.Narrative),
            Attribute(CoverageAttribute, Format(trend.Coverage)),
            Attribute("window", trend.WindowName),
            Attribute("support", trend.Support.ToString(CultureInfo.InvariantCulture)),
            .. trend.Suppressions.Select(reason =>
                Attribute(SuppressionAttribute, TrendAnalyzer.CodeFor(reason))),
        ];

        return new Evidence
        {
            SignalId = signalId,
            Origin = EvidenceOrigin.Behavioural,
            Availability = available ? EvidenceAvailability.Available : EvidenceAvailability.Unavailable,
            Value = available ? magnitude : null,
            SampleSupport = trend.Support,
            SourceVersion = SourceVersion,
            ObservedAt = observedAt,
            ObservedScope = scope,
            Attributes = attributes,
        };
    }

    private static double Magnitude(IReadOnlyDictionary<string, double> velocity) =>
        velocity.Count == 0 ? 0.0 : velocity.Values.Select(Math.Abs).Max();

    private static EvidenceAttribute Attribute(string name, string value) =>
        new() { Name = name, Value = value };

    private static string Format(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
