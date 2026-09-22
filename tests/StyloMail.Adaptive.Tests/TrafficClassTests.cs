using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Signals;
using StyloMail.Adaptive.Temporal;
using StyloMail.Core;
using System.Reflection;

namespace StyloMail.Adaptive.Tests;

public class FanOutEvaluatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AScheduledBurstIsNotNovelFanOutUnderItsOwnTrafficClass()
    {
        var evidence = FanOutEvaluator.Evaluate(
            observedRecipientsPerSecond: 8.0,
            support: 20,
            ScheduledBulk());

        Assert.Equal(EvidenceAvailability.Available, evidence.Availability);
        Assert.False(evidence.IsNovelFanOut);
        Assert.Equal(1.0, evidence.Ratio!.Value, 6);
    }

    [Fact]
    public void TheSameTrafficIsNovelFanOutUnderAConversationalClass()
    {
        // Identical numbers, different class, opposite conclusion — which is the whole point
        // of carrying a traffic class rather than one global notion of "too many recipients".
        var evidence = FanOutEvaluator.Evaluate(
            observedRecipientsPerSecond: 8.0,
            support: 20,
            Conversational());

        Assert.True(evidence.IsNovelFanOut);
        Assert.Equal(160.0, evidence.Ratio!.Value, 6);
    }

    [Fact]
    public void FanOutBelowTheSupportFloorIsUnknownNotReassuring()
    {
        var evidence = FanOutEvaluator.Evaluate(
            observedRecipientsPerSecond: 8.0,
            support: 1,
            ScheduledBulk());

        Assert.Equal(EvidenceAvailability.Unavailable, evidence.Availability);
        Assert.Null(evidence.Ratio);
        Assert.False(evidence.IsNovelFanOut);
        Assert.Equal("insufficient_support", evidence.Reason);
    }

    [Fact]
    public void TheVerdictLooksAtBothToleranceAndSupport()
    {
        var evidence = FanOutEvaluator.Evaluate(
            observedRecipientsPerSecond: 25.0,
            support: 20,
            ScheduledBulk());

        // 3.1x the class expectation, past the 3x tolerance.
        Assert.True(evidence.IsNovelFanOut);
        Assert.Equal(3.125, evidence.Ratio!.Value, 6);
    }

    [Fact]
    public void QuotasAreBoundByObservationNotByTrafficClass()
    {
        var profile = new AdaptiveProfile(new ProfileKey
        {
            TenantId = "tenant-a",
            Scope = ProfileScopeKind.OutboundSender,
            Key = "sender",
            Direction = MailDirection.Outbound,
        });

        // A legitimate newsletter: the same 500 recipients the abusive sender sends to.
        for (var i = 0; i < 10; i++)
        {
            profile.Observe(new ProfileObservation
            {
                ObservedAt = Start.AddSeconds(i),
                RecipientCount = 50,
                WasRejected = false,
            });
        }

        var newsletter = FanOutEvaluator.Evaluate(8.0, 10, ScheduledBulk());
        var abuse = FanOutEvaluator.Evaluate(8.0, 10, Conversational());

        Assert.False(newsletter.IsNovelFanOut);
        Assert.True(abuse.IsNovelFanOut);

        // Classification changes the verdict, never the accounting. A traffic class that
        // excused a sender from the quota would be an allowlist with extra steps.
        Assert.Equal(500, profile.Observed.Recipients);
        Assert.Equal(10, profile.Observed.Attempts);
        Assert.Equal(0, profile.Observed.RejectedAttempts);
    }

    private static TrafficClassExpectation ScheduledBulk() => new()
    {
        TrafficClass = "scheduled-bulk",
        ExpectedRecipientsPerSecond = 8.0,
        NoveltyTolerance = 3.0,
        MinimumSupport = 5,
    };

    private static TrafficClassExpectation Conversational() => new()
    {
        TrafficClass = "conversational",
        ExpectedRecipientsPerSecond = 0.05,
        NoveltyTolerance = 3.0,
        MinimumSupport = 5,
    };
}

public class BehaviouralEvidenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FanOutNoveltyIsBehaviouralEvidenceCarryingItsReason()
    {
        var evidence = BehaviouralEvidence.FanOut(
            new FanOutEvidence
            {
                TrafficClass = "conversational",
                Availability = EvidenceAvailability.Available,
                Ratio = 160.0,
                IsNovelFanOut = true,
                SampleSupport = 20,
                Reason = "recipient fan-out 160x the conversational expectation",
            },
            scope: "relationship",
            observedAt: Start);

        Assert.Equal(EvidenceOrigin.Behavioural, evidence.Origin);
        Assert.Equal(BehaviouralEvidenceIds.FanOutNovel, evidence.SignalId);
        Assert.Equal(160.0, evidence.Value);
        Assert.Equal(20, evidence.SampleSupport);
        Assert.Equal("relationship", evidence.ObservedScope);
    }

    [Fact]
    public void SuppressedTrendEvidenceIsUnavailableRatherThanZero()
    {
        var trend = new TrendResult
        {
            WindowName = "burst",
            VelocityAvailable = false,
            AccelerationAvailable = false,
            Velocity = new Dictionary<string, double>(),
            Acceleration = new Dictionary<string, double>(),
            Movements = [],
            Suppressions = [TrendSuppressionReason.SparseBucket],
            Coverage = 0.0,
            Support = 1,
            Narrative = "derivative evidence suppressed (sparse_bucket)",
        };

        var velocity = BehaviouralEvidence.Velocity(trend, "sender", Start);
        var acceleration = BehaviouralEvidence.Acceleration(trend, "sender", Start);

        // Zero would read as "measured, and flat" — the opposite of "we could not tell".
        Assert.Equal(EvidenceAvailability.Unavailable, velocity.Availability);
        Assert.Null(velocity.Value);
        Assert.Equal(EvidenceAvailability.Unavailable, acceleration.Availability);
        Assert.Null(acceleration.Value);
    }

    [Fact]
    public void AccelerationEvidenceIsMarkedAsNeverSufficientOnItsOwn()
    {
        var trend = RisingTrend();

        var acceleration = BehaviouralEvidence.Acceleration(trend, "sender", Start);

        Assert.Equal(EvidenceAvailability.Available, acceleration.Availability);
        Assert.True(acceleration.Value > 0);

        // The signal carries its own caveat so a policy author cannot read it as a verdict.
        Assert.Equal(
            "false",
            acceleration.Value(BehaviouralEvidence.AloneSufficientAttribute));
    }

    [Fact]
    public void TrendEvidenceCarriesTheNarrativeAndCoverage()
    {
        var trend = RisingTrend();

        var velocity = BehaviouralEvidence.Velocity(trend, "sender", Start);

        Assert.Equal(
            "recipient fan-out rising while payment-redirection evidence also rises",
            velocity.Value(BehaviouralEvidence.NarrativeAttribute));
        Assert.Equal("false", velocity.Value(BehaviouralEvidence.AloneSufficientAttribute));
        Assert.Single(velocity.Values(BehaviouralEvidence.CoverageAttribute));
    }

    [Fact]
    public void DriftWithoutAComparisonIsUnavailable()
    {
        var evidence = BehaviouralEvidence.Drift(
            new StyloMail.Adaptive.Scoring.StandardizedProbe
            {
                Z = new Dictionary<string, double>(),
                MaskedDimensionIds = ["d1"],
                UnmodelledDimensionIds = [],
                Distance = null,
                Coverage = 0.0,
                ComparedDimensionCount = 0,
            },
            "sender",
            Start);

        Assert.Equal(EvidenceAvailability.Unavailable, evidence.Availability);
        Assert.Null(evidence.Value);
    }

    [Fact]
    public void TheAssemblyExposesNoWayToSelectAnAction()
    {
        // Probabilistic components provide evidence; only deterministic policy authorises side
        // effects. If this assembly could return an action, that boundary would be a convention
        // rather than a property of the code.
        var offenders = typeof(AdaptiveProfile).Assembly
            .GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(member => member switch
            {
                MethodInfo method => Mentions(method.ReturnType),
                PropertyInfo property => Mentions(property.PropertyType),
                _ => false,
            })
            .Select(member => $"{member.DeclaringType?.Name}.{member.Name}")
            .ToArray();

        Assert.Empty(offenders);

        static bool Mentions(Type type) =>
            type == typeof(MailAction)
            || (type.IsGenericType && type.GetGenericArguments().Any(Mentions));
    }

    private static TrendResult RisingTrend() => new()
    {
        WindowName = "burst",
        VelocityAvailable = true,
        AccelerationAvailable = true,
        Velocity = new Dictionary<string, double> { ["semantic.payment_redirection"] = 0.04 },
        Acceleration = new Dictionary<string, double> { ["semantic.payment_redirection"] = 0.0002 },
        Movements = [],
        Suppressions = [],
        Coverage = 0.75,
        Support = 4,
        Narrative = "recipient fan-out rising while payment-redirection evidence also rises",
    };
}
