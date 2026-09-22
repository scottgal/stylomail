using StyloMail.Adaptive.Profiles;
using StyloMail.Assessment.Semantic;
using StyloMail.Core;

namespace StyloMail.Assessment.Campaign;

/// <summary>Signal ids the campaign window produces.</summary>
public static class CampaignEvidenceIds
{
    /// <summary>This message resembles something recently seen <em>and</em> does the same thing.</summary>
    public const string NearDuplicate = "campaign.near_duplicate";

    /// <summary>
    /// This message resembles something recently seen but does <em>not</em> do the same thing.
    /// </summary>
    /// <remarks>
    /// A distinct signal rather than a raised near-duplicate, because it means the opposite of what
    /// a near-duplicate means. Reused wording with changed destinations is not evidence that this
    /// message is known-good; it is evidence that somebody had a working template and adapted it.
    /// Reporting it as a match would let the first version of an attack vouch for the second.
    /// </remarks>
    public const string SecurityBearingVariant = "campaign.security_bearing_variant";

    /// <summary>Matches whose security-bearing details agree.</summary>
    public const string MatchingMessagesAttribute = "matching_message_ids";

    /// <summary>Matches whose security-bearing details differ, with the wording still similar.</summary>
    public const string VariantMessagesAttribute = "variant_message_ids";

    /// <summary>Attribute marking whether this signal may justify a hard block alone. Always false.</summary>
    public const string AloneSufficientAttribute = "alone_sufficient";

    /// <summary>Producer version stamped onto everything emitted here.</summary>
    public const string SourceVersion = "campaign/1";
}

/// <summary>Tuning for the near-duplicate detector.</summary>
public sealed record CampaignDetectionOptions
{
    /// <summary>Similarity at or above which two messages are compared seriously.</summary>
    public double MinimumSimilarity { get; init; } = 0.85;

    /// <summary>Dimensions that must be shared before any comparison is reported at all.</summary>
    public int MinimumComparedDimensions { get; init; } = 4;

    /// <summary>Matches examined per message. Bounds both the work and the ledger entry.</summary>
    public int MaxMatches { get; init; } = 8;

    public void Validate()
    {
        if (MinimumSimilarity is < 0 or > 1 || double.IsNaN(MinimumSimilarity))
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumSimilarity), MinimumSimilarity, "Similarity must be within [0, 1].");
        }

        if (MinimumComparedDimensions < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumComparedDimensions), MinimumComparedDimensions, "At least one dimension must be compared.");
        }

        if (MaxMatches < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMatches), MaxMatches, "At least one match must be allowed.");
        }
    }
}

/// <summary>
/// Turns recent-window lookups into campaign evidence.
/// </summary>
/// <remarks>
/// <b>This produces evidence and can never produce an answer.</b> It reads
/// <see cref="RecentCampaignWindow"/>, which retains vectors and digests rather than assessments,
/// so there is no cached result here for a near-duplicate to be served from — the guarantee is
/// structural, not procedural. The worst a false match can do is add a signal that policy weighs
/// with everything else; it cannot skip authentication checks, profile comparison, policy, or
/// counter accounting for this message.
///
/// <para>
/// <b>Security-bearing differences are never smoothed away.</b> When the wording matches and the
/// destinations, payloads or sender context do not, the near-duplicate signal is withheld entirely
/// and the variant signal is emitted instead. There is no threshold at which "similar but pointing
/// somewhere else" becomes "the same message".
/// </para>
///
/// <para>
/// Behavioural family, not semantic: this is a comparison against what this deployment has actually
/// seen, not a statement by a model about the message.
/// </para>
/// </remarks>
public sealed class CampaignNearDuplicateDetector
{
    private readonly RecentCampaignWindow _window;
    private readonly CampaignDetectionOptions _options;

    public CampaignNearDuplicateDetector(RecentCampaignWindow window, CampaignDetectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        _options = options ?? new CampaignDetectionOptions();
        _options.Validate();

        _window = window;
    }

    /// <summary>
    /// Records this message in the window and returns the campaign evidence its arrival produces.
    /// </summary>
    /// <remarks>
    /// Recording happens before the lookup so that a burst landing concurrently can see its own
    /// members — a campaign is exactly the case where the interesting comparisons are between
    /// messages that arrive at the same moment. Self-matching is prevented by excluding the
    /// message's own assessment id rather than by ordering.
    /// </remarks>
    public IReadOnlyList<Evidence> ObserveAndEvaluate(
        string tenantId,
        string assessmentId,
        string internalMessageId,
        DateTimeOffset now,
        DimensionVector vector,
        SecurityBearingFingerprint fingerprint,
        string? senderScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assessmentId);
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(fingerprint);

        _window.Record(
            new CampaignObservation
            {
                TenantId = tenantId,
                AssessmentId = assessmentId,
                InternalMessageId = internalMessageId,
                ObservedAt = now,
                Vector = vector,
                Fingerprint = fingerprint,
                SenderScope = senderScope,
            },
            now);

        var matches = _window.FindNear(
            tenantId,
            vector,
            fingerprint,
            now,
            assessmentId,
            _options.MaxMatches,
            _options.MinimumSimilarity);

        var comparable = matches
            .Where(match => match.ComparedDimensions >= _options.MinimumComparedDimensions)
            .ToList();

        if (comparable.Count == 0)
        {
            // No match is not "nothing to report and therefore normal". It is reported as
            // unavailable so the ledger shows that campaign comparison ran and found nothing,
            // rather than leaving the reader to infer it from an absent signal.
            return
            [
                new Evidence
                {
                    SignalId = CampaignEvidenceIds.NearDuplicate,
                    Origin = EvidenceOrigin.Behavioural,
                    Availability = EvidenceAvailability.Unavailable,
                    Value = null,
                    SampleSupport = 0,
                    SourceVersion = CampaignEvidenceIds.SourceVersion,
                    ObservedAt = now,
                    ObservedScope = "tenant",
                    Attributes =
                    [
                        Attribute(CampaignEvidenceIds.AloneSufficientAttribute, "false"),
                        Attribute("reason", "no comparable message in the recent window"),
                    ],
                },
            ];
        }

        var agreeing = comparable.Where(match => match.SecurityBearingAgrees).ToList();
        var variants = comparable.Where(match => !match.SecurityBearingAgrees).ToList();

        var evidence = new List<Evidence>();

        if (agreeing.Count > 0)
        {
            var best = agreeing[0];

            evidence.Add(new Evidence
            {
                SignalId = CampaignEvidenceIds.NearDuplicate,
                Origin = EvidenceOrigin.Behavioural,
                Availability = EvidenceAvailability.Available,
                Value = best.Similarity,
                SampleSupport = agreeing.Count + 1,
                SourceVersion = CampaignEvidenceIds.SourceVersion,
                ObservedAt = now,
                ObservedScope = "tenant",
                Attributes =
                [
                    Attribute(CampaignEvidenceIds.AloneSufficientAttribute, "false"),
                    // Every match, not a joined string. Three matches and one match are different
                    // facts, and a count of three is not the same fact as a list called "a,b,c".
                    .. agreeing.Select(match =>
                        Attribute(CampaignEvidenceIds.MatchingMessagesAttribute, match.Observation.InternalMessageId)),
                    Attribute("compared_dimensions", best.ComparedDimensions.ToString()),
                ],
            });
        }

        if (variants.Count > 0)
        {
            var best = variants[0];

            evidence.Add(new Evidence
            {
                SignalId = CampaignEvidenceIds.SecurityBearingVariant,
                Origin = EvidenceOrigin.Behavioural,
                Availability = EvidenceAvailability.Available,
                Value = best.Similarity,
                SampleSupport = variants.Count,
                SourceVersion = CampaignEvidenceIds.SourceVersion,
                ObservedAt = now,
                ObservedScope = "tenant",
                Attributes =
                [
                    Attribute(CampaignEvidenceIds.AloneSufficientAttribute, "false"),
                    Attribute("security_bearing_match", "false"),
                    .. variants.Select(match =>
                        Attribute(CampaignEvidenceIds.VariantMessagesAttribute, match.Observation.InternalMessageId)),
                    Attribute("compared_dimensions", best.ComparedDimensions.ToString()),
                ],
            });
        }

        return evidence;
    }

    private static EvidenceAttribute Attribute(string name, string value) => new() { Name = name, Value = value };
}
