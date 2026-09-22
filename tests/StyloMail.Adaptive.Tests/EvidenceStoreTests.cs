using StyloMail.Adaptive.Profiles;
using StyloMail.Core;

namespace StyloMail.Adaptive.Tests;

public class EvidenceStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnlabelledTrafficAffectsObservedStateButNotTheBaseline()
    {
        var profile = Profile(MailDirection.Outbound);

        for (var i = 0; i < 50; i++)
        {
            profile.Observe(Observation(Start.AddMinutes(i), recipients: 3));
        }

        // 50 messages were seen. None of them was approved, so none of them is history.
        Assert.Equal(50, profile.Observed.Attempts);
        Assert.Equal(0, profile.Baseline.TrustedSupport);
        Assert.Null(profile.Baseline.ScaleModel);
    }

    [Fact]
    public void RejectedTrafficStillCounts()
    {
        var profile = Profile(MailDirection.Outbound);

        profile.Observe(Observation(Start, recipients: 5, rejected: true));
        profile.Observe(Observation(Start.AddMinutes(1), recipients: 2));

        // Abuse detection needs rejected attempts: a sender that only ever gets blocked
        // would otherwise look idle, and its throughput bound would reset itself.
        Assert.Equal(2, profile.Observed.Attempts);
        Assert.Equal(1, profile.Observed.RejectedAttempts);
        Assert.Equal(7, profile.Observed.Recipients);
    }

    [Fact]
    public void ObservedStateTracksFirstAndLastObservation()
    {
        var profile = Profile(MailDirection.Outbound);

        profile.Observe(Observation(Start.AddHours(3)));
        profile.Observe(Observation(Start.AddHours(1)));
        profile.Observe(Observation(Start.AddHours(5)));

        Assert.Equal(Start.AddHours(1), profile.Observed.FirstObservedAt);
        Assert.Equal(Start.AddHours(5), profile.Observed.LastObservedAt);
    }

    [Fact]
    public void AnApprovedSampleRaisesTrustedSupport()
    {
        var profile = Profile(MailDirection.Outbound);

        var result = profile.Promote(Sample(LabelProvenance.AuthenticatedOperator));

        Assert.Equal(PromotionOutcome.Promoted, result);
        Assert.Equal(1, profile.Baseline.TrustedSupport);
        Assert.Equal(1, profile.Baseline.Version);
    }

    [Theory]
    [InlineData(LabelProvenance.Unauthenticated)]
    [InlineData(LabelProvenance.DeliveryOnly)]
    [InlineData(LabelProvenance.AbsenceOfComplaint)]
    public void DeliveryAndSilenceDoNotEstablishSafety(LabelProvenance provenance)
    {
        var profile = Profile(MailDirection.Outbound);

        // "It was delivered and nobody complained" is how baseline poisoning works.
        var result = profile.Promote(Sample(provenance));

        Assert.Equal(PromotionOutcome.RejectedUntrustedProvenance, result);
        Assert.Equal(0, profile.Baseline.TrustedSupport);
    }

    [Fact]
    public void RecipientPreferenceChangesPreferenceNotGlobalTruth()
    {
        var sender = Profile(MailDirection.Outbound);
        var recipient = Profile(MailDirection.Outbound, ProfileScopeKind.Recipient);

        Assert.Equal(
            PromotionOutcome.RejectedProvenanceOutOfScope,
            sender.Promote(Sample(LabelProvenance.RecipientPreference)));
        Assert.Equal(
            PromotionOutcome.Promoted,
            recipient.Promote(Sample(LabelProvenance.RecipientPreference)));
    }

    [Fact]
    public void FreezingStopsPromotionButNotObservation()
    {
        var profile = Profile(MailDirection.Outbound);
        profile.Promote(Sample(LabelProvenance.AuthenticatedOperator));
        var versionAtFreeze = profile.Baseline.Version;

        profile.FreezeBaseline("suspected compromise", Start.AddHours(1));
        profile.Observe(Observation(Start.AddHours(2), recipients: 40));
        var result = profile.Promote(Sample(LabelProvenance.AuthenticatedOperator));

        // The account's traffic is still counted — containment needs the rate — but a
        // suspected-compromise account must not be able to teach us what normal looks like.
        Assert.Equal(PromotionOutcome.RejectedBaselineFrozen, result);
        Assert.Equal(1, profile.Observed.Attempts);
        Assert.Equal(1, profile.Baseline.TrustedSupport);
        Assert.Equal(versionAtFreeze, profile.Baseline.Version);
        Assert.True(profile.Baseline.IsFrozen);
    }

    [Fact]
    public void UnfreezingRestoresPromotion()
    {
        var profile = Profile(MailDirection.Outbound);
        profile.FreezeBaseline("suspected compromise", Start);
        profile.UnfreezeBaseline("reviewed and cleared");

        var result = profile.Promote(Sample(LabelProvenance.AuthenticatedOperator));

        Assert.Equal(PromotionOutcome.Promoted, result);
        Assert.False(profile.Baseline.IsFrozen);
        Assert.Null(profile.Baseline.FreezeReason);
    }

    [Fact]
    public void TheBaselineVersionAdvancesOnlyOnPromotion()
    {
        var profile = Profile(MailDirection.Outbound);

        profile.Observe(Observation(Start));
        profile.Observe(Observation(Start.AddMinutes(1)));
        Assert.Equal(0, profile.Baseline.Version);

        profile.Promote(Sample(LabelProvenance.AuthenticatedOperator));
        Assert.Equal(1, profile.Baseline.Version);

        profile.FreezeBaseline("suspected compromise", Start);
        profile.Promote(Sample(LabelProvenance.AuthenticatedOperator));
        Assert.Equal(1, profile.Baseline.Version);
    }

    private static AdaptiveProfile Profile(
        MailDirection direction,
        ProfileScopeKind scope = ProfileScopeKind.OutboundSender) =>
        new(new ProfileKey
        {
            TenantId = "tenant-a",
            Scope = scope,
            Key = "profile-key",
            Direction = direction,
        });

    private static ProfileObservation Observation(
        DateTimeOffset at,
        int recipients = 1,
        bool rejected = false) => new()
    {
        ObservedAt = at,
        RecipientCount = recipients,
        WasRejected = rejected,
        Dimensions = DimensionVector.Create(
            DimensionSample.Available("d1", 0.5),
            DimensionSample.Available("d2", 0.5)),
    };

    private static TrustedSample Sample(LabelProvenance provenance) => new()
    {
        RecordedAt = Start,
        Provenance = provenance,
        Dimensions = DimensionVector.Create(
            DimensionSample.Available("d1", 0.5),
            DimensionSample.Available("d2", 0.5)),
    };
}
