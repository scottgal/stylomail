using StyloMail.Adaptive.Learning;
using StyloMail.Adaptive.Profiles;
using StyloMail.Core;

namespace StyloMail.Adaptive.Tests;

public class EwmaTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AlphaFollowsOneMinusExpOfElapsedOverTau()
    {
        Assert.Equal(0.6321205588, Ewma.Alpha(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)), 9);
        Assert.Equal(0.0, Ewma.Alpha(TimeSpan.Zero, TimeSpan.FromMinutes(1)), 9);
        Assert.Equal(0.9502129316, Ewma.Alpha(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1)), 9);
    }

    [Fact]
    public void TheFirstObservationSeedsTheAverageExactly()
    {
        var state = EwmaState.Empty.Observe(0.7, Start, Options());

        Assert.True(state.HasValue);
        Assert.Equal(0.7, state.Value!.Value, 9);
        Assert.Equal(1, state.Updates);
    }

    [Fact]
    public void AnObservationAtTheSameInstantMovesNothing()
    {
        var options = Options();
        var state = EwmaState.Empty.Observe(0.0, Start, options);

        var after = state.Observe(1.0, Start, options);

        // No time has passed, so there is no basis for updating, elapsed time is the weight.
        Assert.Equal(0.0, after.Value!.Value, 9);
    }

    [Fact]
    public void ASingleEventCannotMoveTheAverageArbitrarilyFar()
    {
        var options = new EwmaOptions
        {
            Tau = TimeSpan.FromSeconds(1),
            MaxEventContribution = 0.25,
        };

        var state = EwmaState.Empty.Observe(0.0, Start, options);
        var after = state.Observe(1.0, Start.AddMinutes(5), options);

        // Five minutes against a one-second tau gives alpha ≈ 1, so without the per-event
        // bound this one message would replace the average outright. One message is one
        // message: it may nudge what we believe, never overwrite it.
        Assert.True(Ewma.Alpha(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1)) > 0.999);
        Assert.Equal(0.25, after.Value!.Value, 9);
    }

    [Fact]
    public void LongerElapsedTimeMovesTheAverageFurther()
    {
        var options = Options();
        var state = EwmaState.Empty.Observe(0.0, Start, options);

        var soon = state.Observe(1.0, Start.AddSeconds(15), options);
        var later = state.Observe(1.0, Start.AddMinutes(4), options);

        Assert.True(later.Value!.Value > soon.Value!.Value);
        Assert.Equal(0.2211992169, soon.Value.Value, 9);
    }

    [Fact]
    public void TheAverageStaysWithinTheRangeOfWhatWasObserved()
    {
        var options = Options();
        var state = EwmaState.Empty.Observe(0.2, Start, options);

        state = state.Observe(0.4, Start.AddMinutes(5), options);
        state = state.Observe(0.1, Start.AddMinutes(9), options);

        // An average outside the observed range would be evidence of nothing at all.
        Assert.InRange(state.Value!.Value, 0.1, 0.4);
    }

    private static EwmaOptions Options() => new()
    {
        Tau = TimeSpan.FromMinutes(1),
        MaxEventContribution = 1.0,
    };
}

public class BaselineMovementTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASinglePromotionCannotMoveTheBaselineFar()
    {
        var options = new AdaptiveOptions { MaxBaselineShiftPerUpdate = 0.25 };
        var profile = Profile(options);
        for (var i = 0; i < 10; i++)
        {
            profile.Promote(Sample(0.5, Start));
        }

        var meanBefore = profile.Baseline.Dimensions["d1"].Mean;
        profile.Promote(Sample(0.9, Start));

        var meanAfter = profile.Baseline.Dimensions["d1"].Mean;
        var uncapped = ((meanBefore * 10) + 0.9) / 11;

        // The dimension's spread is 0.05 (the variance floor), so one sample may move the mean
        // by at most 0.25 × 0.05 = 0.0125. Uncapped it would have gone to ≈ 0.536, and a
        // baseline a single approved sample can drag is one an attacker only has to convince once.
        Assert.Equal(0.5, meanBefore, 9);
        Assert.True(meanAfter > meanBefore);
        Assert.True(meanAfter <= meanBefore + 0.0125 + 1e-9, $"mean moved to {meanAfter}");
        Assert.True(uncapped > meanBefore + 0.03);
    }

    [Fact]
    public void SustainedConsistentPromotionEventuallyMovesTheBaseline()
    {
        var profile = Profile();
        for (var i = 0; i < 5; i++)
        {
            profile.Promote(Sample(0.5, Start));
        }

        // A genuine, sustained shift is learned. The cap slows the baseline down; it does not
        // freeze it, or the system could never adapt to a real change in a sender's business.
        for (var i = 0; i < 200; i++)
        {
            profile.Promote(Sample(0.9, Start));
        }

        Assert.True(
            profile.Baseline.Dimensions["d1"].Mean > 0.8,
            $"mean settled at {profile.Baseline.Dimensions["d1"].Mean}");
    }

    private static AdaptiveProfile Profile(AdaptiveOptions? options = null) => new(
        new ProfileKey
        {
            TenantId = "tenant-a",
            Scope = ProfileScopeKind.OutboundSender,
            Key = "sender",
            Direction = MailDirection.Outbound,
        },
        options);

    private static TrustedSample Sample(double value, DateTimeOffset at) => new()
    {
        RecordedAt = at,
        Provenance = LabelProvenance.AuthenticatedOperator,
        Dimensions = Profiles.DimensionVector.Create(Profiles.DimensionSample.Available("d1", value)),
    };
}

public class ProfileLearningTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewRegimeStartsAsACandidateWithoutReplacingTheBaseline()
    {
        var profile = Profile(initialRegimeId: "regime-a");
        for (var i = 0; i < 5; i++)
        {
            profile.Promote(Sample(0.2, Start));
        }

        profile.BeginRegime("regime-b");
        profile.Promote(Sample(0.9, Start));

        // The new pattern has not earned the right to define normal yet, so it accumulates
        // somewhere else and the incumbent baseline does not move at all.
        Assert.Equal(0.2, profile.Baseline.Dimensions["d1"].Mean, 6);
        Assert.NotNull(profile.RegimeCandidate);
        Assert.Equal("regime-b", profile.RegimeCandidate!.RegimeId);
        Assert.Equal("regime-a", profile.Baseline.RegimeId);
    }

    [Fact]
    public void ACandidateNeedsBothSupportAndStabilityToBePromoted()
    {
        var profile = Profile();
        profile.BeginRegime("regime-b");

        // Plenty of support, but the values are still swinging: promoting now would enshrine
        // a transient as the new normal.
        for (var i = 0; i < 12; i++)
        {
            profile.Promote(Sample(i % 2 == 0 ? 0.0 : 1.0, Start));
        }

        Assert.Equal(12, profile.RegimeCandidate!.TrustedSupport);
        Assert.False(profile.RegimeCandidate.IsStable);
        Assert.False(profile.RegimeCandidate.IsPromotable);
        Assert.False(profile.PromoteRegime());

        // The same shift, sustained, settles, and only then does it replace anything.
        for (var i = 0; i < 12; i++)
        {
            profile.Promote(Sample(0.9, Start));
        }

        Assert.True(profile.RegimeCandidate!.IsStable);
        Assert.True(profile.RegimeCandidate.IsPromotable);
        Assert.True(profile.PromoteRegime());
        Assert.Equal("regime-b", profile.Baseline.RegimeId);
        Assert.Null(profile.RegimeCandidate);
    }

    [Fact]
    public void ACandidateCannotBePromotedOnUntrustedSupport()
    {
        var profile = Profile();
        profile.BeginRegime("regime-b");

        for (var i = 0; i < 20; i++)
        {
            profile.Promote(new TrustedSample
            {
                RecordedAt = Start,
                Provenance = LabelProvenance.DeliveryOnly,
                Dimensions = Dimensions(0.9),
            });
        }

        Assert.Equal(0, profile.RegimeCandidate!.TrustedSupport);
        Assert.False(profile.PromoteRegime());
    }

    [Fact]
    public void RollbackRestoresTheBaselineWithoutErasingQuotasOrIncidents()
    {
        var quota = new SendingQuotaLedger(recipientsPerWindow: 20, TimeSpan.FromHours(1));
        var incidents = new IncidentLog();

        var profile = Profile();
        for (var i = 0; i < 5; i++)
        {
            profile.Promote(Sample(0.5, Start));
        }

        var checkpoint = profile.CaptureBaselineCheckpoint(Start);
        var versionAtCheckpoint = checkpoint.Baseline.Version;

        for (var i = 0; i < 40; i++)
        {
            profile.Promote(Sample(0.9, Start));
        }

        // Containment happened while the poisoned baseline was in force.
        profile.FreezeBaseline("suspected compromise", Start);
        Assert.True(quota.TryReserve("tenant-a", "sender", 12, Start));
        incidents.Record("tenant-a", "sender", "outbound burst quarantined", Start);

        profile.RollbackBaseline(checkpoint);

        Assert.Equal(versionAtCheckpoint, profile.Baseline.Version);
        Assert.Equal(0.5, profile.Baseline.Dimensions["d1"].Mean, 6);

        // Rolling back what we believe is not the same as un-sending mail. The quota stays
        // spent and the incident stays on the record, otherwise rollback becomes an
        // attacker's reset button.
        Assert.Equal(8, quota.Remaining("tenant-a", "sender", Start));
        Assert.Single(incidents.For("tenant-a", "sender"));
        Assert.True(profile.Baseline.IsFrozen);
    }

    [Fact]
    public void DehydrationKeepsObservedCountersAndGrantsNoFreshQuota()
    {
        var quota = new SendingQuotaLedger(recipientsPerWindow: 20, TimeSpan.FromHours(1));
        var profile = Profile();

        profile.Promote(Sample(0.5, Start));
        for (var i = 0; i < 3; i++)
        {
            profile.Observe(Observation(Start.AddMinutes(i), recipients: 2));
        }

        Assert.True(quota.TryReserve("tenant-a", "sender", 12, Start));

        profile.Dehydrate();

        // Dehydrating frees the expensive state, baseline and trend history, and keeps the
        // cheap counters. A profile eviction that reset the observed count would hand the
        // sender a fresh quota the moment its profile was evicted, which is a bypass, not
        // housekeeping.
        Assert.Null(profile.Baseline.ScaleModel);
        Assert.Equal(0, profile.Baseline.TrustedSupport);
        Assert.Equal(3, profile.Observed.Attempts);
        Assert.Equal(6, profile.Observed.Recipients);
        Assert.Equal(8, quota.Remaining("tenant-a", "sender", Start));
    }

    [Fact]
    public void FastAndSlowAveragesDivergeOnASustainedShift()
    {
        var profile = Profile();

        for (var i = 0; i < 20; i++)
        {
            profile.Observe(Observation(Start.AddMinutes(i), dimensionValue: 0.1));
        }

        for (var i = 0; i < 5; i++)
        {
            profile.Observe(Observation(Start.AddMinutes(20 + i), dimensionValue: 0.9));
        }

        // Two windows, two speeds: the fast average notices the change long before the slow
        // one, which is the entire reason for keeping both.
        Assert.True(
            profile.FastAverages["d1"].Value > profile.SlowAverages["d1"].Value,
            $"fast {profile.FastAverages["d1"].Value} slow {profile.SlowAverages["d1"].Value}");
    }

    private static AdaptiveProfile Profile(string initialRegimeId = "regime-0") => new(
        new ProfileKey
        {
            TenantId = "tenant-a",
            Scope = ProfileScopeKind.OutboundSender,
            Key = "sender",
            Direction = MailDirection.Outbound,
        },
        options: null,
        initialRegimeId);

    private static Profiles.DimensionVector Dimensions(double value) =>
        Profiles.DimensionVector.Create(Profiles.DimensionSample.Available("d1", value));

    private static TrustedSample Sample(double value, DateTimeOffset at) => new()
    {
        RecordedAt = at,
        Provenance = LabelProvenance.AuthenticatedOperator,
        Dimensions = Dimensions(value),
    };

    private static ProfileObservation Observation(
        DateTimeOffset at,
        int recipients = 1,
        double? dimensionValue = null) => new()
    {
        ObservedAt = at,
        RecipientCount = recipients,
        WasRejected = false,
        Dimensions = Dimensions(dimensionValue ?? 0.5),
    };
}
