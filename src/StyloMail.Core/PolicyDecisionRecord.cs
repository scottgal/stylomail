namespace StyloMail.Core;

/// <summary>One effective entity property used by a policy evaluation, as a replayable fact.</summary>
/// <remarks>
/// The value is the effective value, not a candidate. <see cref="State"/> and <see cref="Source"/>
/// are carried because "suggested" is not "declared" and an inferred value may raise a protection
/// without ever lowering one. Phase 2 records none of these, and the field is present and empty
/// rather than omitted.
/// </remarks>
public sealed record PolicyPropertyFact
{
    public required string Key { get; init; }

    public string? Value { get; init; }

    public required string State { get; init; }

    public string? Source { get; init; }

    public string? Scope { get; init; }
}

/// <summary>
/// The pack's evaluation recorded beside a decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a record, not an authorisation.</b> Phase 2 runs the evaluator as a second opinion:
/// <see cref="MailAssessment.Action"/> is the legacy engine's action, unchanged, and
/// <see cref="Shadow"/> is true. A reader must never infer that the pack's verdict was applied.
/// </para>
/// <para>
/// <b>Null on the assessment means no pack was active.</b> A configured pack that could not be read
/// is not null: it is a record with <see cref="PolicyVerdict.Review"/> and a named reason, so an
/// outage is distinguishable from "nothing was configured".
/// </para>
/// <para>
/// <b>This is distinct from <see cref="MailAssessment.ProposedActionInShadow"/>.</b> That field is
/// the legacy engine's candidate under the pre-existing shadow mode. This record is the pack
/// evaluator's candidate. One field carrying two engines' answers would make the console unable to
/// say which engine proposed what.
/// </para>
/// </remarks>
public sealed record PolicyDecisionRecord
{
    public required string PackId { get; init; }

    public required string PackVersion { get; init; }

    public required string RuleSetId { get; init; }

    public required string RuleSetVersion { get; init; }

    public required string ScorerVersion { get; init; }

    /// <summary>Version of the deterministic evaluator, never a model id.</summary>
    public required string EvaluatorVersion { get; init; }

    public required PolicyVerdict Verdict { get; init; }

    /// <summary>
    /// True when a human must see this case. For a Review verdict it is necessarily true; for an
    /// unavailable evaluation it records the escalation without claiming a rule fired.
    /// </summary>
    public required bool RequiresHumanReview { get; init; }

    public string? DecidingRuleId { get; init; }

    public int? DecidingRuleVersion { get; init; }

    /// <summary>The explicit band threshold that produced the verdict. Null when none did.</summary>
    public double? DecidingThreshold { get; init; }

    public required string DecidingReasonCode { get; init; }

    public required string DecidingReason { get; init; }

    /// <summary>Required signals that were not measured. Empty is a measured empty, not a zero.</summary>
    public required IReadOnlyList<string> MissingDimensions { get; init; }

    public required double Coverage { get; init; }

    /// <summary>Bindings that shaped the effective policy. Empty in Phase 2.</summary>
    public required IReadOnlyList<string> BindingIds { get; init; }

    /// <summary>Entity properties used. Empty in Phase 2, and present rather than omitted.</summary>
    public required IReadOnlyList<PolicyPropertyFact> PropertyFacts { get; init; }

    /// <summary>
    /// SHA-256 over the canonical evidence snapshot. Null when the evaluation did not run, because
    /// an uncomputed digest is not an empty one.
    /// </summary>
    public string? EvidenceSnapshotDigest { get; init; }

    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>True for a Phase 2 record: the verdict was recorded, not applied.</summary>
    public bool Shadow { get; init; }
}
