using StyloMail.Adaptive.Profiles;

namespace StyloMail.Assessment.Learning;

/// <summary>What a caller is asking the gate to learn from.</summary>
public sealed record TrustedLearningRequest
{
    public required ProfileKey Key { get; init; }

    public required TrustedSample Sample { get; init; }

    /// <summary>
    /// True when an authoritative outcome, an operator decision, an application result, a
    /// verified delivery event, backs this label.
    /// </summary>
    public required bool AuthorizedOutcomePresent { get; init; }

    /// <summary>
    /// An explicitly configured rule that authorises learning without an outcome.
    /// </summary>
    /// <remarks>
    /// The escape hatch for labelled training data and for replay harnesses, and the reason it is a
    /// named identifier rather than a boolean: a boolean cannot be audited, and "which rule taught
    /// the profile this?" is a question an operator will eventually have to answer.
    /// </remarks>
    public string? AuthorizedRuleId { get; init; }

    /// <summary>True when the request came from an assessment-only call. Never learns.</summary>
    public required bool AssessmentOnly { get; init; }

    /// <summary>Why the caller believes this label is trustworthy. Recorded, and independently checked.</summary>
    public required LabelProvenance ClaimedProvenance { get; init; }
}

/// <summary>What the gate decided, and why.</summary>
public sealed record TrustedLearningOutcome
{
    /// <summary>True when the request may proceed to the profile.</summary>
    public required bool Attempted { get; init; }

    /// <summary>The profile's answer, once it has been offered; <see langword="null"/> before then.</summary>
    public PromotionOutcome? Promotion { get; init; }

    /// <summary>Machine-readable reason, for the ledger.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// The only door through which a label becomes trusted history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Learning is the highest-value thing an attacker can steal in this system.</b> The trusted
/// baseline decides what "normal" means for a sender, so a baseline that can be moved by sending
/// mail is a baseline that has already been captured, the attacker sends, it is learned as
/// legitimate, and every subsequent message inherits that. Two independent controls therefore stand
/// between a message and the baseline, and both must open.
/// </para>
///
/// <list type="number">
/// <item><b>Authority.</b> Either an authorized outcome, or an explicitly named rule. The
/// assessment path has neither by construction, an assessment is a question, not a verdict about
/// what was correct, so an assessment can never teach anything. This is enforced here rather than
/// by the caller remembering not to call.</item>
/// <item><b>Provenance.</b> The profile applies its own gate, which no caller can bypass: a
/// <see cref="LabelProvenance.DeliveryOnly"/> or <see cref="LabelProvenance.AbsenceOfComplaint"/>
/// label cannot promote a baseline no matter who asks, because those are exactly the labels an
/// attacker generates by simply sending mail.</item>
/// </list>
///
/// <para>
/// <b>Authorisation is a separate step from promotion, and that is deliberate.</b> The decision has
/// to be made <em>before</em> the profile store is touched: a refused request that still ran a
/// load-save cycle would advance the profile's revision, and every other writer would then lose a
/// compare-and-swap to a write that changed nothing. Refusing is free only if nothing is written.
/// </para>
///
/// <para>
/// The claimed provenance is passed straight through to the profile rather than being trusted here.
/// A gate that accepted the caller's word for provenance would be a gate a caller could open.
/// </para>
/// </remarks>
public sealed class TrustedLearningGate
{
    private readonly LabelProvenance _minimumProvenance;

    /// <param name="minimumProvenance">
    /// The weakest provenance this gate will pass through, defaulting to
    /// <see cref="LabelProvenance.RecipientPreference"/>.
    /// </param>
    /// <remarks>
    /// The default is the first provenance that can promote anything at all. Below it sit
    /// "no authentication", "it was delivered" and "nobody complained", the three labels an
    /// attacker generates simply by sending mail, and the three
    /// <see cref="BaselineLabelPolicy"/> already refuses. Checking them here as well is deliberate
    /// duplication: the profile's gate is the authority, and this one exists so that the cheapest
    /// possible attack on this system, claim a label you did not earn, is refused at the door
    /// with a reason, rather than travelling onwards to be refused quietly somewhere else.
    /// </remarks>
    public TrustedLearningGate(LabelProvenance minimumProvenance = LabelProvenance.RecipientPreference) =>
        _minimumProvenance = minimumProvenance;

    /// <summary>
    /// Decides whether a label may be offered to a profile at all.
    /// </summary>
    /// <returns>
    /// An outcome whose <see cref="TrustedLearningOutcome.Attempted"/> is false, with the reason, when
    /// the request is refused; one whose <c>Attempted</c> is true when it may proceed.
    /// </returns>
    public TrustedLearningOutcome Authorise(TrustedLearningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AssessmentOnly)
        {
            // Assessment-only calls are defined as not participating in live state, and the trusted
            // baseline is the most consequential piece of live state there is.
            return Declined("assessment-only calls do not commit learning");
        }

        if (!request.AuthorizedOutcomePresent && string.IsNullOrWhiteSpace(request.AuthorizedRuleId))
        {
            return Declined("no authorized outcome and no explicitly permitted rule");
        }

        if (request.ClaimedProvenance < _minimumProvenance)
        {
            return Declined(
                $"claimed provenance '{request.ClaimedProvenance}' is weaker than the minimum "
                + $"'{_minimumProvenance}' this deployment accepts");
        }

        return new TrustedLearningOutcome
        {
            Attempted = true,
            Promotion = null,
            Reason = request.AuthorizedRuleId is { Length: > 0 } rule
                ? $"authorized rule '{rule}'"
                : "authorized outcome",
        };
    }

    private static TrustedLearningOutcome Declined(string reason) =>
        new() { Attempted = false, Promotion = null, Reason = reason };
}
