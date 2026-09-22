using StyloMail.Adaptive.Profiles;
using StyloMail.Assessment.Campaign;
using StyloMail.Assessment.Semantic;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// The change a channel without dimensions forced onto the campaign window, measured on the email
/// path rather than assumed inert.
/// </summary>
/// <remarks>
/// The window used to skip every comparison with no dimensions to compare. That is the ordinary case
/// for chat, where all twelve dimensions are recorded unavailable by design, and it is also the case
/// for **an email message assessed while the semantic classifier is down**. The suite passing
/// unchanged after the change does not show the email path is unaffected: it shows nothing covered
/// this. These cover it.
/// </remarks>
public sealed class CampaignFingerprintFallbackTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);

    /// <summary>A fingerprint over two components, so it is agreement rather than nothing.</summary>
    private static SecurityBearingFingerprint Fingerprint(string digest) => new()
    {
        Digest = digest,
        ComponentCount = 2,
    };

    private static void Record(RecentCampaignWindow window, string assessmentId, string digest)
    {
        window.Record(
            new CampaignObservation
            {
                TenantId = "acme",
                AssessmentId = assessmentId,
                InternalMessageId = $"msg-{assessmentId}",
                ObservedAt = Now,
                Vector = DimensionVector.Create(),
                Fingerprint = Fingerprint(digest),
                SenderScope = "sender",
            },
            Now);
    }

    [Fact]
    public void An_email_message_with_no_dimensions_now_matches_on_its_fingerprint_alone()
    {
        // The deliberate change, stated as the behaviour rather than as an intention: a message
        // assessed during a semantic outage has no dimensions to compare, and before this it could
        // not be matched as a duplicate at all. Now the campaign window keeps working while the
        // classifier is down.
        var window = new RecentCampaignWindow(8, TimeSpan.FromHours(1));
        Record(window, "asm_1", "same-digest");

        var matches = window.FindNear(
            "acme",
            DimensionVector.Create(),
            Fingerprint("same-digest"),
            Now,
            assessmentId: "asm_2");

        var match = Assert.Single(matches);
        Assert.Equal(0, match.ComparedDimensions);
        Assert.True(match.SecurityBearingAgrees);
    }

    [Fact]
    public void A_fingerprint_over_nothing_still_agrees_with_nothing()
    {
        // The guard that makes the fallback safe. Two unrelated messages that both carry no links
        // and no dimensions have the same empty digest, and treating that as agreement would make
        // every quiet message a duplicate of every other quiet one.
        var window = new RecentCampaignWindow(8, TimeSpan.FromHours(1));

        window.Record(
            new CampaignObservation
            {
                TenantId = "acme",
                AssessmentId = "asm_1",
                InternalMessageId = "msg-1",
                ObservedAt = Now,
                Vector = DimensionVector.Create(),
                Fingerprint = new SecurityBearingFingerprint { Digest = string.Empty, ComponentCount = 0 },
                SenderScope = "sender",
            },
            Now);

        var matches = window.FindNear(
            "acme",
            DimensionVector.Create(),
            new SecurityBearingFingerprint { Digest = string.Empty, ComponentCount = 0 },
            Now,
            assessmentId: "asm_2");

        Assert.Empty(matches);
    }

    [Fact]
    public void Different_fingerprints_do_not_match_on_the_fallback()
    {
        // The fifty-first message: identical in every comparable respect except the destination it
        // points at. It must escalate rather than be dismissable, and by construction rather than by
        // a threshold.
        var window = new RecentCampaignWindow(8, TimeSpan.FromHours(1));
        Record(window, "asm_1", "digest-one");

        var matches = window.FindNear(
            "acme",
            DimensionVector.Create(),
            Fingerprint("digest-two"),
            Now,
            assessmentId: "asm_2");

        Assert.Empty(matches);
    }
}
