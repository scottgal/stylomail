using StyloMail.Adaptive.Profiles;
using StyloMail.Assessment.Campaign;
using StyloMail.Assessment.Semantic;
using StyloMail.Core;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// The fingerprint fallback proved at the call site rather than at the window.
/// </summary>
/// <remarks>
/// <b>This exists because the window-level tests passed while the check could not fire.</b>
/// `RecentCampaignWindow.FindNear` admitted a fingerprint-only match and
/// `CampaignNearDuplicateDetector` then filtered it out on its own floor, so a green result on the
/// window described a component the real path discards. The only test that distinguishes a working
/// check from one thrown away a layer up is one that goes through the detector.
/// </remarks>
public sealed class CampaignDetectorFallbackTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);

    private static SecurityBearingFingerprint Fingerprint(string digest) => new()
    {
        Digest = digest,
        ComponentCount = 2,
    };

    private static CampaignNearDuplicateDetector Detector() =>
        new(new RecentCampaignWindow(8, TimeSpan.FromHours(1)));

    private static IReadOnlyList<Evidence> Observe(
        CampaignNearDuplicateDetector detector,
        string assessmentId,
        string digest) =>
        detector.ObserveAndEvaluate(
            "acme",
            assessmentId,
            $"msg-{assessmentId}",
            Now,
            DimensionVector.Create(),
            Fingerprint(digest),
            "sender");

    [Fact]
    public void A_message_with_no_dimensions_reaches_a_near_duplicate_through_the_detector()
    {
        // The claim the whole change rests on, proved where production makes it rather than one
        // layer below. Before the detector's floor was relaxed, this returned unavailable: the
        // window admitted the match and the detector discarded it, so the check could not fire while
        // appearing to run.
        var detector = Detector();
        Observe(detector, "asm_1", "same-digest");

        var evidence = Observe(detector, "asm_2", "same-digest");

        var nearDuplicate = Assert.Single(
            evidence,
            e => e.SignalId == CampaignEvidenceIds.NearDuplicate);

        Assert.Equal(EvidenceAvailability.Available, nearDuplicate.Availability);
    }

    [Fact]
    public void A_fingerprint_only_match_says_it_compared_no_dimensions()
    {
        // The absence stated rather than left to be read off a count. A match on the fingerprint
        // alone is a narrower comparison than one over dimensions, and a reader who does not spot
        // "compared_dimensions: 0" would take it for the fuller one.
        var detector = Detector();
        Observe(detector, "asm_1", "same-digest");

        var evidence = Observe(detector, "asm_2", "same-digest");

        var nearDuplicate = Assert.Single(
            evidence,
            e => e.SignalId == CampaignEvidenceIds.NearDuplicate);

        Assert.Contains(nearDuplicate.Attributes ?? [], a => a.Name == "fingerprint_only" && a.Value == "true");
    }

    [Fact]
    public void A_different_destination_still_does_not_match_through_the_detector()
    {
        // The fifty-first message, at the call site. Identical in every way except the destination,
        // and it must not be dismissable.
        var detector = Detector();
        Observe(detector, "asm_1", "digest-one");

        var evidence = Observe(detector, "asm_2", "digest-two");

        var nearDuplicate = Assert.Single(
            evidence,
            e => e.SignalId == CampaignEvidenceIds.NearDuplicate);

        Assert.NotEqual(EvidenceAvailability.Available, nearDuplicate.Availability);
    }
}
