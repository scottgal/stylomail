using StyloMail.Core;

namespace StyloMail.Adaptive.Temporal;

/// <summary>
/// What fan-out is normal for a traffic class.
/// </summary>
/// <remarks>
/// A newsletter sending five hundred copies and a compromised account sending five hundred
/// lures produce identical arithmetic. The only thing that separates them is the class the
/// traffic was declared in, which is why the class is a first-class input here rather than a
/// threshold that quietly encodes one class's expectations for all of them.
/// </remarks>
public sealed record TrafficClassExpectation
{
    public required string TrafficClass { get; init; }

    /// <summary>Recipient rate this class is expected to sustain.</summary>
    public required double ExpectedRecipientsPerSecond { get; init; }

    /// <summary>How many times the expectation is still unremarkable. A multiple, not a ceiling.</summary>
    public required double NoveltyTolerance { get; init; }

    /// <summary>Observations needed before this class will say anything at all.</summary>
    public required int MinimumSupport { get; init; }
}

/// <summary>The fan-out verdict for one traffic class.</summary>
public sealed record FanOutEvidence
{
    public required string TrafficClass { get; init; }

    public required EvidenceAvailability Availability { get; init; }

    /// <summary>Observed rate as a multiple of the class expectation. <see langword="null"/> when unknown.</summary>
    public required double? Ratio { get; init; }

    public required bool IsNovelFanOut { get; init; }

    public required int SampleSupport { get; init; }

    public required string? Reason { get; init; }
}

/// <summary>
/// Compares an observed recipient rate against what the traffic class leads you to expect.
/// </summary>
public static class FanOutEvaluator
{
    public static FanOutEvidence Evaluate(
        double observedRecipientsPerSecond,
        int support,
        TrafficClassExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentOutOfRangeException.ThrowIfNegative(observedRecipientsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectation.ExpectedRecipientsPerSecond);

        if (support < expectation.MinimumSupport)
        {
            // Below the support floor the honest answer is "not yet known". Reporting calm
            // here would let a burst buy itself a clean reading simply by being the first
            // thing the class ever saw.
            return new FanOutEvidence
            {
                TrafficClass = expectation.TrafficClass,
                Availability = EvidenceAvailability.Unavailable,
                Ratio = null,
                IsNovelFanOut = false,
                SampleSupport = support,
                Reason = "insufficient_support",
            };
        }

        var ratio = observedRecipientsPerSecond / expectation.ExpectedRecipientsPerSecond;
        var novel = ratio > expectation.NoveltyTolerance;

        return new FanOutEvidence
        {
            TrafficClass = expectation.TrafficClass,
            Availability = EvidenceAvailability.Available,
            Ratio = ratio,
            IsNovelFanOut = novel,
            SampleSupport = support,
            Reason = novel
                ? $"recipient fan-out {ratio:0.##}x the {expectation.TrafficClass} expectation"
                : $"recipient fan-out within the {expectation.TrafficClass} expectation",
        };
    }
}
