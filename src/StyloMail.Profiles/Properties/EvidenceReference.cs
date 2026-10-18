namespace StyloMail.Profiles.Properties;

/// <summary>What kind of thing an evidence reference points at.</summary>
/// <remarks>
/// A closed vocabulary rather than a free string: the console groups and labels evidence by kind,
/// and a kind nobody defined is a label nobody can render. <see cref="Other"/> exists so a producer
/// can cite something this build has no word for without inventing one, and it is rendered as
/// "other evidence" rather than guessed at.
/// </remarks>
public enum EvidenceKind
{
    Message = 0,
    ConversationTurn = 1,
    Conversation = 2,
    Profile = 3,
    Import = 4,
    ModelTrace = 5,
    Other = 6,
}

/// <summary>Whether a reviewer could actually see the cited evidence.</summary>
/// <remarks>
/// <b><see cref="NotEvaluated"/> is the honest default, not a pass.</b> The property store does not
/// enforce per-evidence access, so a reference it has not evaluated must never be presented as
/// available. Access is a decision for the subsystem that owns the evidence, and this field reports
/// what that subsystem said when the value was written.
/// </remarks>
public enum EvidenceAccessOutcome
{
    NotEvaluated = 0,
    Available = 1,
    Withheld = 2,
}

/// <summary>How long the cited evidence is kept.</summary>
/// <remarks>
/// <see cref="Unknown"/> is a real answer and the default. Retention is a property of the owning
/// subsystem's policy; the property store records the claim but never derives it.
/// </remarks>
public enum EvidenceRetention
{
    Unknown = 0,
    Retained = 1,
    Scheduled = 2,
}

/// <summary>
/// One piece of evidence a property rests on, with enough metadata to explain it without a second
/// call and without exposing content.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a bare identifier.</b> A drawer that can only render <c>msg_123</c> cannot tell an
/// operator whether the cited thing still exists, what it is, or whether they are allowed to see
/// it, so the reference carries the metadata the writer had at the time.
/// </para>
/// <para>
/// <b>No content, and no dereference.</b> A label is a short human name for the evidence, not its
/// body. A dereference route is a separate contract with its own access check; v1 explains the
/// item, it does not open it.
/// </para>
/// <para>
/// The store records these claims rather than verifying them: it has no way to know whether a
/// message was deleted or whether a reviewer holds the privilege to read it. That is why
/// <see cref="AccessOutcome"/> defaults to <see cref="EvidenceAccessOutcome.NotEvaluated"/> instead
/// of claiming availability.
/// </para>
/// </remarks>
public sealed record EvidenceReference
{
    public required string Id { get; init; }

    public EvidenceKind Kind { get; init; } = EvidenceKind.Other;

    /// <summary>A short human label. Never content, never a subject line.</summary>
    public string? Label { get; init; }

    /// <summary>When the evidence itself was observed, not when the property was written.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    public EvidenceRetention Retention { get; init; } = EvidenceRetention.Unknown;

    /// <summary>When retention ends, if the writer stated it.</summary>
    public DateTimeOffset? RetentionUntil { get; init; }

    public EvidenceAccessOutcome AccessOutcome { get; init; } = EvidenceAccessOutcome.NotEvaluated;
}

/// <summary>
/// The denominator behind a value's confidence.
/// </summary>
/// <remarks>
/// <para>
/// <b>A confidence without a denominator is not enough.</b> "0.82" over nine analysed messages and
/// "0.82" over three are different claims, and a drawer that cannot show which one it is invites an
/// operator to over-read the number.
/// </para>
/// <para>
/// A contribution either states its counts and window, or states a named reason it cannot. There is
/// no third shape in which the absence of counts reads as completeness.
/// </para>
/// </remarks>
public sealed record PropertyCoverage
{
    /// <summary>How many observations the contribution actually analysed.</summary>
    public int? Analysed { get; init; }

    /// <summary>How many were in scope.</summary>
    public int? Total { get; init; }

    public DateTimeOffset? WindowStart { get; init; }

    public DateTimeOffset? WindowEnd { get; init; }

    /// <summary>
    /// Why counts are not stated. When set, the counts above are absent by design rather than by
    /// omission.
    /// </summary>
    public string? UnavailableReason { get; init; }

    public bool HasCounts => Analysed is not null && Total is not null;
}

/// <summary>Whether a contribution exists, is not produced, or does not apply.</summary>
public enum AvailabilityState
{
    Available = 0,

    /// <summary>It should have been produced and was not. Always carries a reason.</summary>
    NotProduced = 1,

    /// <summary>The question does not apply to this kind of value. Always carries a reason.</summary>
    NotApplicable = 2,
}

/// <summary>A contribution's availability, and why when it is absent.</summary>
/// <remarks>
/// <b>An absent contribution must not read as a value or a zero.</b> These three states are the
/// difference between "nobody measured this", "the measurement should exist but does not" and "this
/// kind of value has no such measurement". A consumer that only branches on null cannot tell them
/// apart, which is why the state travels beside the optional payload.
/// </remarks>
public sealed record PropertyAvailability
{
    public required AvailabilityState State { get; init; }

    /// <summary>A stable machine-readable name. Null only when available.</summary>
    public string? Reason { get; init; }

    /// <summary>A human sentence. Never replaces the reason.</summary>
    public string? Detail { get; init; }

    public static PropertyAvailability Available { get; } =
        new() { State = AvailabilityState.Available };

    public static PropertyAvailability NotProduced(string reason, string detail) => new()
    {
        State = AvailabilityState.NotProduced,
        Reason = reason,
        Detail = detail,
    };

    public static PropertyAvailability NotApplicable(string reason, string detail) => new()
    {
        State = AvailabilityState.NotApplicable,
        Reason = reason,
        Detail = detail,
    };
}

/// <summary>Whether a value's confidence has a denominator the drawer can show.</summary>
public enum CoverageState
{
    /// <summary>Counts and window are stated.</summary>
    Available = 0,

    /// <summary>Counts should exist but do not. Always carries a reason.</summary>
    Unavailable = 1,

    /// <summary>The value is not evidence-derived, so it has no sample. Carries a reason.</summary>
    NotApplicable = 2,
}

/// <summary>The stable names used in <see cref="PropertyAvailability.Reason"/>.</summary>
public static class AvailabilityReasons
{
    public const string NoUnexpiredValue = "no_unexpired_value";
    public const string OnlyIneligibleCandidates = "only_ineligible_candidates";
    public const string SourceIsNotModelDerived = "source_is_not_model_derived";
    public const string ModelContributionMissing = "model_contribution_missing";
    public const string SourceIsNotEvidenceDerived = "source_is_not_evidence_derived";
    public const string CoverageMissing = "coverage_missing";
}
