using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Signals;
using StyloMail.Adaptive.Temporal;
using StyloMail.Core;

namespace StyloMail.Adaptive.Tests;

public class BehaviouralEvidenceEvaluatorTests
{
    private readonly TestClock _clock = TestClock.AtEpoch();
    private readonly DateTimeOffset _start;

    public BehaviouralEvidenceEvaluatorTests() => _start = _clock.GetUtcNow();

    [Fact]
    public void AColdProfileProducesUnknownNotZero()
    {
        var evidence = Evaluator().Evaluate(Profile(), "sender");

        var drift = Single(evidence, BehaviouralEvidenceIds.DriftDistance);
        var velocity = Trend(evidence, BehaviouralEvidenceIds.Velocity);

        // Nothing has been trusted and nothing has been compared, so the honest answer is
        // "unknown". A zero here would read as "measured, and perfectly normal".
        Assert.Equal(EvidenceAvailability.Unavailable, drift.Availability);
        Assert.Null(drift.Value);
        Assert.Equal(EvidenceAvailability.Unavailable, velocity.Availability);
        Assert.Null(velocity.Value);
        Assert.Contains("suppressed", velocity.Value(BehaviouralEvidence.NarrativeAttribute), StringComparison.Ordinal);
    }

    [Fact]
    public void TheInjectedClockDecidesWhichBucketsAreInTheWindow()
    {
        var profile = Established();
        for (var i = 0; i < 3; i++)
        {
            profile.Observe(Observation(_start.AddMinutes(i), 0.05 + (i * 0.15)));
        }

        _clock.Advance(TimeSpan.FromMinutes(3));
        var current = Trend(Evaluator().Evaluate(profile, "sender"), BehaviouralEvidenceIds.Velocity);
        Assert.Equal(EvidenceAvailability.Available, current.Availability);

        // Same profile, same observations, clock moved on: those buckets are now hours in the
        // past and say nothing about the present. The clock is the only thing that changed.
        _clock.Advance(TimeSpan.FromHours(2));
        var stale = Trend(Evaluator().Evaluate(profile, "sender"), BehaviouralEvidenceIds.Velocity);

        Assert.Equal(EvidenceAvailability.Unavailable, stale.Availability);
        Assert.Null(stale.Value);
    }

    [Fact]
    public void ANovelFanOutIsReportedWithAReason()
    {
        var profile = Established();
        for (var i = 0; i < 3; i++)
        {
            profile.Observe(Observation(_start.AddMinutes(i), 0.05 + (i * 0.15), recipients: 60));
        }

        _clock.Advance(TimeSpan.FromMinutes(3));
        var evidence = Evaluator().Evaluate(
            profile,
            "relationship",
            new TrafficClassExpectation
            {
                TrafficClass = "conversational",
                ExpectedRecipientsPerSecond = 0.05,
                NoveltyTolerance = 3.0,
                MinimumSupport = 1,
            });

        var fanOut = Single(evidence, BehaviouralEvidenceIds.FanOutNovel);

        Assert.Equal(EvidenceAvailability.Available, fanOut.Availability);
        Assert.True(fanOut.Value > 3.0);
        Assert.Equal("false", fanOut.Value(BehaviouralEvidence.AloneSufficientAttribute));
        Assert.Contains("conversational", fanOut.Value(BehaviouralEvidence.NarrativeAttribute), StringComparison.Ordinal);
    }

    [Fact]
    public void EverySignalIsBehaviouralAndNoneIsAVerdict()
    {
        var evidence = Evaluator().Evaluate(Profile(), "sender");

        Assert.NotEmpty(evidence);
        Assert.All(evidence, e => Assert.Equal(EvidenceOrigin.Behavioural, e.Origin));
        Assert.All(evidence, e => Assert.Equal("false", e.Value(BehaviouralEvidence.AloneSufficientAttribute)));
        Assert.All(evidence, e => Assert.Equal(BehaviouralEvidence.SourceVersion, e.SourceVersion));

        // Two windows, each contributing velocity and acceleration, plus drift.
        Assert.Equal(2, evidence.Count(e => e.SignalId == BehaviouralEvidenceIds.Velocity));
        Assert.Equal(2, evidence.Count(e => e.SignalId == BehaviouralEvidenceIds.Acceleration));
    }

    [Fact]
    public void ARegimeChangeSuppressesDerivativesUntilTheWindowClears()
    {
        var profile = Established();
        for (var i = 0; i < 3; i++)
        {
            profile.Observe(Observation(_start.AddMinutes(i), 0.05 + (i * 0.15)));
        }

        profile.BeginRegime("regime-b");
        for (var i = 0; i < 12; i++)
        {
            profile.Promote(Sample(_start.AddMinutes(3), 0.9));
        }

        Assert.True(profile.PromoteRegime());

        _clock.Advance(TimeSpan.FromMinutes(3));
        var during = Trend(Evaluator().Evaluate(profile, "sender"), BehaviouralEvidenceIds.Velocity);

        // The window still contains buckets observed under the old regime, so differencing
        // them against the new baseline would be arithmetic on two different things.
        Assert.Equal(EvidenceAvailability.Unavailable, during.Availability);
        Assert.Contains("regime_change", during.Values(BehaviouralEvidence.SuppressionAttribute));

        for (var i = 10; i < 13; i++)
        {
            profile.Observe(Observation(_start.AddMinutes(i), 0.9));
        }

        _clock.Set(_start.AddMinutes(13));
        var after = Trend(Evaluator().Evaluate(profile, "sender"), BehaviouralEvidenceIds.Velocity);

        Assert.Equal(EvidenceAvailability.Available, after.Availability);
    }

    private BehaviouralEvidenceEvaluator Evaluator() => new(_clock);

    private AdaptiveProfile Established()
    {
        var profile = Profile();
        foreach (var value in new[] { 0.05, 0.06, 0.04, 0.05, 0.05 })
        {
            profile.Promote(Sample(_start, value));
        }

        return profile;
    }

    private static AdaptiveProfile Profile() => new(new ProfileKey
    {
        TenantId = "tenant-a",
        Scope = ProfileScopeKind.OutboundSender,
        Key = "sender",
        Direction = MailDirection.Outbound,
    });

    private static ProfileObservation Observation(
        DateTimeOffset at,
        double value,
        int recipients = 1) => new()
    {
        ObservedAt = at,
        RecipientCount = recipients,
        WasRejected = false,
        Dimensions = DimensionVector.Create(DimensionSample.Available("semantic.payment_redirection", value)),
    };

    private static TrustedSample Sample(DateTimeOffset at, double value) => new()
    {
        RecordedAt = at,
        Provenance = LabelProvenance.AuthenticatedOperator,
        Dimensions = DimensionVector.Create(DimensionSample.Available("semantic.payment_redirection", value)),
    };

    private static Evidence Single(IReadOnlyList<Evidence> evidence, string signalId) =>
        Assert.Single(evidence, e => e.SignalId == signalId);

    /// <summary>The burst-window reading. Every trend signal is emitted once per window.</summary>
    private static Evidence Trend(IReadOnlyList<Evidence> evidence, string signalId) =>
        Assert.Single(evidence, e =>
            e.SignalId == signalId
            && e.Value("window") == "burst");
}
