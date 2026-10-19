using StyloMail.Core;

namespace StyloMail.Host.Contracts;

/// <summary>
/// Availability words shared by every dashboard summary response.
/// </summary>
/// <remarks>
/// These are strings rather than an enum because they are a wire contract the console matches on,
/// and a reordered enum member must never change what a client reads. <c>available</c> means every
/// series in the response was produced; <c>partial</c> means at least one was not and the response
/// names which; <c>not_produced</c> means none was.
/// </remarks>
public static class SummaryAvailability
{
    public const string Available = "available";

    public const string Partial = "partial";

    public const string NotProduced = "not_produced";
}

/// <summary>Named absence reasons. A reason, never a zero.</summary>
public static class SummaryReasons
{
    public const string QueueUnreadable = "queue_unreadable";

    public const string LedgerUnreadable = "ledger_unreadable";

    public const string DirectionNotRecorded = "direction_not_recorded";

    public const string NoProfileKey = "no_profile_key";

    public const string NoProfileRecorded = "no_profile_recorded";

    public const string NoStoredLabel = "no_stored_label";

    public const string DirectoryEvidenceNotComputed = "directory_evidence_not_computed";

    public const string RetentionHorizonNotRecorded = "retention_horizon_not_recorded";

    public const string CapacityNotMeasured = "capacity_not_measured";

    public const string SpoolNotMeasured = "spool_not_measured";

    public const string VolumeNotComputed = "volume_not_computed";

    public const string DirectionAgnosticScope = "direction_agnostic_scope";

    public const string SearchUnsupported = "search_unsupported";
}

/// <summary>One series the response did not produce, and why.</summary>
public sealed record SummaryAbsenceResponse
{
    public required string Series { get; init; }

    public required string Reason { get; init; }
}

// ---------------------------------------------------------------------------------------------
// GET /v1/overview/flow
// ---------------------------------------------------------------------------------------------

/// <summary>Counts for one time bucket. Every member is null only when its series is not produced.</summary>
public sealed record FlowBucketResponse
{
    public required DateTimeOffset BucketStart { get; init; }

    public int? Inbound { get; init; }

    public int? Outbound { get; init; }

    public int? Held { get; init; }

    public int? Review { get; init; }

    public int? Quarantined { get; init; }

    public int? Delivered { get; init; }

    public int? Rejected { get; init; }

    /// <summary>Accepted messages in none of the named dispositions (queued, delivering, retrying, failed).</summary>
    public int? Other { get; init; }
}

/// <summary>Window totals. The same series as <see cref="FlowBucketResponse"/>.</summary>
public sealed record FlowTotalsResponse
{
    public int? Inbound { get; init; }

    public int? Outbound { get; init; }

    public int? Held { get; init; }

    public int? Review { get; init; }

    public int? Quarantined { get; init; }

    public int? Delivered { get; init; }

    public int? Rejected { get; init; }

    public int? Other { get; init; }
}

/// <summary>What the flow aggregate could see.</summary>
public sealed record FlowCoverageResponse
{
    /// <summary>Accepted messages in the window. Messages, not recipients.</summary>
    public int? MessagesCounted { get; init; }

    /// <summary>Decision rows in the window, including rows with no accepted queue message.</summary>
    public int? DecisionsCounted { get; init; }

    /// <summary>
    /// Decisions whose direction is not recoverable because no accepted queue row carries it. They
    /// are never split between inbound and outbound.
    /// </summary>
    public int? DirectionNotRecoverable { get; init; }

    public required bool Complete { get; init; }

    public required IReadOnlyList<string> Tables { get; init; }

    public string? Note { get; init; }
}

/// <summary>The answer to <c>GET /v1/overview/flow</c>.</summary>
/// <remarks>
/// <para>
/// <b>Every accepted-message series counts messages accepted in the bucket</b>, and reports their
/// disposition as of <see cref="AsOf"/>. That basis is on the response rather than left to prose,
/// because "delivered" read as an event in the bucket and "delivered" read as the state of this
/// bucket's mail are different numbers and a chart cannot tell a reader which one it drew.
/// </para>
/// <para>
/// <b>Rejected is the exception and says so.</b> It counts decision-ledger rows whose action was
/// Reject in the bucket; those messages were refused before acceptance and are therefore not in the
/// queue series at all. Its basis travels separately in <see cref="RejectedBasis"/> so it can be
/// labelled rather than presented as another disposition of the same population.
/// </para>
/// <para>
/// <b>Zero is a measurement here.</b> The counts come from one indexed range over the window rather
/// than a truncated page, so an empty bucket is an empty bucket. Null is reserved for a series the
/// Host did not produce, which is also why <see cref="NotProduced"/> travels beside it.
/// </para>
/// </remarks>
public sealed record FlowSummaryResponse
{
    public required string TenantId { get; init; }

    public required string Window { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    public required DateTimeOffset AsOf { get; init; }

    public required int BucketMinutes { get; init; }

    public required string Availability { get; init; }

    /// <summary>The basis the accepted-message series use.</summary>
    public required string Basis { get; init; }

    /// <summary>The basis <c>rejected</c> uses, which is not the same population.</summary>
    public required string RejectedBasis { get; init; }

    public required FlowTotalsResponse Totals { get; init; }

    public required IReadOnlyList<FlowBucketResponse> Buckets { get; init; }

    public required FlowCoverageResponse Coverage { get; init; }

    public IReadOnlyList<SummaryAbsenceResponse> NotProduced { get; init; } = [];
}

// ---------------------------------------------------------------------------------------------
// GET /v1/queues/summary
// ---------------------------------------------------------------------------------------------

/// <summary>Current attention counts. Deliberately overlapping; see the route remarks.</summary>
public sealed record QueueCountsResponse
{
    /// <summary>Items in Held, Quarantined or Review: the union of the three below.</summary>
    public required int AwaitingDecision { get; init; }

    public required int Held { get; init; }

    public required int Quarantined { get; init; }

    public required int Review { get; init; }
}

/// <summary>The answer to <c>GET /v1/queues/summary</c>.</summary>
/// <remarks>
/// <b>AwaitingDecision is the union, so the four counts are not additive.</b> That is the same shape
/// the console's queue filters already have: "needs your decision" is the set of items in any
/// attention state. A stacked bar drawn from these numbers would double count, so the response says
/// so with <see cref="Overlapping"/> rather than leaving the client to discover it.
/// </remarks>
public sealed record QueueSummaryResponse
{
    public required string TenantId { get; init; }

    public required DateTimeOffset AsOf { get; init; }

    public required string Availability { get; init; }

    /// <summary><c>current_item_state</c>: item-level aggregate disposition, not recipient rows.</summary>
    public required string Basis { get; init; }

    public required bool Overlapping { get; init; }

    public required QueueCountsResponse Counts { get; init; }

    public required SummaryCoverageResponse Coverage { get; init; }
}

/// <summary>A generic "is this complete, and what should a reader know" note.</summary>
public sealed record SummaryCoverageResponse
{
    public required bool Complete { get; init; }

    public string? Note { get; init; }
}

// ---------------------------------------------------------------------------------------------
// GET /v1/storage/summary
// ---------------------------------------------------------------------------------------------

/// <summary>A capped row count. <see cref="Truncated"/> means the table holds more than <see cref="Count"/>.</summary>
public sealed record TableRowCountResponse
{
    public required string Table { get; init; }

    public required int Count { get; init; }

    public required bool Truncated { get; init; }

    public string? Reason { get; init; }
}

/// <summary>The answer to <c>GET /v1/storage/summary</c>. Sizes only; no path is ever served.</summary>
public sealed record StorageSummaryResponse
{
    public required string TenantId { get; init; }

    public required DateTimeOffset AsOf { get; init; }

    public required string Availability { get; init; }

    /// <summary>Bytes in the host's SQLite database file. Host-wide, not attributable to one tenant.</summary>
    public long? DatabaseBytes { get; init; }

    /// <summary>Bytes in the write-ahead log beside the database, when one is present.</summary>
    public long? WriteAheadLogBytes { get; init; }

    /// <summary>Bytes on disk under this tenant's own spool directory.</summary>
    public long? SpoolBytes { get; init; }

    /// <summary>Files counted under this tenant's spool directory.</summary>
    public int? SpoolFileCount { get; init; }

    /// <summary>Sum of unpurged payload bytes recorded for this tenant's accepted messages.</summary>
    public long? PayloadBytes { get; init; }

    /// <summary>Total size of the filesystem volume holding the spool. Not a StyloMail quota.</summary>
    public long? CapacityBytes { get; init; }

    public long? VolumeFreeBytes { get; init; }

    /// <summary>The scope words behind <see cref="CapacityBytes"/>: a volume figure, not a tenant quota.</summary>
    public required string CapacityScope { get; init; }

    /// <summary>Null with a named reason when no retention horizon is recorded.</summary>
    public DateTimeOffset? RetentionHorizon { get; init; }

    public string? RetentionReason { get; init; }

    public required IReadOnlyList<TableRowCountResponse> RowCounts { get; init; }

    public required StorageCoverageResponse Coverage { get; init; }

    public IReadOnlyList<SummaryAbsenceResponse> NotProduced { get; init; } = [];
}

/// <summary>What was measured for a storage answer.</summary>
public sealed record StorageCoverageResponse
{
    public required IReadOnlyList<string> Measured { get; init; }

    public string? Note { get; init; }
}

// ---------------------------------------------------------------------------------------------
// GET /v1/entities
// ---------------------------------------------------------------------------------------------

/// <summary>The volume a directory row carries. Lifetime profile observations, not a window.</summary>
public sealed record EntityVolumeResponse
{
    public int? Messages { get; init; }

    public int? Inbound { get; init; }

    public int? Outbound { get; init; }

    public required string Availability { get; init; }

    public required string Basis { get; init; }

    public string? Reason { get; init; }
}

/// <summary>One evidence member on a directory row. Always named when absent.</summary>
public sealed record EntityEvidenceMemberResponse
{
    public required string Availability { get; init; }

    public string? Label { get; init; }

    public string? Coverage { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// The per-row evidence summary.
/// </summary>
/// <remarks>
/// <b>All three members are not produced by this route, by design.</b> Each one is a KPI that
/// <c>GET /v1/profiles</c> computes for one entity over a stated window; computing six of them for
/// every row of a directory would turn a bounded listing into unbounded work. The members travel
/// with the row so a console renders "open the profile for evidence" rather than a zero.
/// </remarks>
public sealed record EntityEvidenceResponse
{
    public required EntityEvidenceMemberResponse Reply { get; init; }

    public required EntityEvidenceMemberResponse Pressure { get; init; }

    public required EntityEvidenceMemberResponse Identity { get; init; }
}

/// <summary>One directory row. No address, subject, filename or content.</summary>
public sealed record EntitySummaryResponse
{
    /// <summary>Opaque scope identifier: a principal id, a keyed profile hash, a domain key or a company id.</summary>
    public required string ScopeId { get; init; }

    public required string Kind { get; init; }

    /// <summary>Present where the scope is per direction; null where it is direction-agnostic.</summary>
    public MailDirection? Direction { get; init; }

    /// <summary>An operator label, a domain key or a company name. Never derived from content or a hash.</summary>
    public string? DisplayLabel { get; init; }

    public required string DisplayLabelAvailability { get; init; }

    public string? DisplayLabelReason { get; init; }

    public string? CompanyId { get; init; }

    public string? CompanyName { get; init; }

    public string? Domain { get; init; }

    public string? DomainReason { get; init; }

    public DateTimeOffset? LastContactAt { get; init; }

    public string? LastContactReason { get; init; }

    public required EntityVolumeResponse Volume { get; init; }

    public required EntityEvidenceResponse Evidence { get; init; }
}

/// <summary>What the directory page covered.</summary>
public sealed record EntityDirectoryCoverageResponse
{
    public required bool Complete { get; init; }

    /// <summary>Rows examined to fill this page, including any that a search filter rejected.</summary>
    public int? Scanned { get; init; }

    public string? Note { get; init; }
}

/// <summary>The answer to <c>GET /v1/entities</c>.</summary>
public sealed record EntityDirectoryResponse
{
    public required string TenantId { get; init; }

    public required string Kind { get; init; }

    public required string Availability { get; init; }

    public required IReadOnlyList<EntitySummaryResponse> Entities { get; init; }

    public string? NextCursor { get; init; }

    public required bool HasMore { get; init; }

    public required EntityDirectoryCoverageResponse Coverage { get; init; }

    public IReadOnlyList<SummaryAbsenceResponse> NotProduced { get; init; } = [];
}
