namespace StyloMail.Desktop.Api.Contracts;

/// <summary>
/// The four read-only aggregates the dashboard consumes.
/// </summary>
/// <remarks>
/// <para>
/// Every numeric field is nullable. The Host's summary routes distinguish "not produced" from
/// "measured zero" by sending null for the first, and this client preserves that distinction all the
/// way to the screen: a null becomes "not measured" or a gap, never a zero.
/// </para>
/// <para>
/// The shape follows the served Host contract. The console adapts to the server, not the reverse.
/// </para>
/// </remarks>
public sealed record SummaryAbsence
{
    /// <summary>The series or source that was not produced.</summary>
    public required string Series { get; init; }

    /// <summary>The named reason, for example <c>queue_unreadable</c>.</summary>
    public required string Reason { get; init; }
}

public sealed record FlowResponse
{
    public required string TenantId { get; init; }

    /// <summary>The only accepted window for the dashboard: <c>last_24_hours</c>.</summary>
    public required string Window { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>Bucket width in minutes. Positive; the console labels the axis from it.</summary>
    public required int BucketMinutes { get; init; }

    /// <summary><c>available</c>, <c>partial</c> or <c>not_produced</c>.</summary>
    public string? Availability { get; init; }

    /// <summary>
    /// The instant the current dispositions were read. The flow is not a delivery timeline: a bucket
    /// counts messages accepted in that bucket and reports their disposition as of this instant.
    /// </summary>
    public DateTimeOffset? AsOf { get; init; }

    /// <summary>
    /// How to read the series, for example <c>accepted_in_window_current_disposition</c>. The console
    /// shows this rather than inventing "delivered today" from it.
    /// </summary>
    public string? Basis { get; init; }

    /// <summary>
    /// The basis of <c>rejected</c>, for example <c>rejected_decisions_in_bucket</c>. It is a
    /// different population from the accepted-message series and the console labels it that way.
    /// </summary>
    public string? RejectedBasis { get; init; }

    /// <summary>
    /// The series the Host did not produce, each with its reason. Availability is per series, not one
    /// global flag.
    /// </summary>
    public IReadOnlyList<SummaryAbsence> NotProduced { get; init; } = [];

    public required FlowCoverage Coverage { get; init; }

    public required FlowTotals Totals { get; init; }

    public required IReadOnlyList<FlowBucket> Buckets { get; init; }
}

/// <summary>What the flow aggregate covered.</summary>
public sealed record FlowCoverage
{
    public long? MessagesCounted { get; init; }

    public long? MessagesSkipped { get; init; }

    /// <summary>Whether the whole named window was counted, rather than a bounded sample.</summary>
    public bool Complete { get; init; }

    /// <summary>
    /// Rejected rows whose direction could not be recovered. They are counted once, never split by
    /// direction, and the console says so rather than guessing.
    /// </summary>
    public long? DirectionNotRecoverable { get; init; }

    /// <summary>Decision rows in the window, including rows with no accepted queue message.</summary>
    public long? DecisionsCounted { get; init; }

    /// <summary>The tables the aggregate read, as served.</summary>
    public IReadOnlyList<string> Tables { get; init; } = [];

    public string? Note { get; init; }
}

/// <summary>Window totals. Every member is nullable for the same reason as the buckets.</summary>
public sealed record FlowTotals
{
    public long? Inbound { get; init; }

    public long? Outbound { get; init; }

    public long? Held { get; init; }

    public long? Delivered { get; init; }

    public long? Quarantined { get; init; }

    public long? Review { get; init; }

    /// <summary>Queued, delivering, retry-scheduled or terminal-failure.</summary>
    public long? Other { get; init; }

    public long? Rejected { get; init; }
}

/// <summary>
/// One named time bucket.
/// </summary>
/// <remarks>
/// A null numeric means the SERIES was not produced. A produced series sends a measured zero for an
/// empty bucket, and the chart draws through it. The console must not treat a zero as a gap.
/// </remarks>
public sealed record FlowBucket
{
    public required DateTimeOffset BucketStart { get; init; }

    public long? Inbound { get; init; }

    public long? Outbound { get; init; }

    public long? Held { get; init; }

    public long? Delivered { get; init; }

    public long? Quarantined { get; init; }

    public long? Review { get; init; }

    public long? Other { get; init; }

    public long? Rejected { get; init; }
}

/// <summary><c>GET /v1/queues/summary</c>: counts per attention state at one instant.</summary>
public sealed record QueueSummaryResponse
{
    public required string TenantId { get; init; }

    public required DateTimeOffset AsOf { get; init; }

    public required string Availability { get; init; }

    /// <summary>For example <c>current_item_state</c>.</summary>
    public string? Basis { get; init; }

    /// <summary>
    /// True when one count contains another. <c>awaitingDecision</c> is the union of the other three,
    /// so the console must not sum the rows or stack them.
    /// </summary>
    public bool Overlapping { get; init; }

    public required QueueCounts Counts { get; init; }

    public required QueueSummaryCoverage Coverage { get; init; }
}

/// <summary>Nullable counts. A null count is "not counted", never zero.</summary>
public sealed record QueueCounts
{
    public long? AwaitingDecision { get; init; }

    public long? Held { get; init; }

    public long? Quarantined { get; init; }

    public long? Review { get; init; }
}

public sealed record QueueSummaryCoverage
{
    public bool Complete { get; init; }

    public string? Note { get; init; }
}

/// <summary><c>GET /v1/storage/summary</c>: measured storage and its retention horizon.</summary>
public sealed record StorageSummaryResponse
{
    public required string TenantId { get; init; }

    public required DateTimeOffset AsOf { get; init; }

    public required string Availability { get; init; }

    public long? DatabaseBytes { get; init; }

    /// <summary>The database write-ahead log, when the Host measures it separately.</summary>
    public long? WriteAheadLogBytes { get; init; }

    /// <summary>This tenant's spool directory, not the whole volume.</summary>
    public long? SpoolBytes { get; init; }

    /// <summary>
    /// True when <see cref="SpoolBytes"/> is a lower bound because the walk hit its file cap.
    /// Null means the spool was not measured.
    /// </summary>
    public bool? SpoolTruncated { get; init; }

    /// <summary>Files counted under this tenant's spool directory.</summary>
    public long? SpoolFileCount { get; init; }

    /// <summary>The tenant's unpurged metadata sum.</summary>
    public long? PayloadBytes { get; init; }

    /// <summary>
    /// Filesystem capacity, not a StyloMail quota. A percentage from this is labelled "disk".
    /// </summary>
    public long? CapacityBytes { get; init; }

    /// <summary>Filesystem free space, not a StyloMail quota.</summary>
    public long? VolumeFreeBytes { get; init; }

    /// <summary>The scope words behind <see cref="CapacityBytes"/>, for example a volume figure.</summary>
    public string? CapacityScope { get; init; }

    public DateTimeOffset? RetentionHorizon { get; init; }

    /// <summary>Why the retention horizon is null, for example <c>retention_horizon_not_recorded</c>.</summary>
    public string? RetentionReason { get; init; }

    /// <summary>Capped per-table row counts. The Host sends a list, not one total.</summary>
    public IReadOnlyList<TableRowCount> RowCounts { get; init; } = [];

    public required StorageSummaryCoverage Coverage { get; init; }

    public IReadOnlyList<SummaryAbsence> NotProduced { get; init; } = [];
}

/// <summary>One table's capped row count.</summary>
public sealed record TableRowCount
{
    public required string Table { get; init; }

    /// <summary>
    /// Null when the table could not be read, with <see cref="Reason"/>. A readable empty table is 0.
    /// </summary>
    public long? Count { get; init; }

    /// <summary>True when the table holds more than <see cref="Count"/>.</summary>
    public required bool Truncated { get; init; }

    public string? Reason { get; init; }
}

public sealed record StorageSummaryCoverage
{
    public IReadOnlyList<string> Measured { get; init; } = [];

    public string? Note { get; init; }
}

/// <summary><c>GET /v1/entities</c>: a page of the tenant's entity directory.</summary>
public sealed record EntityDirectoryResponse
{
    public required string TenantId { get; init; }

    public required string Kind { get; init; }

    public required string Availability { get; init; }

    public required IReadOnlyList<EntityDirectoryRow> Entities { get; init; }

    public string? NextCursor { get; init; }

    public required bool HasMore { get; init; }

    public required EntityDirectoryCoverage Coverage { get; init; }

    public IReadOnlyList<SummaryAbsence> NotProduced { get; init; } = [];
}

public sealed record EntityDirectoryRow
{
    /// <summary>The opaque id the profile route accepts. Never decoded on the client.</summary>
    public required string ScopeId { get; init; }

    public required string Kind { get; init; }

    /// <summary>Present where the scope is per direction.</summary>
    public string? Direction { get; init; }

    /// <summary>The operator label when one exists; null means nobody has described it.</summary>
    public string? DisplayLabel { get; init; }

    public string? DisplayLabelAvailability { get; init; }

    /// <summary>Why the label is null, for example <c>address_not_in_bounded_scan</c>.</summary>
    public string? DisplayLabelReason { get; init; }

    /// <summary>
    /// Where the label came from: <c>sender_profile</c>, <c>queue_envelope_masked</c>,
    /// <c>domain</c> or <c>company</c>. Null when no label was produced.
    /// </summary>
    public string? DisplayLabelSource { get; init; }

    public string? CompanyId { get; init; }

    public string? CompanyName { get; init; }

    public string? Domain { get; init; }

    public string? DomainReason { get; init; }

    public DateTimeOffset? LastContactAt { get; init; }

    public string? LastContactReason { get; init; }

    /// <summary>Null for a lifetime observation count, which is not a window.</summary>
    public int? WindowDays { get; init; }

    public EntityVolume? Volume { get; init; }

    public EntityEvidence? Evidence { get; init; }
}

public sealed record EntityVolume
{
    public long? Messages { get; init; }

    public long? Inbound { get; init; }

    public long? Outbound { get; init; }

    /// <summary>For example <c>lifetime_profile_observations</c>. The console prints this, not "30 days".</summary>
    public string? Basis { get; init; }

    public required string Availability { get; init; }

    public string? Reason { get; init; }
}

public sealed record EntityEvidence
{
    public EntityEvidenceItem? Reply { get; init; }

    public EntityEvidenceItem? Pressure { get; init; }

    public EntityEvidenceItem? Identity { get; init; }

    /// <summary>
    /// Why the members are absent, for example <c>directory_evidence_not_computed</c>. This is a
    /// deliberate refusal, not a pending route, and the console says "open the profile for evidence".
    /// </summary>
    public string? Reason { get; init; }
}

public sealed record EntityEvidenceItem
{
    public string? Label { get; init; }

    public string? Coverage { get; init; }

    public required string Availability { get; init; }

    /// <summary>Why the member is absent, for example <c>directory_evidence_not_computed</c>.</summary>
    public string? Reason { get; init; }
}

public sealed record EntityDirectoryCoverage
{
    public bool Complete { get; init; }

    /// <summary>
    /// Raw pre-trim query result count, including the probe row that proves there is another page.
    /// It is not rows remaining and can exceed the page length by one. The console labels it
    /// "examined" if it shows it at all.
    /// </summary>
    public int? Scanned { get; init; }

    public string? Note { get; init; }
}

/// <summary><c>GET /v1/policy/reviews?state=open</c>: the review queue the dashboard counts.</summary>
/// <remarks>
/// The dashboard needs the page size and cursor only. The item type is deliberately small; the
/// review page in slice 3 will carry its own full contract so the two surfaces cannot disagree about
/// what a review row means.
/// </remarks>
public sealed record ReviewQueueResponse
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<ReviewQueueRow> Items { get; init; }

    public string? NextCursor { get; init; }

    public bool HasMore => NextCursor is not null;
}

public sealed record ReviewQueueRow
{
    public required string QueueId { get; init; }

    public required string InternalMessageId { get; init; }
}

/// <summary><c>GET /v1/proxy/connection-outcomes</c>: counted admission failures and timeouts.</summary>
/// <remarks>
/// The Host counts two reasons in aligned five-minute buckets. This is not a count of every opened
/// connection; it is the subset the proxy refuses or times out, and the console labels it that way.
/// </remarks>
public sealed record ConnectionOutcomesResponse
{
    public required string TenantId { get; init; }

    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset Through { get; init; }

    public required IReadOnlyList<ConnectionOutcomeBucket> Buckets { get; init; }
}

public sealed record ConnectionOutcomeBucket
{
    public required string Protocol { get; init; }

    public required string Outcome { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset BucketStart { get; init; }

    public required long Count { get; init; }
}
