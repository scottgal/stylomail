using StyloMail.Core;
using StyloMail.Profiles.Kpis;
using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Profiles;

/// <summary>How one underlying signal behaved in the window.</summary>
/// <remarks>
/// Counts are nullable only where the measure itself can be absent. A count of zero is a real
/// observation (nothing was asked, nothing confirmed); an absent measure is null with a named
/// reason, never flattened to zero.
/// </remarks>
public sealed record KpiSignalEvidence(
    string SignalId,
    string Label,
    int Answered,
    int Confirming,
    int? Unavailable,
    int? NotApplicable,
    int Missing,
    string? Reason);

/// <summary>An opaque source row behind a KPI, with metadata and no content.</summary>
public sealed record EvidenceSourceRow(
    string AssessmentId,
    DateTimeOffset ObservedAt,
    MailDirection? Direction,
    MailAction Action,
    IReadOnlyList<string> MeasuredSignalIds);

/// <summary>The evidence behind one KPI value, bounded and content-free.</summary>
public sealed record KpiEvidenceRead(
    KpiValue Current,
    KpiValue Prior,
    IReadOnlyList<KpiSignalEvidence> Signals,
    IReadOnlyList<EvidenceSourceRow> Sources,
    int SourceCount,
    bool SourcesTruncated);

/// <summary>What a stored evidence reference resolved to.</summary>
public enum ReferenceOutcome
{
    Resolved = 0,

    /// <summary>The claim's own retention instant has passed.</summary>
    Expired = 1,

    /// <summary>A claim explicitly withholds access.</summary>
    Withheld = 2,

    /// <summary>No claim asserts access either way.</summary>
    NotEvaluated = 3,

    /// <summary>
    /// The kind has a ledger backend (Message) but the tenant no longer carries the row. The claim
    /// cannot be resolved to its source, so it is never Resolved.
    /// </summary>
    SourceMissing = 4,

    /// <summary>
    /// The claim exists but this kind has no verifiable backend here (conversation, profile, import,
    /// model trace). Existence cannot be checked, so it is not called Resolved.
    /// </summary>
    Unverified = 5,

    /// <summary>Matched storage held a malformed evidence array; it is named, not a SQL failure.</summary>
    Invalid = 6,
}

/// <summary>One property row that cites an evidence reference, with its own claim.</summary>
public sealed record ReferenceCitation(
    PropertyScope Scope,
    string Key,
    string Value,
    PropertySource Source,
    PropertySurfaceState State,
    string SetBy,
    DateTimeOffset SetAt,
    string? ConfirmedBy,
    DateTimeOffset? ConfirmedAt,
    EvidenceAccessOutcome AccessOutcome,
    EvidenceRetention Retention,
    DateTimeOffset? RetentionUntil);

/// <summary>Whether the cited row still exists in this tenant's ledger, and its metadata.</summary>
public sealed record ReferenceSource(
    string AssessmentId,
    DateTimeOffset ObservedAt,
    MailAction? Action);

/// <summary>
/// A resolved property evidence reference: the newest claim, every bounded citation with its own
/// claim, and whether the source row is present.
/// </summary>
/// <remarks>
/// <b>The top-level claim is explicitly the latest citation's claim, not "the" reference.</b> Claims
/// can disagree; <see cref="Citations"/> carries each one, and <see cref="Outcome"/> is the most
/// restrictive aggregate. <see cref="Selection"/> states that all matched citations were returned
/// (bounded), so a reader is not left to guess whether a sample was chosen.
/// </remarks>
public sealed record ReferenceEvidenceRead(
    EvidenceReference LatestClaim,
    ReferenceOutcome Outcome,
    string? OutcomeReason,
    string Selection,
    IReadOnlyList<ReferenceCitation> Citations,
    int CitationCount,
    int ReturnedCount,
    bool CitationsTruncated,
    int InvalidCitationRows,
    ReferenceSource? Source,
    bool SourcePresent);
