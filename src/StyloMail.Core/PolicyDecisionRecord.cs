namespace StyloMail.Core;

/// <summary>Which path produced the policy record.</summary>
public enum PolicySource
{
    /// <summary>The tenant was opted in and a pack was used.</summary>
    Pack = 0,

    /// <summary>The separate pack shadow path ran; no action was applied.</summary>
    Shadow = 1,

    /// <summary>The pack was unavailable and the bounded fallback was attempted.</summary>
    Fallback = 2,

    /// <summary>The pack was evaluated but the global assessment shadow mode won.</summary>
    GlobalShadow = 3,
}

/// <summary>What happened at the durable admission boundary.</summary>
public enum AdmissionOutcome
{
    /// <summary>A pre-acceptance decline; acceptance must not be called.</summary>
    NotAttempted = 0,

    /// <summary>Acceptance succeeded and a durable queue row exists.</summary>
    Admitted = 1,

    /// <summary>Acceptance was attempted and refused; no durable row exists.</summary>
    Refused = 2,

    /// <summary>Assessment-only traffic; there is no delivery path.</summary>
    NotApplicableForAssessmentOnly = 3,
}

/// <summary>One effective entity property used by a policy evaluation, as a replayable fact.</summary>
/// <remarks>
/// The value is the effective value, not a candidate. <see cref="State"/> and <see cref="Source"/>
/// are carried because "suggested" is not "declared" and an inferred value may raise a protection
/// without ever lowering one. A read failure is distinct from a healthy Unknown.
/// </remarks>
public sealed record PolicyPropertyFact
{
    public required string Key { get; init; }

    public string? Value { get; init; }

    public required string State { get; init; }

    public string? Source { get; init; }

    public string? Scope { get; init; }

    /// <summary>e.g. <c>Available</c>, <c>NotProduced</c>, <c>NotApplicable</c>.</summary>
    public string? Availability { get; init; }

    public string? AvailabilityReason { get; init; }
}

/// <summary>One binding's contribution to the effective policy, kept for replay.</summary>
public sealed record PolicyBindingSnapshot
{
    public required string BindingId { get; init; }

    public required string Action { get; init; }

    public string? CategoryId { get; init; }

    public string? TargetLevel { get; init; }

    public required string ScopeKind { get; init; }

    public required string ScopeValue { get; init; }

    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required string OnExpiry { get; init; }

    /// <summary>True when this binding was selected for the category or the expiry hold.</summary>
    public required bool Selected { get; init; }
}

/// <summary>
/// The canonical effective policy input a decision was made from.
/// </summary>
/// <remarks>
/// Self-contained so a later reader can replay the levels without reading live property or binding
/// state. Levels are recorded by their stable names, keeping Core free of Policy's enum. This is a
/// different artefact from the evidence snapshot and has its own digest.
/// </remarks>
public sealed record PolicyEffectiveInputSnapshot
{
    public required string ResolverVersion { get; init; }

    public required IReadOnlyDictionary<string, string> Levels { get; init; }

    public required string Ceiling { get; init; }

    public required IReadOnlyList<string> ForcedReviewCategories { get; init; }

    /// <summary>Categories withheld because an expired HoldForReview binding still applies.</summary>
    public required IReadOnlyList<string> WithheldReviewCategories { get; init; }

    public required IReadOnlyList<PolicyPropertyFact> PropertyFacts { get; init; }

    public required IReadOnlyList<string> MissingPropertyKeys { get; init; }

    /// <summary>The addressed scope chain, narrowest first.</summary>
    public required IReadOnlyList<string> AddressedChain { get; init; }

    public required IReadOnlyList<PolicyBindingSnapshot> Bindings { get; init; }

    public required string Digest { get; init; }
}

/// <summary>
/// The pack's evaluation recorded beside a decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a record, not an authorisation.</b> Under shadow, <see cref="MailAssessment.Action"/>
/// is the legacy engine's action and <see cref="Shadow"/> is true. A reader must never infer that
/// the pack's verdict was applied.
/// </para>
/// <para>
/// <b>A null record means no pack was active or the pack path was not entered.</b> A configured pack
/// that could not be read is a Fallback record with a named reason, so an outage is distinguishable
/// from "nothing was configured".
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
    /// <summary>The path that produced this record: Pack, Shadow, Fallback or GlobalShadow.</summary>
    public required PolicySource Source { get; init; }

    // Pack/rule provenance. Null only on a Fallback record, where no pack was read; null is not an
    // empty version.
    public string? PackId { get; init; }

    public string? PackVersion { get; init; }

    public string? RuleSetId { get; init; }

    public string? RuleSetVersion { get; init; }

    public string? ScorerVersion { get; init; }

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

    /// <summary>Bindings that shaped the effective policy.</summary>
    public required IReadOnlyList<string> BindingIds { get; init; }

    /// <summary>Entity properties used. Present and empty when none were read.</summary>
    public required IReadOnlyList<PolicyPropertyFact> PropertyFacts { get; init; }

    /// <summary>
    /// SHA-256 over the canonical evidence snapshot. Null when the evaluation did not run, because
    /// an uncomputed digest is not an empty one.
    /// </summary>
    public string? EvidenceSnapshotDigest { get; init; }

    /// <summary>The self-contained effective input, when the authoritative path ran.</summary>
    public PolicyEffectiveInputSnapshot? EffectiveInput { get; init; }

    /// <summary>SHA-256 over <see cref="EffectiveInput"/>, distinct from the evidence digest.</summary>
    public string? EffectiveInputDigest { get; init; }

    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>The absolute review deadline the queue admission used. Null when the action is not Review.</summary>
    public DateTimeOffset? ReviewDeadline { get; init; }

    /// <summary>True when the record was produced under a shadow mode and no action was applied.</summary>
    public bool Shadow { get; init; }

    /// <summary>True when the tenant flag was on and a pack actually ran.</summary>
    public bool Authoritative { get; init; }

    /// <summary>True when the composed action differs from the legacy action.</summary>
    public bool PolicyContributed { get; init; }

    /// <summary>True when the policy intervention actually applied.</summary>
    public bool Applied { get; init; }

    /// <summary>True when acceptance succeeded and a durable queue row exists.</summary>
    public bool Durable { get; init; }

    public required AdmissionOutcome Admission { get; init; }

    /// <summary>True when a no-cache fallback was attempted.</summary>
    public bool FallbackAttempted { get; init; }

    /// <summary>True when no pack/rule could be read for a configured tenant.</summary>
    public bool PackUnavailable { get; init; }

    /// <summary>True when the pack and rule set came from the bounded cache.</summary>
    public bool PackFromCache { get; init; }

    /// <summary>Age of the cached pack at the decision instant, when it was cached.</summary>
    public TimeSpan? CacheAge { get; init; }

    public string? FallbackReason { get; init; }

    /// <summary>True when Review was unsupported before admission and Quarantine was composed.</summary>
    public bool ReviewStateUnavailable { get; init; }

    /// <summary>True when Review admission was attempted and refused; the result is Defer.</summary>
    public bool ReviewAdmissionRefused { get; init; }

    /// <summary>True when one or more required property facts could not be read.</summary>
    public bool PropertyFactsUnavailable { get; init; }

    /// <summary>Property keys that were required by rules and could not be read.</summary>
    public required IReadOnlyList<string> MissingPropertyKeys { get; init; }

    public MailAction? LegacyAction { get; init; }

    /// <summary>The composed candidate before global shadow or admission suppression.</summary>
    public MailAction? ComposedAction { get; init; }

    public MailAction? FinalAction { get; init; }
}
