using StyloMail.Adaptive;
using StyloMail.Adaptive.Profiles;
using StyloMail.Assessment.Campaign;
using StyloMail.Assessment.Semantic;
using StyloMail.Core;
using StyloMail.Policy;

namespace StyloMail.Assessment;

/// <summary>
/// Configuration for the composition root.
/// </summary>
/// <remarks>
/// Everything here is either a hard limit or a version stamp. The limits are bounds rather than
/// tuning: an unbounded recipient list, link list or attachment list is a message that can make the
/// pipeline expensive, and expense an attacker controls is a denial-of-service vector. The versions
/// are what make a decision reproducible and a cache invalidatable — a version that is not recorded
/// is a version nobody can act on when it changes.
/// </remarks>
public sealed record MailAssessorOptions
{
    /// <summary>
    /// Produces the tenant-scoped pseudonyms profiles are keyed on.
    /// </summary>
    /// <remarks>
    /// Required, and required to be built from a real master key. There is deliberately no default:
    /// a pipeline that quietly invented a key would key every deployment's profiles identically,
    /// and the pseudonymization the whole profile store depends on would hold only until somebody
    /// noticed.
    /// </remarks>
    public required ProfileKeyHasher ProfileKeyHasher { get; init; }

    /// <summary>
    /// Version of the preprocessing that prepares a message for the classifier.
    /// </summary>
    /// <remarks>
    /// Stamped on every assessment and part of the semantic cache key, so changing how content is
    /// prepared invalidates the corpus instead of silently mixing two regimes in one store.
    /// </remarks>
    public string PreprocessingVersion { get; init; } = "assessment-preprocessing/1";

    /// <summary>The semantic question set. Changing this is a question-schema version change.</summary>
    public IReadOnlyList<SemanticDimension> Dimensions { get; init; } = SemanticDimensions.All;

    /// <summary>Maximum recipients per message. Beyond this the message is refused before acceptance.</summary>
    public int MaxRecipients { get; init; } = 1_000;

    /// <summary>Maximum links carried into analysis.</summary>
    public int MaxLinks { get; init; } = 500;

    /// <summary>Maximum attachments carried into analysis.</summary>
    public int MaxAttachments { get; init; } = 100;

    /// <summary>Maximum body characters accepted for analysis.</summary>
    public int MaxBodyCharacters { get; init; } = 1_000_000;

    /// <summary>
    /// Per-recipient relationship profiles maintained for one message.
    /// </summary>
    /// <remarks>
    /// A bound, not a preference. The recipient count is attacker-controlled, and one message with a
    /// thousand recipients must not become a thousand profile store round trips. The sender profile
    /// is never bounded and still records every recipient, so rate and fan-out evidence stay
    /// complete; what the bound costs is per-pair drift for recipients past it, and that is a
    /// deliberate trade rather than an oversight.
    /// </remarks>
    public int MaxRelationshipsObserved { get; init; } = 10;

    /// <summary>
    /// An operator-named rule that authorises the assessment path to commit trusted learning.
    /// </summary>
    /// <remarks>
    /// <b>Null by default, and null is the whole point.</b> An assessment is a question, not a
    /// verdict about what was correct, so it has no authority to teach — the ordinary case learns
    /// nothing, permanently. Setting this names the rule that overrides that, which is what a
    /// replay harness or an explicitly-permitted training feed needs. The value is recorded on every
    /// sample it authorises, so "which rule taught the profile this?" stays answerable.
    /// </remarks>
    public string? AssessmentPathLearningRuleId { get; init; }

    /// <summary>
    /// Decline responsibility for a message whose semantic evidence is <em>entirely</em> unavailable.
    /// </summary>
    /// <remarks>
    /// <b>On by default, and it is a safety property rather than a preference.</b> When the provider
    /// cannot be reached, every semantic dimension is masked, the risk index is computed over no
    /// weight at all, and the number that comes out is zero. Zero is below every threshold, so
    /// without this the wiring's answer to a classifier outage would be to allow everything — the
    /// exact failure the "unknown is not zero" rule exists to prevent, arriving through the back
    /// door of a scoring function that cannot see it happened.
    ///
    /// <para>
    /// The honest answer to "we cannot assess this" is to decline responsibility temporarily, which
    /// is recoverable, rather than to deliver on a decision nobody is in a position to make. The
    /// underlying allow-on-zero-coverage behaviour belongs in the policy engine, where coverage is
    /// already a first-class input; until it is handled there, this guard is the wiring's
    /// responsibility and is deliberately impossible to miss.
    /// </para>
    ///
    /// <para>
    /// Set it to <see langword="false"/> for a deployment that genuinely runs on the local evidence
    /// path with no semantic provider at all — the case the spec describes for tenants that forbid
    /// external content processing. Those deployments are not experiencing an outage; they have an
    /// explicitly unavailable state by design, and refusing every message would be refusing the
    /// deployment's whole traffic.
    /// </para>
    /// </remarks>
    public bool DeclineResponsibilityOnSemanticOutage { get; init; } = true;

    /// <summary>
    /// Perceptual key material for near-duplicate comparison of message content.
    /// </summary>
    /// <remarks>
    /// Kept separate from the profile pseudonymization key so that rotating one does not
    /// invalidate the other. Reserved for the durable campaign store; the in-process window
    /// compares vectors directly and needs no key.
    /// </remarks>
    public AdaptiveOptions Adaptive { get; init; } = new();

    public CampaignDetectionOptions Campaign { get; init; } = new();

    public SemanticCacheOptions SemanticCache { get; init; } = new()
    {
        ClassifierModelVersion = "jev-1.13.0",
    };

    public PolicyOptions Policy { get; init; } = new();

    /// <summary>Maximum recipients one authenticated outbound principal may address within the budget window.</summary>
    public int OutboundRecipientBudgetPerPrincipal { get; init; } = 500;

    /// <summary>Length of the outbound recipient budget window.</summary>
    public TimeSpan OutboundRecipientBudgetWindow { get; init; } =
        StyloMail.Adaptive.Learning.SendingQuotaLedger.DefaultWindow;

    /// <summary>Size of the per-tenant recent-campaign window.</summary>
    public int CampaignWindowCapacity { get; init; } = 512;

    /// <summary>How long an observation stays in the recent-campaign window.</summary>
    public TimeSpan CampaignWindowRetention { get; init; } = TimeSpan.FromHours(24);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ProfileKeyHasher);
        ArgumentException.ThrowIfNullOrWhiteSpace(PreprocessingVersion);

        if (Dimensions.Count == 0)
        {
            throw new ArgumentException("At least one semantic dimension must be configured.", nameof(Dimensions));
        }

        Positive(MaxRecipients, nameof(MaxRecipients));
        Positive(MaxLinks, nameof(MaxLinks));
        Positive(MaxAttachments, nameof(MaxAttachments));
        Positive(MaxBodyCharacters, nameof(MaxBodyCharacters));
        Positive(MaxRelationshipsObserved, nameof(MaxRelationshipsObserved));
        Positive(OutboundRecipientBudgetPerPrincipal, nameof(OutboundRecipientBudgetPerPrincipal));
        Positive(CampaignWindowCapacity, nameof(CampaignWindowCapacity));

        if (OutboundRecipientBudgetWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OutboundRecipientBudgetWindow),
                OutboundRecipientBudgetWindow,
                "The budget window must be positive.");
        }

        if (CampaignWindowRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CampaignWindowRetention), CampaignWindowRetention, "Retention must be positive.");
        }

        SemanticCache.Validate();
        Campaign.Validate();
    }

    private static void Positive(int value, string name)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be at least one.");
        }
    }
}
