using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Host.Proxy;
using StyloMail.Host.Serialization;

namespace StyloMail.Host.Storage;

/// <summary>
/// Image observation storage: one account-scoped content observation per observation_key, bounded
/// parts, usage accounting, epoch allocation and the rotating retention sweep.
/// </summary>
public sealed class SqliteProxyImageStore : IProxyImageObservationSink, IProxyImageObservationReader
{
    private const int MaximumParts = 2;
    private const int MaximumFindings = 16;
    private const int MaximumEvidenceRows = 13;
    private const int OutcomeOverheadBytes = 512;
    private const int ChildLimit = 8;
    private const int SweepPerTenant = 200;
    public const int DefaultMaximumOutcomesPerTenant = 250_000;
    public const long DefaultMaximumUsedBytesPerTenant = 256L * 1024 * 1024;

    private static readonly Regex SafeOpaque = new(
        "^[A-Za-z0-9._:-]{1,512}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SafeDetail = new(
        "^[A-Za-z0-9_=;.,:+\\-]{0,64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SafeFormat = new(
        "^[a-z0-9_+.-]{1,16}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SafeVersion = new(
        "^[a-z0-9/._-]{1,32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HostDatabase _database;
    private readonly int _retentionDays;
    private readonly int _maximumOutcomesPerTenant;
    private readonly long _maximumUsedBytesPerTenant;
    private long _epoch;
    private int _epochAvailable;

    private long _capacityRefusals;
    private long _storageErrorDrops;
    private long _staleObservationDrops;
    private long _conflictDrops;
    private long _epochUnavailableDrops;

    public SqliteProxyImageStore(HostDatabase database, IOptions<HostProxyOptions> options)
    {
        _database = database;
        _retentionDays = options.Value.ImageObservationRetentionDays;
        _maximumOutcomesPerTenant = options.Value.ImageObservationMaxOutcomesPerTenant;
        _maximumUsedBytesPerTenant = options.Value.ImageObservationMaxUsedBytesPerTenant;

        if (_retentionDays is < 1 or > 365)
        {
            throw new InvalidOperationException(
                "StyloMail:Proxy:ImageObservationRetentionDays must be between 1 and 365.");
        }

        if (_maximumOutcomesPerTenant < 1 || _maximumUsedBytesPerTenant < 1024)
        {
            throw new InvalidOperationException(
                "Image observation capacity settings must be positive.");
        }
    }

    public bool EpochAvailable => Volatile.Read(ref _epochAvailable) != 0;

    public long CurrentEpoch => Interlocked.Read(ref _epoch);

    public long CapacityRefusals => Interlocked.Read(ref _capacityRefusals);

    public long StorageErrorDrops => Interlocked.Read(ref _storageErrorDrops);

    public long StaleObservationDrops => Interlocked.Read(ref _staleObservationDrops);

    public long ConflictDrops => Interlocked.Read(ref _conflictDrops);

    public long EpochUnavailableDrops => Interlocked.Read(ref _epochUnavailableDrops);

    internal Action? BeforePartInsert { get; set; }

    internal Action? BeforeCommit { get; set; }

    /// <summary>
    /// Increments the persisted monotonic epoch once. A fresh migration has seeded 0, so the first
    /// start returns 1. Missing epoch after the migration marker is a repair fault: the method
    /// returns false and the sink fails closed.
    /// </summary>
    public Task<bool> InitializeEpochAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            using var marker = connection.CreateCommand();
            marker.Transaction = transaction;
            marker.CommandText =
                "SELECT 1 FROM host_schema_migration WHERE name='proxy-image-v1' LIMIT 1;";
            if (marker.ExecuteScalar() is null)
            {
                transaction.Rollback();
                Volatile.Write(ref _epochAvailable, 0);
                return Task.FromResult(false);
            }

            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT epoch FROM proxy_image_epoch WHERE id=1;";
            var value = read.ExecuteScalar();

            if (value is null)
            {
                // The marker proves the image schema existed. A missing epoch is a repair fault;
                // never reset to 1 over existing or swept history.
                transaction.Rollback();
                Volatile.Write(ref _epochAvailable, 0);
                return Task.FromResult(false);
            }

            var next = Convert.ToInt64(value, CultureInfo.InvariantCulture) + 1;
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE proxy_image_epoch SET epoch=$epoch, updated_at=$at WHERE id=1;";
            update.Parameters.AddWithValue("$epoch", next);
            update.Parameters.AddWithValue("$at", Timestamp(DateTimeOffset.UtcNow));
            update.ExecuteNonQuery();
            transaction.Commit();

            Interlocked.Exchange(ref _epoch, next);
            Volatile.Write(ref _epochAvailable, 1);
            return Task.FromResult(true);
        }
        catch
        {
            Volatile.Write(ref _epochAvailable, 0);
            return Task.FromResult(false);
        }
    }

    public ValueTask RecordAsync(ProxyImageObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();

        if (!EpochAvailable)
        {
            Interlocked.Increment(ref _epochUnavailableDrops);
            throw new InvalidOperationException("The image observation epoch is unavailable.");
        }

        Validate(observation);
        observation = observation with { RetentionDays = _retentionDays };
        var usageBytes = UsageBytes(observation);
        var expiresAt = observation.ObservedAt.AddDays(_retentionDays);

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var existing = ReadExisting(connection, transaction, observation);
            if (existing is { } current)
            {
                var incoming = (observation.ObservationEpoch, observation.ObservationSequence);
                var stored = (current.Epoch, current.Sequence);

                if (incoming.CompareTo(stored) < 0)
                {
                    Interlocked.Increment(ref _staleObservationDrops);
                    transaction.Rollback();
                    return ValueTask.CompletedTask;
                }

                if (incoming == stored)
                {
                    if (string.Equals(observation.ResultDigest, current.ResultDigest, StringComparison.Ordinal))
                    {
                        transaction.Rollback();
                        return ValueTask.CompletedTask;
                    }

                    Interlocked.Increment(ref _conflictDrops);
                    transaction.Rollback();
                    return ValueTask.CompletedTask;
                }

                if (string.Equals(observation.ResultDigest, current.ResultDigest, StringComparison.Ordinal))
                {
                    AdvanceWatermark(connection, transaction, observation);
                    BeforeCommit?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction.Commit();
                    return ValueTask.CompletedTask;
                }

                var delta = usageBytes - current.UsageBytes;
                if (!TryChargeUsage(connection, transaction, observation.TenantId, 0, delta))
                {
                    Interlocked.Increment(ref _capacityRefusals);
                    transaction.Rollback();
                    return ValueTask.CompletedTask;
                }

                UpdateOutcome(connection, transaction, observation, usageBytes, expiresAt);
                DeleteParts(connection, transaction, observation);
                InsertParts(connection, transaction, observation, cancellationToken);
                BeforeCommit?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                return ValueTask.CompletedTask;
            }

            if (!TryChargeUsage(connection, transaction, observation.TenantId, 1, usageBytes))
            {
                Interlocked.Increment(ref _capacityRefusals);
                transaction.Rollback();
                return ValueTask.CompletedTask;
            }

            InsertOutcome(connection, transaction, observation, usageBytes, expiresAt);
            InsertParts(connection, transaction, observation, cancellationToken);
            BeforeCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return ValueTask.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            TryWriteStorageError(observation, usageBytes, expiresAt);
            throw;
        }
    }

    public ValueTask<ProxyImageMessagePage> ListAsync(
        string tenantId,
        string threadToken,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        string? after,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadToken);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var cursor = ParseParentCursor(after);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT account_id,message_token,direction,thread_token,MAX(observed_at) AS last_at "
            + "FROM ("
            + "SELECT account_id,message_token,'Retrieval' AS direction,thread_token,occurred_at AS observed_at "
            + "FROM proxy_threading_event WHERE tenant_id=$tenant AND thread_token=$thread "
            + "AND occurred_at >= $from AND occurred_at < $to "
            + "UNION ALL "
            + "SELECT account_id,message_token,direction,thread_token,observed_at "
            + "FROM proxy_warning_outcome WHERE tenant_id=$tenant AND thread_token=$thread "
            + "AND direction=$direction AND observed_at >= $from AND observed_at < $to"
            + ") u "
            + "GROUP BY account_id,message_token,direction,thread_token "
            + "HAVING ($afterAt IS NULL OR last_at < $afterAt "
            + "OR (last_at = $afterAt AND (account_id > $afterAccount "
            + "OR (account_id = $afterAccount AND message_token > $afterMessage)))) "
            + "ORDER BY last_at DESC, account_id ASC, message_token ASC LIMIT $limit;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$thread", threadToken);
        command.Parameters.AddWithValue("$direction", MessageTrafficKind.Retrieval.ToString());
        command.Parameters.AddWithValue("$from", Timestamp(from));
        command.Parameters.AddWithValue("$to", Timestamp(to));
        command.Parameters.AddWithValue("$afterAt", (object?)cursor?.At ?? DBNull.Value);
        command.Parameters.AddWithValue("$afterAccount", (object?)cursor?.Account ?? DBNull.Value);
        command.Parameters.AddWithValue("$afterMessage", (object?)cursor?.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);

        var parents = new List<ProxyImageMessage>(limit + 1);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                parents.Add(new ProxyImageMessage
                {
                    TenantId = tenantId,
                    AccountId = reader.GetString(0),
                    MessageToken = reader.GetString(1),
                    TrafficKind = MessageTrafficKind.Retrieval,
                    ThreadToken = reader.GetString(3),
                    ObservedAt = ParseTimestamp(reader.GetString(4)),
                    Observations = [],
                });
            }
        }

        var hasMore = parents.Count > limit;
        if (hasMore)
        {
            parents.RemoveAt(parents.Count - 1);
        }

        for (var index = 0; index < parents.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = parents[index];
            var observations = ReadChildren(
                connection, parent, now, ChildLimit + 1, after: null, cancellationToken);
            var overflow = observations.Count > ChildLimit;
            if (overflow)
            {
                observations.RemoveAt(observations.Count - 1);
            }

            var total = overflow
                ? CountChildren(connection, parent, now)
                : observations.Count;
            parents[index] = parent with
            {
                Observations = observations,
                ChildrenOverflowCount = Math.Max(0, total - observations.Count),
                ChildrenNext = overflow && observations.Count > 0
                    ? EncodeChildCursor(observations[^1], from, to)
                    : null,
            };
        }

        return ValueTask.FromResult(new ProxyImageMessagePage
        {
            Messages = parents,
            HasMore = hasMore,
            NextCursor = hasMore && parents.Count > 0
                ? EncodeParentCursor(parents[^1], from, to)
                : null,
        });
    }

    public ValueTask<ProxyImageChildPage> ListChildrenAsync(
        string tenantId,
        string threadToken,
        string accountId,
        string messageToken,
        MessageTrafficKind trafficKind,
        int limit,
        string? after,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var childCursor = ParseChildCursor(after);
        var parent = new ProxyImageMessage
        {
            TenantId = tenantId,
            AccountId = accountId,
            MessageToken = messageToken,
            ThreadToken = threadToken,
            TrafficKind = trafficKind,
            ObservedAt = now,
            Observations = [],
        };

        using var connection = _database.Open();
        var children = ReadChildren(
            connection, parent, now, limit + 1, childCursor, cancellationToken);
        var hasMore = children.Count > limit;
        if (hasMore)
        {
            children.RemoveAt(children.Count - 1);
        }

        return ValueTask.FromResult(new ProxyImageChildPage
        {
            Observations = children,
            HasMore = hasMore,
            NextCursor = hasMore && children.Count > 0
                ? EncodeChildCursor(children[^1], windowStart, windowEnd)
                : null,
        });
    }

    public ValueTask<int> DeleteExpiredAsync(
        DateTimeOffset now,
        int maximumMessages,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessages);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var lastTenant = ReadSweepCursor(connection, transaction);
        var tenantLimit = Math.Max(1, maximumMessages / SweepPerTenant) + 1;
        var tenants = SelectTenants(connection, transaction, now, lastTenant, tenantLimit);
        if (tenants.Count == 0 && lastTenant is not null)
        {
            // Wrap at the end of the alphabet so a tenant before the cursor is never starved.
            tenants = SelectTenants(connection, transaction, now, after: null, tenantLimit);
        }

        var deleted = 0;
        foreach (var tenant in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var budget = Math.Min(SweepPerTenant, maximumMessages - deleted);
            if (budget <= 0)
            {
                break;
            }

            var keys = new List<(string Account, string Message, string Direction, string ObservationKey, long Usage)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    "SELECT account_id,message_token,direction,observation_key,usage_bytes "
                    + "FROM proxy_image_outcome WHERE tenant_id=$tenant AND expires_at < $now "
                    + "ORDER BY expires_at ASC, observation_key ASC LIMIT $limit;";
                select.Parameters.AddWithValue("$tenant", tenant);
                select.Parameters.AddWithValue("$now", Timestamp(now));
                select.Parameters.AddWithValue("$limit", budget);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    keys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetInt64(4)));
                }
            }

            foreach (var key in keys)
            {
                DeleteObservation(connection, transaction, tenant, key.Account, key.Message,
                    key.Direction, key.ObservationKey);
                ChargeUsage(connection, transaction, tenant, -1, -key.Usage);
                deleted++;
            }

            WriteSweepCursor(connection, transaction, tenant);
        }

        transaction.Commit();
        return ValueTask.FromResult(deleted);
    }

    // ---------------------------------------------------------------------------------------------
    // Write helpers
    // ---------------------------------------------------------------------------------------------

    private readonly record struct Existing(long Epoch, long Sequence, string ResultDigest, long UsageBytes);

    private static Existing? ReadExisting(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProxyImageObservation observation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT accepted_epoch,accepted_sequence,result_digest,usage_bytes "
            + "FROM proxy_image_outcome WHERE tenant_id=$tenant AND account_id=$account "
            + "AND direction=$direction AND observation_key=$key;";
        command.Parameters.AddWithValue("$tenant", observation.TenantId);
        command.Parameters.AddWithValue("$account", observation.AccountId);
        command.Parameters.AddWithValue("$direction", observation.TrafficKind.ToString());
        command.Parameters.AddWithValue("$key", observation.ObservationKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new Existing(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3))
            : null;
    }

    private static void AdvanceWatermark(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProxyImageObservation observation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE proxy_image_outcome SET accepted_epoch=$epoch, accepted_sequence=$sequence "
            + "WHERE tenant_id=$tenant AND account_id=$account AND direction=$direction "
            + "AND observation_key=$key;";
        command.Parameters.AddWithValue("$epoch", observation.ObservationEpoch);
        command.Parameters.AddWithValue("$sequence", observation.ObservationSequence);
        command.Parameters.AddWithValue("$tenant", observation.TenantId);
        command.Parameters.AddWithValue("$account", observation.AccountId);
        command.Parameters.AddWithValue("$direction", observation.TrafficKind.ToString());
        command.Parameters.AddWithValue("$key", observation.ObservationKey);
        command.ExecuteNonQuery();
    }

    private static void InsertOutcome(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProxyImageObservation observation,
        long usageBytes,
        DateTimeOffset expiresAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO proxy_image_outcome "
            + "(tenant_id,account_id,message_token,direction,thread_token,observation_key,"
            + "content_fingerprint,accepted_epoch,accepted_sequence,result_digest,observed_at,"
            + "availability,availability_reason,source_version,image_parts,"
            + "images_skipped_as_chrome,images_admitted,images_produced,images_not_produced,"
            + "images_over_budget,first_over_budget_ordinal,last_over_budget_ordinal,"
            + "evidence_row_count,usage_bytes,expires_at,retention_days) "
            + "VALUES ($tenant,$account,$message,$direction,$thread,$key,$fingerprint,$epoch,"
            + "$sequence,$digest,$at,$availability,$reason,$version,$parts,$skipped,$admitted,"
            + "$produced,$notProduced,$overBudget,$firstOver,$lastOver,$evidenceRows,$usage,"
            + "$expires,$retention);";
        BindOutcome(command, observation, usageBytes, expiresAt);
        command.ExecuteNonQuery();
    }

    private static void UpdateOutcome(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProxyImageObservation observation,
        long usageBytes,
        DateTimeOffset expiresAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE proxy_image_outcome SET message_token=$message,thread_token=$thread,"
            + "content_fingerprint=$fingerprint,accepted_epoch=$epoch,accepted_sequence=$sequence,"
            + "result_digest=$digest,observed_at=$at,availability=$availability,"
            + "availability_reason=$reason,source_version=$version,image_parts=$parts,"
            + "images_skipped_as_chrome=$skipped,images_admitted=$admitted,images_produced=$produced,"
            + "images_not_produced=$notProduced,images_over_budget=$overBudget,"
            + "first_over_budget_ordinal=$firstOver,last_over_budget_ordinal=$lastOver,"
            + "evidence_row_count=$evidenceRows,usage_bytes=$usage,expires_at=$expires,"
            + "retention_days=$retention "
            + "WHERE tenant_id=$tenant AND account_id=$account AND direction=$direction "
            + "AND observation_key=$key;";
        BindOutcome(command, observation, usageBytes, expiresAt);
        command.ExecuteNonQuery();
    }

    private static void BindOutcome(
        SqliteCommand command,
        ProxyImageObservation observation,
        long usageBytes,
        DateTimeOffset expiresAt)
    {
        command.Parameters.AddWithValue("$tenant", observation.TenantId);
        command.Parameters.AddWithValue("$account", observation.AccountId);
        command.Parameters.AddWithValue("$message", observation.MessageToken);
        command.Parameters.AddWithValue("$direction", observation.TrafficKind.ToString());
        command.Parameters.AddWithValue("$thread", observation.ThreadToken);
        command.Parameters.AddWithValue("$key", observation.ObservationKey);
        command.Parameters.AddWithValue("$fingerprint", observation.ContentFingerprint);
        command.Parameters.AddWithValue("$epoch", observation.ObservationEpoch);
        command.Parameters.AddWithValue("$sequence", observation.ObservationSequence);
        command.Parameters.AddWithValue("$digest", observation.ResultDigest);
        command.Parameters.AddWithValue("$at", Timestamp(observation.ObservedAt));
        command.Parameters.AddWithValue("$availability", observation.Availability.ToString());
        command.Parameters.AddWithValue("$reason", observation.AvailabilityReason.ToString());
        command.Parameters.AddWithValue("$version", observation.SourceVersion);
        command.Parameters.AddWithValue("$parts", observation.ImageParts);
        command.Parameters.AddWithValue("$skipped", observation.ImagesSkippedAsChrome);
        command.Parameters.AddWithValue("$admitted", observation.ImagesAdmitted);
        command.Parameters.AddWithValue("$produced", observation.ImagesProduced);
        command.Parameters.AddWithValue("$notProduced", observation.ImagesNotProduced);
        command.Parameters.AddWithValue("$overBudget", observation.ImagesOverBudget);
        command.Parameters.AddWithValue("$firstOver", (object?)observation.FirstOverBudgetOrdinal ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastOver", (object?)observation.LastOverBudgetOrdinal ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidenceRows", observation.EvidenceRowCount);
        command.Parameters.AddWithValue("$usage", usageBytes);
        command.Parameters.AddWithValue("$expires", Timestamp(expiresAt));
        command.Parameters.AddWithValue("$retention", observation.RetentionDays);
    }

    private static void DeleteParts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProxyImageObservation observation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM proxy_image_part WHERE tenant_id=$tenant AND account_id=$account "
            + "AND direction=$direction AND observation_key=$key;";
        command.Parameters.AddWithValue("$tenant", observation.TenantId);
        command.Parameters.AddWithValue("$account", observation.AccountId);
        command.Parameters.AddWithValue("$direction", observation.TrafficKind.ToString());
        command.Parameters.AddWithValue("$key", observation.ObservationKey);
        command.ExecuteNonQuery();
    }

    private void InsertParts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProxyImageObservation observation,
        CancellationToken cancellationToken)
    {
        foreach (var part in observation.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforePartInsert?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO proxy_image_part "
                + "(tenant_id,account_id,message_token,direction,thread_token,observation_key,ordinal,"
                + "disposition,availability,availability_reason,format,declared_format,width,height,"
                + "pixels,image_end_offset,trailing_bytes,observed_trailing_bytes,trailing_scan_complete,"
                + "transparency_declared,source_version,findings,observed_at) "
                + "VALUES ($tenant,$account,$message,$direction,$thread,$key,$ordinal,$disposition,"
                + "$availability,$reason,$format,$declared,$width,$height,$pixels,$endOffset,$trailing,"
                + "$observedTrailing,$scanComplete,$transparency,$version,$findings,$at);";
            command.Parameters.AddWithValue("$tenant", observation.TenantId);
            command.Parameters.AddWithValue("$account", observation.AccountId);
            command.Parameters.AddWithValue("$message", observation.MessageToken);
            command.Parameters.AddWithValue("$direction", observation.TrafficKind.ToString());
            command.Parameters.AddWithValue("$thread", observation.ThreadToken);
            command.Parameters.AddWithValue("$key", observation.ObservationKey);
            command.Parameters.AddWithValue("$ordinal", part.Ordinal);
            command.Parameters.AddWithValue("$disposition", part.Disposition.ToString());
            command.Parameters.AddWithValue("$availability", part.Availability.ToString());
            command.Parameters.AddWithValue("$reason", part.AvailabilityReason.ToString());
            command.Parameters.AddWithValue("$format", part.Format);
            command.Parameters.AddWithValue("$declared", (object?)part.DeclaredFormat ?? DBNull.Value);
            command.Parameters.AddWithValue("$width", (object?)part.Width ?? DBNull.Value);
            command.Parameters.AddWithValue("$height", (object?)part.Height ?? DBNull.Value);
            command.Parameters.AddWithValue("$pixels", (object?)part.PixelCount ?? DBNull.Value);
            command.Parameters.AddWithValue("$endOffset", (object?)part.ImageEndOffset ?? DBNull.Value);
            command.Parameters.AddWithValue("$trailing", (object?)part.TrailingByteCount ?? DBNull.Value);
            command.Parameters.AddWithValue("$observedTrailing", (object?)part.TrailingBytesObserved ?? DBNull.Value);
            command.Parameters.AddWithValue("$scanComplete", part.TrailingScanComplete is { } scan ? (scan ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("$transparency", part.TransparencyDeclared is { } transparency ? (transparency ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("$version", part.SourceVersion);
            command.Parameters.AddWithValue("$findings", JsonSerializer.Serialize(part.Findings, HostJson.Options));
            command.Parameters.AddWithValue("$at", Timestamp(observation.ObservedAt));
            command.ExecuteNonQuery();
        }
    }

    private bool TryChargeUsage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        int outcomeDelta,
        long byteDelta)
    {
        var current = ReadUsage(connection, transaction, tenantId);
        var outcomes = current.Outcomes + outcomeDelta;
        var bytes = current.Bytes + byteDelta;

        if (outcomes < 0 || bytes < 0)
        {
            return false;
        }

        if (outcomeDelta > 0 && outcomes > _maximumOutcomesPerTenant)
        {
            return false;
        }

        if (byteDelta > 0 && bytes > _maximumUsedBytesPerTenant)
        {
            return false;
        }

        WriteUsage(connection, transaction, tenantId, outcomes, bytes);
        return true;
    }

    private static void ChargeUsage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        int outcomeDelta,
        long byteDelta)
    {
        var current = ReadUsage(connection, transaction, tenantId);
        WriteUsage(connection, transaction, tenantId,
            Math.Max(0, current.Outcomes + outcomeDelta),
            Math.Max(0, current.Bytes + byteDelta));
    }

    private readonly record struct Usage(long Outcomes, long Bytes);

    private static Usage ReadUsage(SqliteConnection connection, SqliteTransaction transaction, string tenantId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT outcomes,used_bytes FROM proxy_image_tenant_usage WHERE tenant_id=$tenant;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new Usage(reader.GetInt64(0), reader.GetInt64(1)) : default;
    }

    private static void WriteUsage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        long outcomes,
        long bytes)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO proxy_image_tenant_usage(tenant_id,outcomes,used_bytes,updated_at) "
            + "VALUES($tenant,$outcomes,$bytes,$at) "
            + "ON CONFLICT(tenant_id) DO UPDATE SET outcomes=excluded.outcomes,"
            + "used_bytes=excluded.used_bytes,updated_at=excluded.updated_at;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$outcomes", outcomes);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$at", Timestamp(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private void TryWriteStorageError(
        ProxyImageObservation observation,
        long usageBytes,
        DateTimeOffset expiresAt)
    {
        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            if (ReadExisting(connection, transaction, observation) is not null)
            {
                transaction.Rollback();
                return;
            }

            var failure = observation with
            {
                Availability = ProxyImageAvailability.Unavailable,
                AvailabilityReason = ProxyImageReason.StorageError,
                ImageParts = 0,
                ImagesSkippedAsChrome = 0,
                ImagesAdmitted = 0,
                ImagesProduced = 0,
                ImagesNotProduced = 0,
                ImagesOverBudget = 0,
                FirstOverBudgetOrdinal = null,
                LastOverBudgetOrdinal = null,
                EvidenceRowCount = 0,
                ResultDigest = "storage-error-v1",
                Parts = [],
            };

            var failureUsageBytes = UsageBytes(failure);
            if (!TryChargeUsage(connection, transaction, observation.TenantId, 1, failureUsageBytes))
            {
                Interlocked.Increment(ref _capacityRefusals);
                transaction.Rollback();
                return;
            }

            // INSERT OR IGNORE is the final guard: a prior valid row is never overwritten.
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT OR IGNORE INTO proxy_image_outcome "
                + "(tenant_id,account_id,message_token,direction,thread_token,observation_key,"
                + "content_fingerprint,accepted_epoch,accepted_sequence,result_digest,observed_at,"
                + "availability,availability_reason,source_version,image_parts,"
                + "images_skipped_as_chrome,images_admitted,images_produced,images_not_produced,"
                + "images_over_budget,first_over_budget_ordinal,last_over_budget_ordinal,"
                + "evidence_row_count,usage_bytes,expires_at,retention_days) "
                + "VALUES ($tenant,$account,$message,$direction,$thread,$key,$fingerprint,$epoch,"
                + "$sequence,$digest,$at,$availability,$reason,$version,$parts,$skipped,$admitted,"
                + "$produced,$notProduced,$overBudget,$firstOver,$lastOver,$evidenceRows,$usage,"
                + "$expires,$retention);";
            BindOutcome(insert, failure, failureUsageBytes, expiresAt);
            var inserted = insert.ExecuteNonQuery();
            if (inserted == 0)
            {
                ChargeUsage(connection, transaction, observation.TenantId, -1, -failureUsageBytes);
            }

            transaction.Commit();
        }
        catch
        {
            Interlocked.Increment(ref _storageErrorDrops);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Read helpers
    // ---------------------------------------------------------------------------------------------

    private static List<ProxyImageObservation> ReadChildren(
        SqliteConnection connection,
        ProxyImageMessage parent,
        DateTimeOffset now,
        int limit,
        ChildCursor? after,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT observation_key,content_fingerprint,accepted_epoch,accepted_sequence,result_digest,"
            + "observed_at,availability,availability_reason,source_version,image_parts,"
            + "images_skipped_as_chrome,images_admitted,images_produced,images_not_produced,"
            + "images_over_budget,first_over_budget_ordinal,last_over_budget_ordinal,"
            + "evidence_row_count,retention_days,usage_bytes FROM proxy_image_outcome "
            + "WHERE tenant_id=$tenant AND account_id=$account AND message_token=$message "
            + "AND direction=$direction AND thread_token=$thread AND expires_at > $now "
            + "AND ($afterEpoch IS NULL OR accepted_epoch < $afterEpoch "
            + "OR (accepted_epoch = $afterEpoch AND accepted_sequence < $afterSequence) "
            + "OR (accepted_epoch = $afterEpoch AND accepted_sequence = $afterSequence "
            + "AND observation_key > $afterKey)) "
            + "ORDER BY accepted_epoch DESC, accepted_sequence DESC, observation_key ASC LIMIT $limit;";
        command.Parameters.AddWithValue("$tenant", parent.TenantId);
        command.Parameters.AddWithValue("$account", parent.AccountId);
        command.Parameters.AddWithValue("$message", parent.MessageToken);
        command.Parameters.AddWithValue("$direction", parent.TrafficKind.ToString());
        command.Parameters.AddWithValue("$thread", parent.ThreadToken);
        command.Parameters.AddWithValue("$now", Timestamp(now));
        command.Parameters.AddWithValue("$afterEpoch", (object?)after?.Epoch ?? DBNull.Value);
        command.Parameters.AddWithValue("$afterSequence", (object?)after?.Sequence ?? DBNull.Value);
        command.Parameters.AddWithValue("$afterKey", (object?)after?.Key ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);

        var observations = new List<ProxyImageObservation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            observations.Add(new ProxyImageObservation
            {
                TenantId = parent.TenantId,
                AccountId = parent.AccountId,
                MessageToken = parent.MessageToken,
                ThreadToken = parent.ThreadToken,
                TrafficKind = parent.TrafficKind,
                ObservationKey = reader.GetString(0),
                ContentFingerprint = reader.GetString(1),
                ObservationEpoch = reader.GetInt64(2),
                ObservationSequence = reader.GetInt64(3),
                ResultDigest = reader.GetString(4),
                ObservedAt = ParseTimestamp(reader.GetString(5)),
                Availability = ParseAvailability(reader.GetString(6)),
                AvailabilityReason = ParseReason(reader.GetString(7)),
                SourceVersion = reader.GetString(8),
                ImageParts = reader.GetInt32(9),
                ImagesSkippedAsChrome = reader.GetInt32(10),
                ImagesAdmitted = reader.GetInt32(11),
                ImagesProduced = reader.GetInt32(12),
                ImagesNotProduced = reader.GetInt32(13),
                ImagesOverBudget = reader.GetInt32(14),
                FirstOverBudgetOrdinal = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                LastOverBudgetOrdinal = reader.IsDBNull(16) ? null : reader.GetInt32(16),
                EvidenceRowCount = reader.GetInt32(17),
                RetentionDays = reader.GetInt32(18),
                Parts = ReadParts(connection, parent, reader.GetString(0), cancellationToken),
            });
        }

        return observations;
    }

    private static List<ProxyImagePartObservation> ReadParts(
        SqliteConnection connection,
        ProxyImageMessage parent,
        string observationKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ordinal,disposition,availability,availability_reason,format,declared_format,"
            + "width,height,pixels,image_end_offset,trailing_bytes,observed_trailing_bytes,"
            + "trailing_scan_complete,transparency_declared,source_version,findings "
            + "FROM proxy_image_part WHERE tenant_id=$tenant AND account_id=$account "
            + "AND message_token=$message AND direction=$direction AND thread_token=$thread "
            + "AND observation_key=$key ORDER BY ordinal;";
        command.Parameters.AddWithValue("$tenant", parent.TenantId);
        command.Parameters.AddWithValue("$account", parent.AccountId);
        command.Parameters.AddWithValue("$message", parent.MessageToken);
        command.Parameters.AddWithValue("$direction", parent.TrafficKind.ToString());
        command.Parameters.AddWithValue("$thread", parent.ThreadToken);
        command.Parameters.AddWithValue("$key", observationKey);

        var parts = new List<ProxyImagePartObservation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var findings = ParseFindings(reader.GetString(15));
            parts.Add(new ProxyImagePartObservation
            {
                Ordinal = reader.GetInt32(0),
                Disposition = findings is null ? ProxyImageDisposition.NotProduced : ParseDisposition(reader.GetString(1)),
                Availability = findings is null ? ProxyImageAvailability.Unavailable : ParseAvailability(reader.GetString(2)),
                AvailabilityReason = findings is null ? ProxyImageReason.InvalidStructure : ParseReason(reader.GetString(3)),
                Format = reader.GetString(4),
                DeclaredFormat = reader.IsDBNull(5) ? null : reader.GetString(5),
                Width = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                Height = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                PixelCount = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                ImageEndOffset = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                TrailingByteCount = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                TrailingBytesObserved = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                TrailingScanComplete = reader.IsDBNull(12) ? null : reader.GetInt32(12) != 0,
                TransparencyDeclared = reader.IsDBNull(13) ? null : reader.GetInt32(13) != 0,
                SourceVersion = reader.GetString(14),
                Findings = findings ?? [],
            });
        }

        return parts;
    }

    private static int CountChildren(SqliteConnection connection, ProxyImageMessage parent, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM proxy_image_outcome WHERE tenant_id=$tenant AND account_id=$account "
            + "AND message_token=$message AND direction=$direction AND thread_token=$thread "
            + "AND expires_at > $now;";
        command.Parameters.AddWithValue("$tenant", parent.TenantId);
        command.Parameters.AddWithValue("$account", parent.AccountId);
        command.Parameters.AddWithValue("$message", parent.MessageToken);
        command.Parameters.AddWithValue("$direction", parent.TrafficKind.ToString());
        command.Parameters.AddWithValue("$thread", parent.ThreadToken);
        command.Parameters.AddWithValue("$now", Timestamp(now));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<string> SelectTenants(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        string? after,
        int limit)
    {
        var tenants = new List<string>();
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            "SELECT DISTINCT tenant_id FROM proxy_image_outcome "
            + "WHERE expires_at < $now AND tenant_id > $after ORDER BY tenant_id LIMIT $limit;";
        select.Parameters.AddWithValue("$now", Timestamp(now));
        select.Parameters.AddWithValue("$after", after ?? string.Empty);
        select.Parameters.AddWithValue("$limit", limit);
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            tenants.Add(reader.GetString(0));
        }

        return tenants;
    }

    private static void DeleteObservation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string accountId,
        string messageToken,
        string direction,
        string observationKey)
    {
        using var parts = connection.CreateCommand();
        parts.Transaction = transaction;
        parts.CommandText =
            "DELETE FROM proxy_image_part WHERE tenant_id=$tenant AND account_id=$account "
            + "AND message_token=$message AND direction=$direction AND observation_key=$key;";
        parts.Parameters.AddWithValue("$tenant", tenantId);
        parts.Parameters.AddWithValue("$account", accountId);
        parts.Parameters.AddWithValue("$message", messageToken);
        parts.Parameters.AddWithValue("$direction", direction);
        parts.Parameters.AddWithValue("$key", observationKey);
        parts.ExecuteNonQuery();

        using var outcome = connection.CreateCommand();
        outcome.Transaction = transaction;
        outcome.CommandText =
            "DELETE FROM proxy_image_outcome WHERE tenant_id=$tenant AND account_id=$account "
            + "AND message_token=$message AND direction=$direction AND observation_key=$key;";
        outcome.Parameters.AddWithValue("$tenant", tenantId);
        outcome.Parameters.AddWithValue("$account", accountId);
        outcome.Parameters.AddWithValue("$message", messageToken);
        outcome.Parameters.AddWithValue("$direction", direction);
        outcome.Parameters.AddWithValue("$key", observationKey);
        outcome.ExecuteNonQuery();
    }

    private static string? ReadSweepCursor(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_tenant_id FROM proxy_image_sweep_state WHERE id=1;";
        return command.ExecuteScalar() as string;
    }

    private static void WriteSweepCursor(SqliteConnection connection, SqliteTransaction transaction, string tenantId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO proxy_image_sweep_state(id,last_tenant_id,updated_at) VALUES(1,$tenant,$at) "
            + "ON CONFLICT(id) DO UPDATE SET last_tenant_id=excluded.last_tenant_id,"
            + "updated_at=excluded.updated_at;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$at", Timestamp(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------------------------------------
    // Validation, cursors, usage
    // ---------------------------------------------------------------------------------------------

    private static void Validate(ProxyImageObservation observation)
    {
        RequireToken(observation.TenantId, nameof(observation.TenantId));
        RequireToken(observation.AccountId, nameof(observation.AccountId));
        RequireToken(observation.MessageToken, nameof(observation.MessageToken));
        RequireToken(observation.ThreadToken, nameof(observation.ThreadToken));
        RequireToken(observation.ObservationKey, nameof(observation.ObservationKey));
        RequireToken(observation.ContentFingerprint, nameof(observation.ContentFingerprint));
        RequireToken(observation.ResultDigest, nameof(observation.ResultDigest));
        if (!SafeVersion.IsMatch(observation.SourceVersion))
        {
            throw new ArgumentException("Image observation source version is not a safe token.", nameof(observation));
        }

        if (observation.ObservationEpoch < 0 || observation.ObservationSequence < 0)
        {
            throw new ArgumentException("Observation epoch and sequence must be non-negative.", nameof(observation));
        }

        if (observation.ImageParts < 0 || observation.ImagesSkippedAsChrome < 0 ||
            observation.ImagesAdmitted < 0 || observation.ImagesProduced < 0 ||
            observation.ImagesNotProduced < 0 || observation.ImagesOverBudget < 0)
        {
            throw new ArgumentException("Image counts cannot be negative.", nameof(observation));
        }

        if (observation.ImageParts !=
            observation.ImagesSkippedAsChrome + observation.ImagesAdmitted + observation.ImagesOverBudget)
        {
            throw new ArgumentException("Image part counts do not add up.", nameof(observation));
        }

        if (observation.ImagesAdmitted != observation.Parts.Count || observation.ImagesAdmitted > MaximumParts)
        {
            throw new ArgumentException("Admitted images must match parts and be at most two.", nameof(observation));
        }

        if (observation.ImagesAdmitted != observation.ImagesProduced + observation.ImagesNotProduced)
        {
            throw new ArgumentException("Produced plus not-produced must equal admitted.", nameof(observation));
        }

        if (observation.EvidenceRowCount is < 0 or > MaximumEvidenceRows)
        {
            throw new ArgumentException("Image evidence row count must be between 0 and 13.", nameof(observation));
        }

        if (observation.ImagesOverBudget == 0)
        {
            if (observation.FirstOverBudgetOrdinal is not null || observation.LastOverBudgetOrdinal is not null)
            {
                throw new ArgumentException("Over-budget ordinals must be absent when nothing is over budget.", nameof(observation));
            }
        }
        else if (observation.FirstOverBudgetOrdinal is null || observation.LastOverBudgetOrdinal is null ||
                 observation.FirstOverBudgetOrdinal > observation.LastOverBudgetOrdinal)
        {
            throw new ArgumentException("Over-budget ordinals are required and ordered.", nameof(observation));
        }

        var ordinals = new HashSet<int>();
        var produced = 0;
        var notProduced = 0;
        foreach (var part in observation.Parts)
        {
            if (part.Ordinal <= 0 || !ordinals.Add(part.Ordinal))
            {
                throw new ArgumentException("Image ordinals must be positive and unique.", nameof(observation));
            }

            if (!SafeFormat.IsMatch(part.Format) ||
                (part.DeclaredFormat is not null && !SafeFormat.IsMatch(part.DeclaredFormat)))
            {
                throw new ArgumentException("Image format is not a safe token.", nameof(observation));
            }

            if (!SafeVersion.IsMatch(part.SourceVersion))
            {
                throw new ArgumentException("Image part source version is not a safe token.", nameof(observation));
            }

            if (part.Findings.Count > MaximumFindings)
            {
                throw new ArgumentException("An image cannot carry more than 16 findings.", nameof(observation));
            }

            foreach (var finding in part.Findings)
            {
                if (!Enum.IsDefined(finding.Kind) || finding.Offset is < 0 ||
                    (finding.Detail is not null && !SafeDetail.IsMatch(finding.Detail)))
                {
                    throw new ArgumentException("Image finding is outside the closed vocabulary.", nameof(observation));
                }
            }

            if (part.Disposition == ProxyImageDisposition.NotProduced)
            {
                notProduced++;
            }
            else
            {
                produced++;
            }
        }

        if (produced != observation.ImagesProduced || notProduced != observation.ImagesNotProduced)
        {
            throw new ArgumentException("Produced/not-produced counts do not match parts.", nameof(observation));
        }
    }

    private static void RequireToken(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0') || !SafeOpaque.IsMatch(value))
        {
            throw new ArgumentException("Image observation token is not a safe opaque value.", name);
        }
    }

    internal static long UsageBytes(ProxyImageObservation observation) =>
        JsonSerializer.SerializeToUtf8Bytes(observation, HostJson.Options).LongLength + OutcomeOverheadBytes;

    private readonly record struct ParentCursor(
        string At,
        string Account,
        string Message,
        DateTimeOffset WindowStart,
        DateTimeOffset WindowEnd);

    private readonly record struct ChildCursor(
        long Epoch,
        long Sequence,
        string Key,
        DateTimeOffset WindowStart,
        DateTimeOffset WindowEnd);

    private static ParentCursor? ParseParentCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        var parts = cursor.Split('|');
        if (parts.Length != 5 ||
            !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var windowStartTicks) ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var windowEndTicks) ||
            !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
        {
            throw new ArgumentException("Image parent cursor is malformed.", nameof(cursor));
        }

        return new ParentCursor(
            new DateTimeOffset(ticks, TimeSpan.Zero).ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture),
            parts[3],
            parts[4],
            new DateTimeOffset(windowStartTicks, TimeSpan.Zero),
            new DateTimeOffset(windowEndTicks, TimeSpan.Zero));
    }

    private static string EncodeParentCursor(
        ProxyImageMessage parent,
        DateTimeOffset from,
        DateTimeOffset to) =>
        from.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture) + "|"
        + to.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture) + "|"
        + parent.ObservedAt.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture) + "|"
        + parent.AccountId + "|" + parent.MessageToken;

    private static ChildCursor? ParseChildCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        var parts = cursor.Split('|');
        if (parts.Length != 5 ||
            !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks) ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var endTicks) ||
            !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) ||
            !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ||
            startTicks < 0 || endTicks < 0 ||
            startTicks > DateTimeOffset.MaxValue.UtcTicks ||
            endTicks > DateTimeOffset.MaxValue.UtcTicks)
        {
            throw new ArgumentException("Image child cursor is malformed.", nameof(cursor));
        }

        return new ChildCursor(
            epoch,
            sequence,
            parts[4],
            new DateTimeOffset(startTicks, TimeSpan.Zero),
            new DateTimeOffset(endTicks, TimeSpan.Zero));
    }

    private static string EncodeChildCursor(
        ProxyImageObservation observation,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd) =>
        windowStart.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture) + "|"
        + windowEnd.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture) + "|"
        + observation.ObservationEpoch.ToString(CultureInfo.InvariantCulture) + "|"
        + observation.ObservationSequence.ToString(CultureInfo.InvariantCulture) + "|"
        + observation.ObservationKey;

    private static List<ProxyImageFinding>? ParseFindings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ProxyImageFinding>>(json, HostJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProxyImageDisposition ParseDisposition(string value) =>
        Enum.TryParse(value, ignoreCase: true, out ProxyImageDisposition parsed)
            ? parsed
            : ProxyImageDisposition.NotProduced;

    private static ProxyImageAvailability ParseAvailability(string value) =>
        Enum.TryParse(value, ignoreCase: true, out ProxyImageAvailability parsed)
            ? parsed
            : ProxyImageAvailability.Unavailable;

    private static ProxyImageReason ParseReason(string value) =>
        Enum.TryParse(value, ignoreCase: true, out ProxyImageReason parsed)
            ? parsed
            : ProxyImageReason.InvalidStructure;

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
