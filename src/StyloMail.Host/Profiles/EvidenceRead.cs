using StyloMail.Core;
using StyloMail.Profiles.Kpis;
using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Profiles;

/// <summary>How one underlying signal behaved in the window.</summary>
/// <remarks>
/// <see cref="Answered"/> counts rows that carried a usable value; <see cref="Confirming"/> counts
/// those that reached the definition's confirming threshold (or established the anomaly). A
/// signal that was never asked is <see cref="Missing"/>; one that was asked and failed is
/// <see cref="Unavailable"/>; one that does not apply is <see cref="NotApplicable"/>. These are
/// distinct because provider silence must never read as a negative.
/// </remarks>
public sealed record KpiSignalEvidence(
    string SignalId,
    string Label,
    int Answered,
    int Confirming,
    int Unavailable,
    int NotApplicable,
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
    Expired = 1,
    Withheld = 2,
    NotEvaluated = 3,
}

/// <summary>One property row that cites an evidence reference.</summary>
public sealed record ReferenceCitation(
    PropertyScope Scope,
    string Key,
    string Value,
    PropertySource Source,
    PropertySurfaceState State,
    string SetBy,
    DateTimeOffset SetAt,
    string? ConfirmedBy,
    DateTimeOffset? ConfirmedAt);

/// <summary>Whether the cited row still exists in this tenant's ledger, and its metadata.</summary>
public sealed record ReferenceSource(
    string AssessmentId,
    DateTimeOffset ObservedAt,
    MailAction? Action);

/// <summary>A resolved property evidence reference: metadata, citations and source existence.</summary>
public sealed record ReferenceEvidenceRead(
    EvidenceReference Reference,
    ReferenceOutcome Outcome,
    IReadOnlyList<ReferenceCitation> Citations,
    int CitationCount,
    bool CitationsTruncated,
    ReferenceSource? Source);
