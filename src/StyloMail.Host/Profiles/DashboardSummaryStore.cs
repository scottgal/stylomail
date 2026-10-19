using System.Globalization;
using Microsoft.Data.Sqlite;
using StyloMail.Core;
using StyloMail.Host.Contracts;
using StyloMail.Host.Storage;
using StyloMail.Queue;

namespace StyloMail.Host.Profiles;

/// <summary>One flow window the route accepts, with its bucket resolution.</summary>
public sealed record FlowWindow(string Name, TimeSpan Duration, int BucketMinutes)
{
    public static FlowWindow? Parse(string? value) => value switch
    {
        null or "" => Last24Hours,
        "last_24_hours" => Last24Hours,
        "last_7_days" => Last7Days,
        _ => null,
    };

    public static FlowWindow Last24Hours { get; } =
        new("last_24_hours", TimeSpan.FromHours(24), 60);

    public static FlowWindow Last7Days { get; } =
        new("last_7_days", TimeSpan.FromDays(7), 1440);
}

/// <summary>
/// The three read-only dashboard aggregates: mail flow, queue attention counts and storage sizes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, tenant-scoped, and bounded by an index range.</b> The flow aggregate reads accepted
/// messages over the window through <c>ix_queue_item_tenant_created</c>; the queue summary reads
/// only the three attention states through <c>ix_queue_item_tenant</c>; the storage row counts are
/// capped queries. None of these scans another tenant's rows, and none of them enumerates a table
/// to produce a total.
/// </para>
/// <para>
/// <b>Missing is not zero.</b> A series the Host could not produce is null and named in
/// <c>notProduced</c>. A measured zero stays zero, including an empty bucket, because these counts
/// are complete aggregates rather than a truncated page.
/// </para>
/// </remarks>
public sealed class DashboardSummaryStore
{
    /// <summary>The basis every flow series uses. On the wire, because a chart must label it.</summary>
    public const string FlowBasis = "accepted_in_window_current_disposition";

    /// <summary>
    /// Current counts are per recipient state within the attention population, the same facts the
    /// queue listing filters answer.
    /// </summary>
    public const string QueueBasis = "current_attention_recipient_state";

    /// <summary>
    /// Rejected is the one series that is not an accepted-message disposition: it counts decisions
    /// whose action was Reject, which were refused before acceptance.
    /// </summary>
    public const string RejectedBasis = "rejected_decisions_in_bucket";

    /// <summary>Cap for a single table's row count. Hitting it reports "more than", never the cap as a total.</summary>
    public const int RowCountCap = 10_000;

    /// <summary>Cap for the tenant's spool walk. A length-bounded diagnostic, not an inventory.</summary>
    public const int SpoolFileCap = 100_000;

    /// <summary>
    /// Accepted messages in the window grouped by direction and aggregate state.
    /// </summary>
    /// <remarks>
    /// <c>strftime('%s', ...)</c> converts the stored ISO instant to epoch seconds so the bucket is
    /// integer arithmetic. The WHERE clause is what the covering index serves; the conversion runs
    /// only over the rows already inside the window.
    /// </remarks>
    internal const string FlowQueueSql =
        """
        SELECT (CAST(strftime('%s', created_at) AS INTEGER) - $from) / $bucket AS bucket_index,
               direction, state, COUNT(*)
          FROM queue_item
         WHERE tenant_id = $tenant
           AND created_at >= $from_text
           AND created_at <= $to_text
         GROUP BY bucket_index, direction, state;
        """;

    /// <summary>Refused decisions in the window. They were never accepted, so they are not queue rows.</summary>
    internal const string FlowRejectedSql =
        """
        SELECT (CAST(strftime('%s', recorded_at) AS INTEGER) - $from) / $bucket AS bucket_index,
               COUNT(*)
          FROM host_decision_ledger
         WHERE tenant_id = $tenant
           AND recorded_at >= $from_text
           AND recorded_at <= $to_text
           AND action = $reject
         GROUP BY bucket_index;
        """;

    /// <summary>
    /// Decisions in the window and how many have no accepted queue row.
    /// </summary>
    /// <remarks>
    /// The missing row is the direction gap: the ledger does not persist the envelope's direction,
    /// so a decision that never reached the queue can only be counted, never split inbound or
    /// outbound. The LEFT JOIN is index-driven by <c>ix_queue_item_tenant_message</c>.
    /// </remarks>
    internal const string FlowDirectionGapSql =
        """
        SELECT COUNT(*),
               COALESCE(SUM(CASE WHEN q.queue_id IS NULL THEN 1 ELSE 0 END), 0)
          FROM host_decision_ledger l
          LEFT JOIN queue_item q
            ON q.tenant_id = l.tenant_id AND q.internal_message_id = l.internal_message_id
         WHERE l.tenant_id = $tenant
           AND l.recorded_at >= $from_text
           AND l.recorded_at <= $to_text;
        """;

    /// <summary>
    /// Items currently in an attention state, with per-state counts.
    /// </summary>
    /// <remarks>
    /// <b>The counts deliberately overlap.</b> An item with one held recipient and one quarantined
    /// recipient appears in both those counts, because that is what the queue's own filters do: a
    /// reviewer opening "being re-checked" sees it and so does a reviewer opening "stopped before
    /// delivery". <c>awaitingDecision</c> is the union, so it is not the sum of the other three.
    /// The outer predicate uses the item's rolled-up state to reach the attention population
    /// through the tenant index; the inner checks are the exact per-recipient states.
    /// </remarks>
    internal const string QueueSummarySql =
        """
        SELECT COUNT(*),
               COALESCE(SUM(CASE WHEN EXISTS (
                   SELECT 1 FROM queue_recipient r
                    WHERE r.queue_id = i.queue_id AND r.state = $held) THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN EXISTS (
                   SELECT 1 FROM queue_recipient r
                    WHERE r.queue_id = i.queue_id AND r.state = $quarantined) THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN EXISTS (
                   SELECT 1 FROM queue_recipient r
                    WHERE r.queue_id = i.queue_id AND r.state = $review) THEN 1 ELSE 0 END), 0)
          FROM queue_item i
         WHERE i.tenant_id = $tenant
           AND i.state IN ($held, $quarantined, $review);
        """;

    /// <summary>Unpurged payload bytes recorded for this tenant.</summary>
    internal const string PayloadBytesSql =
        """
        SELECT COALESCE(SUM(payload_bytes), 0)
          FROM queue_item
         WHERE tenant_id = $tenant AND purged_at IS NULL;
        """;

    private static readonly (string Table, string Sql)[] RowCountQueries =
    [
        ("host_decision_ledger",
            "SELECT COUNT(*) FROM (SELECT 1 FROM host_decision_ledger "
            + "WHERE tenant_id = $tenant LIMIT $cap);"),
        ("queue_item",
            "SELECT COUNT(*) FROM (SELECT 1 FROM queue_item "
            + "WHERE tenant_id = $tenant LIMIT $cap);"),
        ("queue_recipient",
            "SELECT COUNT(*) FROM (SELECT 1 FROM queue_recipient r "
            + "JOIN queue_item i ON i.queue_id = r.queue_id "
            + "WHERE i.tenant_id = $tenant LIMIT $cap);"),
        ("sender_profile",
            "SELECT COUNT(*) FROM (SELECT 1 FROM sender_profile "
            + "WHERE tenant_id = $tenant LIMIT $cap);"),
        ("company",
            "SELECT COUNT(*) FROM (SELECT 1 FROM company "
            + "WHERE tenant_id = $tenant LIMIT $cap);"),
        ("profiles",
            "SELECT COUNT(*) FROM (SELECT 1 FROM profiles "
            + "WHERE tenant_id = $tenant LIMIT $cap);"),
        ("host_entity_property",
            "SELECT COUNT(*) FROM (SELECT 1 FROM host_entity_property "
            + "WHERE tenant_id = $tenant LIMIT $cap);"),
    ];

    /// <summary>
    /// The row-count cap this instance uses. <see cref="RowCountCap"/> in every deployment;
    /// overridable inside the test assembly so the truncation boundary can be exercised without
    /// seeding ten thousand rows.
    /// </summary>
    internal int RowCountLimit { get; init; } = RowCountCap;

    /// <summary>The spool file cap this instance uses. Overridable for the truncation test.</summary>
    internal int SpoolFileLimit { get; init; } = SpoolFileCap;

    private readonly HostDatabase _database;
    private readonly SpoolStore _spool;
    private readonly TimeProvider _clock;

    public DashboardSummaryStore(HostDatabase database, SpoolStore spool, TimeProvider clock)
    {
        _database = database;
        _spool = spool;
        _clock = clock;
    }

    /// <summary>Exposed for the query-plan tests, which assert each aggregate is index-driven.</summary>
    internal static IReadOnlyList<(string Table, string Sql)> RowCountSql => RowCountQueries;

    // ---------------------------------------------------------------------------------------------
    // Flow
    // ---------------------------------------------------------------------------------------------

    public FlowSummaryResponse ReadFlow(
        string tenantId,
        FlowWindow window,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(window);
        cancellationToken.ThrowIfCancellationRequested();

        var asOf = _clock.GetUtcNow();
        var windowEnd = asOf;
        var windowStart = windowEnd - window.Duration;

        var bucketSeconds = window.BucketMinutes * 60L;
        var bucketCount = (int)Math.Ceiling(window.Duration.TotalSeconds / bucketSeconds);

        var buckets = new FlowBucketResponse[bucketCount];
        for (var index = 0; index < bucketCount; index++)
        {
            buckets[index] = new FlowBucketResponse
            {
                BucketStart = windowStart.AddMinutes((double)index * window.BucketMinutes),
                Inbound = 0,
                Outbound = 0,
                Held = 0,
                Review = 0,
                Quarantined = 0,
                Delivered = 0,
                Other = 0,
                // Rejected is a measured zero here. The ledger-failure path below nulls every
                // bucket, so a null always means "the ledger could not be read", never "no rejects
                // happened in this bucket".
                Rejected = 0,
            };
        }

        var notProduced = new List<SummaryAbsenceResponse>();
        var tables = new List<string>();
        int? messagesCounted = null;
        int? decisionsCounted = null;
        int? directionGap = null;
        var queueProduced = false;
        var ledgerProduced = false;
        var gapProduced = false;
        string? note = null;

        var fromEpoch = windowStart.ToUnixTimeSeconds();
        var fromText = QueueStamp(windowStart);
        var toText = QueueStamp(windowEnd);
        var ledgerFromText = LedgerStamp(windowStart);
        var ledgerToText = LedgerStamp(windowEnd);

        try
        {
            using var connection = _database.Open();

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = FlowQueueSql;
                command.Parameters.AddWithValue("$tenant", tenantId);
                command.Parameters.AddWithValue("$from", fromEpoch);
                command.Parameters.AddWithValue("$bucket", bucketSeconds);
                command.Parameters.AddWithValue("$from_text", fromText);
                command.Parameters.AddWithValue("$to_text", toText);

                var total = 0;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // A row exactly at windowEnd belongs in the last bucket: the window closes
                    // inclusively so "the last 24 hours" includes the instant it was asked at.
                    var index = Math.Clamp(reader.GetInt32(0), 0, buckets.Length - 1);

                    var direction = (MailDirection)reader.GetInt32(1);
                    var state = (DeliveryState)reader.GetInt32(2);
                    var count = reader.GetInt32(3);
                    total += count;

                    var bucket = buckets[index];
                    buckets[index] = direction switch
                    {
                        MailDirection.Inbound => bucket with
                        {
                            Inbound = (bucket.Inbound ?? 0) + count,
                            Held = StateIsHeld(state) ? (bucket.Held ?? 0) + count : bucket.Held ?? 0,
                            Review = state == DeliveryState.Review ? (bucket.Review ?? 0) + count : bucket.Review ?? 0,
                            Quarantined = state == DeliveryState.Quarantined
                                ? (bucket.Quarantined ?? 0) + count
                                : bucket.Quarantined ?? 0,
                            Delivered = state == DeliveryState.Delivered
                                ? (bucket.Delivered ?? 0) + count
                                : bucket.Delivered ?? 0,
                            Other = IsOtherState(state) ? (bucket.Other ?? 0) + count : bucket.Other ?? 0,
                        },
                        MailDirection.Outbound => bucket with
                        {
                            Outbound = (bucket.Outbound ?? 0) + count,
                            Held = StateIsHeld(state) ? (bucket.Held ?? 0) + count : bucket.Held ?? 0,
                            Review = state == DeliveryState.Review ? (bucket.Review ?? 0) + count : bucket.Review ?? 0,
                            Quarantined = state == DeliveryState.Quarantined
                                ? (bucket.Quarantined ?? 0) + count
                                : bucket.Quarantined ?? 0,
                            Delivered = state == DeliveryState.Delivered
                                ? (bucket.Delivered ?? 0) + count
                                : bucket.Delivered ?? 0,
                            Other = IsOtherState(state) ? (bucket.Other ?? 0) + count : bucket.Other ?? 0,
                        },
                        _ => bucket,
                    };
                }

                messagesCounted = total;
                queueProduced = true;
                tables.Add("queue_item");
            }
            catch (SqliteException)
            {
                notProduced.AddRange(
                [
                    new SummaryAbsenceResponse { Series = "inbound", Reason = SummaryReasons.QueueUnreadable },
                    new SummaryAbsenceResponse { Series = "outbound", Reason = SummaryReasons.QueueUnreadable },
                    new SummaryAbsenceResponse { Series = "held", Reason = SummaryReasons.QueueUnreadable },
                    new SummaryAbsenceResponse { Series = "review", Reason = SummaryReasons.QueueUnreadable },
                    new SummaryAbsenceResponse { Series = "quarantined", Reason = SummaryReasons.QueueUnreadable },
                    new SummaryAbsenceResponse { Series = "delivered", Reason = SummaryReasons.QueueUnreadable },
                    new SummaryAbsenceResponse { Series = "other", Reason = SummaryReasons.QueueUnreadable },
                ]);
            }

            try
            {
                using (var rejected = connection.CreateCommand())
                {
                    rejected.CommandText = FlowRejectedSql;
                    rejected.Parameters.AddWithValue("$tenant", tenantId);
                    rejected.Parameters.AddWithValue("$from", fromEpoch);
                    rejected.Parameters.AddWithValue("$bucket", bucketSeconds);
                    rejected.Parameters.AddWithValue("$from_text", ledgerFromText);
                    rejected.Parameters.AddWithValue("$to_text", ledgerToText);
                    rejected.Parameters.AddWithValue("$reject", MailAction.Reject.ToString());

                    using var reader = rejected.ExecuteReader();
                    while (reader.Read())
                    {
                        var index = Math.Clamp(reader.GetInt32(0), 0, buckets.Length - 1);

                        buckets[index] = buckets[index] with
                        {
                            Rejected = (buckets[index].Rejected ?? 0) + reader.GetInt32(1),
                        };
                    }
                }

                ledgerProduced = true;
                tables.Add("host_decision_ledger");
            }
            catch (SqliteException)
            {
                notProduced.Add(new SummaryAbsenceResponse
                {
                    Series = "rejected",
                    Reason = SummaryReasons.LedgerUnreadable,
                });
            }

            // Separately, because the gap needs BOTH tables: the decision count comes from the
            // ledger and the missing-row test joins the queue. A queue failure must not cost the
            // rejected series as well, and a gap that could not be measured must not render as 0.
            try
            {
                using var gap = connection.CreateCommand();
                gap.CommandText = FlowDirectionGapSql;
                gap.Parameters.AddWithValue("$tenant", tenantId);
                gap.Parameters.AddWithValue("$from_text", ledgerFromText);
                gap.Parameters.AddWithValue("$to_text", ledgerToText);

                using var reader = gap.ExecuteReader();
                if (reader.Read())
                {
                    decisionsCounted = reader.GetInt32(0);
                    directionGap = reader.GetInt32(1);
                }

                gapProduced = true;
            }
            catch (SqliteException)
            {
                notProduced.Add(new SummaryAbsenceResponse
                {
                    Series = "directionNotRecoverable",
                    Reason = queueProduced
                        ? SummaryReasons.LedgerUnreadable
                        : SummaryReasons.QueueUnreadable,
                });
            }

            if (!queueProduced)
            {
                // No inbound/outbound/delivered/held series exists to sum. Every queue-derived
                // bucket field is null rather than the zero it was initialised to.
                for (var index = 0; index < buckets.Length; index++)
                {
                    buckets[index] = buckets[index] with
                    {
                        Inbound = null,
                        Outbound = null,
                        Held = null,
                        Review = null,
                        Quarantined = null,
                        Delivered = null,
                        Other = null,
                    };
                }
            }

            if (!ledgerProduced)
            {
                for (var index = 0; index < buckets.Length; index++)
                {
                    buckets[index] = buckets[index] with { Rejected = null };
                }
            }

            if (gapProduced && directionGap is > 0)
            {
                // Named on the response, not left for a reader to infer from totals that do not
                // reconcile. These decisions have no direction and are never split by one.
                note = $"{directionGap} decision(s) in this window have no accepted queue row, so "
                    + "their direction is not recoverable. They are counted in rejected only when "
                    + "the decision was Reject, and are never attributed to inbound or outbound.";
            }

            if (!queueProduced && !ledgerProduced && !gapProduced)
            {
                throw new StorageUnavailableException(
                    "Neither the queue nor the decision ledger could be read, so no flow is produced.");
            }
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The dashboard summary could not be read.", ex);
        }

        var totals = new FlowTotalsResponse
        {
            Inbound = queueProduced ? buckets.Sum(bucket => bucket.Inbound ?? 0) : null,
            Outbound = queueProduced ? buckets.Sum(bucket => bucket.Outbound ?? 0) : null,
            Held = queueProduced ? buckets.Sum(bucket => bucket.Held ?? 0) : null,
            Review = queueProduced ? buckets.Sum(bucket => bucket.Review ?? 0) : null,
            Quarantined = queueProduced ? buckets.Sum(bucket => bucket.Quarantined ?? 0) : null,
            Delivered = queueProduced ? buckets.Sum(bucket => bucket.Delivered ?? 0) : null,
            Other = queueProduced ? buckets.Sum(bucket => bucket.Other ?? 0) : null,
            Rejected = ledgerProduced ? buckets.Sum(bucket => bucket.Rejected ?? 0) : null,
        };

        return new FlowSummaryResponse
        {
            TenantId = tenantId,
            Window = window.Name,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            AsOf = asOf,
            BucketMinutes = window.BucketMinutes,
            Availability = queueProduced && ledgerProduced && gapProduced
                ? SummaryAvailability.Available
                : SummaryAvailability.Partial,
            Basis = FlowBasis,
            RejectedBasis = RejectedBasis,
            Totals = totals,
            Buckets = buckets,
            Coverage = new FlowCoverageResponse
            {
                MessagesCounted = messagesCounted,
                DecisionsCounted = decisionsCounted,
                DirectionNotRecoverable = directionGap,
                Complete = queueProduced && ledgerProduced && gapProduced,
                Tables = tables,
                Note = note,
            },
            NotProduced = notProduced,
        };
    }

    private static bool StateIsHeld(DeliveryState state) => state == DeliveryState.Held;

    private static bool IsOtherState(DeliveryState state) =>
        state is DeliveryState.Queued or DeliveryState.Delivering
            or DeliveryState.RetryScheduled or DeliveryState.TerminalFailure;

    // ---------------------------------------------------------------------------------------------
    // Queues
    // ---------------------------------------------------------------------------------------------

    public QueueSummaryResponse ReadQueues(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();

        var asOf = _clock.GetUtcNow();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = QueueSummarySql;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$held", (int)DeliveryState.Held);
            command.Parameters.AddWithValue("$quarantined", (int)DeliveryState.Quarantined);
            command.Parameters.AddWithValue("$review", (int)DeliveryState.Review);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new StorageUnavailableException("The queue summary returned no row.");
            }

            var awaitingDecision = reader.GetInt32(0);
            var held = reader.GetInt32(1);
            var quarantined = reader.GetInt32(2);
            var review = reader.GetInt32(3);

            return new QueueSummaryResponse
            {
                TenantId = tenantId,
                AsOf = asOf,
                Availability = SummaryAvailability.Available,
                Basis = QueueBasis,
                Overlapping = true,
                Counts = new QueueCountsResponse
                {
                    AwaitingDecision = awaitingDecision,
                    Held = held,
                    Quarantined = quarantined,
                    Review = review,
                },
                Coverage = new SummaryCoverageResponse
                {
                    Complete = true,
                    Note = "Current snapshot. awaitingDecision is the union of held, quarantined and "
                        + "review, so the four counts are not additive: an item with recipients in "
                        + "two attention states appears in both. Counts are per recipient state and "
                        + "match the queue listing filters.",
                },
            };
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The queue summary could not be read.", ex);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Storage
    // ---------------------------------------------------------------------------------------------

    public StorageSummaryResponse ReadStorage(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();

        var asOf = _clock.GetUtcNow();
        var notProduced = new List<SummaryAbsenceResponse>();
        var measured = new List<string>();

        long? databaseBytes = null;
        long? walBytes = null;
        long? spoolBytes = null;
        int? spoolFileCount = null;
        bool? spoolTruncated = null;
        long? payloadBytes = null;
        long? capacityBytes = null;
        long? volumeFreeBytes = null;
        string capacityScope = "filesystem_volume";
        var rowCounts = new List<TableRowCountResponse>();

        try
        {
            var database = new FileInfo(_database.DatabasePath);
            if (database.Exists)
            {
                databaseBytes = database.Length;
                measured.Add("database");
            }
            else
            {
                notProduced.Add(new SummaryAbsenceResponse
                {
                    Series = "databaseBytes",
                    Reason = SummaryReasons.VolumeNotComputed,
                });
            }

            var wal = new FileInfo(_database.DatabasePath + "-wal");
            // Absent WAL is a measured zero: this deployment is not in a write transaction.
            walBytes = wal.Exists ? wal.Length : 0;
            measured.Add("write_ahead_log");
        }
        catch (IOException)
        {
            notProduced.Add(new SummaryAbsenceResponse
            {
                Series = "databaseBytes",
                Reason = SummaryReasons.VolumeNotComputed,
            });
        }
        catch (UnauthorizedAccessException)
        {
            notProduced.Add(new SummaryAbsenceResponse
            {
                Series = "databaseBytes",
                Reason = SummaryReasons.VolumeNotComputed,
            });
        }

        try
        {
            var usage = _spool.MeasureTenantBytes(tenantId, SpoolFileLimit);
            spoolBytes = usage.Bytes;
            spoolFileCount = usage.FileCount;
            spoolTruncated = usage.Truncated;
            measured.Add("spool");
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            notProduced.Add(new SummaryAbsenceResponse
            {
                Series = "spoolBytes",
                Reason = SummaryReasons.SpoolNotMeasured,
            });
        }

        SqliteConnection? connection = null;
        try
        {
            connection = _database.Open();
        }
        catch (SqliteException ex)
        {
            // The file cannot be opened at all. That is an outage rather than a partial answer.
            throw new StorageUnavailableException(
                "The storage metadata could not be read, so no byte totals are being given.", ex);
        }

        using (connection)
        {
            try
            {
                using var payload = connection.CreateCommand();
                payload.CommandText = PayloadBytesSql;
                payload.Parameters.AddWithValue("$tenant", tenantId);
                payloadBytes = Convert.ToInt64(
                    payload.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
                measured.Add("payloads");
            }
            catch (SqliteException)
            {
                // One absent table is not an outage. The byte total is null and named, because a
                // zero would read as a measured empty queue.
                notProduced.Add(new SummaryAbsenceResponse
                {
                    Series = "payloadBytes",
                    Reason = "table_unreadable",
                });
            }

            foreach (var (table, sql) in RowCountQueries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var count = connection.CreateCommand();
                    count.CommandText = sql;
                    count.Parameters.AddWithValue("$tenant", tenantId);
                    count.Parameters.AddWithValue("$cap", RowCountLimit + 1);

                    var raw = Convert.ToInt32(count.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
                    rowCounts.Add(new TableRowCountResponse
                    {
                        Table = table,
                        Count = Math.Min(raw, RowCountLimit),
                        Truncated = raw > RowCountLimit,
                    });
                }
                catch (SqliteException)
                {
                    // A table that is absent is named, not reported as empty. Count stays null so
                    // it cannot be read as a measured zero, and the rest of the answer stands.
                    rowCounts.Add(new TableRowCountResponse
                    {
                        Table = table,
                        Count = null,
                        Truncated = false,
                        Reason = "table_unreadable",
                    });
                    notProduced.Add(new SummaryAbsenceResponse
                    {
                        Series = $"rowCounts.{table}",
                        Reason = "table_unreadable",
                    });
                }
            }

            measured.Add("row_counts");
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_spool.Root));
            if (!string.IsNullOrEmpty(root))
            {
                var volume = new DriveInfo(root);
                if (volume.IsReady)
                {
                    capacityBytes = volume.TotalSize;
                    volumeFreeBytes = volume.AvailableFreeSpace;
                    measured.Add("volume");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // The two fields stay null and the response says why. A capacity is never guessed.
        }

        if (capacityBytes is null)
        {
            notProduced.Add(new SummaryAbsenceResponse
            {
                Series = "capacityBytes",
                Reason = SummaryReasons.CapacityNotMeasured,
            });
            capacityScope = "not_measured";
        }

        var availability = notProduced.Count == 0
            ? SummaryAvailability.Available
            : SummaryAvailability.Partial;

        return new StorageSummaryResponse
        {
            TenantId = tenantId,
            AsOf = asOf,
            Availability = availability,
            DatabaseBytes = databaseBytes,
            WriteAheadLogBytes = walBytes,
            SpoolBytes = spoolBytes,
            SpoolFileCount = spoolFileCount,
            SpoolTruncated = spoolTruncated,
            PayloadBytes = payloadBytes,
            CapacityBytes = capacityBytes,
            VolumeFreeBytes = volumeFreeBytes,
            CapacityScope = capacityScope,
            RetentionHorizon = null,
            RetentionReason = SummaryReasons.RetentionHorizonNotRecorded,
            RowCounts = rowCounts,
            Coverage = new StorageCoverageResponse
            {
                Measured = measured,
                Note = "databaseBytes is the host's SQLite main file and excludes the write-ahead "
                    + "log, which is reported separately as writeAheadLogBytes; neither is a "
                    + "per-tenant share. spoolBytes is this tenant's own spool directory. "
                    + "capacityBytes and volumeFreeBytes describe the filesystem volume, not a "
                    + "StyloMail quota. rowCounts are capped at "
                    + $"{RowCountLimit}; a capped row reports truncated, never the cap as a total. "
                    + "An unreadable row count is null with a reason, never zero. spoolTruncated=true "
                    + "means spoolBytes is a lower bound: the walk stopped at its file cap. "
                    + "No path is served.",
            },
            NotProduced = notProduced,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Timestamps
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The queue stores <c>UtcDateTime.ToString("O")</c>, which ends in <c>Z</c>. Text comparison
    /// is safe only against the same format, so the bound is formatted here rather than reused
    /// from the ledger, whose stamps end in <c>+00:00</c>.
    /// </summary>
    private static string QueueStamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The ledger stores <c>ToUniversalTime().ToString("O")</c>, which ends in <c>+00:00</c>.</summary>
    private static string LedgerStamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
