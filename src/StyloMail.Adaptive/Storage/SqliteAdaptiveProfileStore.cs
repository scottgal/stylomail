using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloMail.Adaptive.Learning;
using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Scoring;
using StyloMail.Adaptive.Temporal;
using StyloMail.Core;
using StyloMail.Persistence;

namespace StyloMail.Adaptive.Storage;

/// <summary>
/// A write was refused because the profile moved on since this instance was loaded.
/// </summary>
/// <remarks>
/// <b>This is a correctness event, not a transient storage failure.</b> Two writers each loaded
/// the same revision and both tried to save; the loser's update would have silently dropped the
/// winner's. Retrying the same in-memory object will fail again, the remedy is to reload, reapply
/// the change, and save.
///
/// <para>
/// Deliberately not a subtype of <see cref="SqliteException"/>: a handler that treats storage
/// trouble as retryable must not swallow a lost update as though it were a busy lock.
/// </para>
/// </remarks>
public sealed class ProfileVersionConflictException : InvalidOperationException
{
    public ProfileVersionConflictException(ProfileKey key, long expectedRevision, long actualRevision)
        : base(
            $"Profile {key.Scope} in tenant '{key.TenantId}' is at revision {actualRevision}, but this "
            + $"instance was loaded at revision {expectedRevision}. Reload it, reapply the change, and "
            + "save again.")
    {
        Key = key;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public ProfileKey Key { get; }

    public long ExpectedRevision { get; }

    public long ActualRevision { get; }
}

/// <summary>
/// Durable profile state, written to the shared SQLite store.
/// </summary>
/// <remarks>
/// Two properties matter more than any others here, and both are structural rather than
/// enforced by convention. <b>Tenant isolation:</b> every statement is keyed on
/// <c>tenant_id</c>, and no method can reach a row without naming the tenant it is serving.
/// <b>Eviction is not a reset:</b> eviction dehydrates the expensive baseline state and leaves
/// the observed counters, so a profile that comes back has not earned a fresh recipient quota.
///
/// <para>
/// <b>Safe to share across threads</b>, including for the <em>same</em> profile when writes go
/// through <see cref="ApplyObservation"/> or <see cref="Update"/>. Both take SQLite's write lock
/// before reading, so concurrent writers queue and each sees the previous writer's result instead
/// of conflicting, no caller-side serialisation is needed, and adding one would only serialise
/// work the store already serialises.
/// </para>
///
/// <para>
/// <see cref="Save"/> is the exception and the reason this paragraph is specific. It writes a
/// profile the caller already holds, so it cannot re-read first; it compares the stored revision
/// and refuses a stale write with <see cref="ProfileVersionConflictException"/> rather than losing
/// an update silently. Callers holding a profile must therefore be prepared to reload and reapply.
/// </para>
/// </remarks>
public sealed class SqliteAdaptiveProfileStore
{
    private const string OutboundSuffix = "#outbound";
    private const string InboundSuffix = "#inbound";
    private const string DefaultRegimeId = "regime-0";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly SqliteConnectionFactory _factory;

    public SqliteAdaptiveProfileStore(SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <summary>Creates the shared schema and this store's own tables. Safe to call on every start.</summary>
    public void EnsureCreated()
    {
        using var connection = _factory.Open();
        SqliteSchema.EnsureCreated(connection);

        using var command = connection.CreateCommand();
        command.CommandText = AdaptiveTablesDdl;
        command.ExecuteNonQuery();
    }

    public void Save(AdaptiveProfile profile, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(profile);

        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();

        // The compare-and-swap comes first, before anything is written, so a refused write
        // leaves the stored profile exactly as the winner left it, the counters and the
        // baseline move together or not at all.
        var actualRevision = ReadRevision(connection, transaction, profile.Key);
        if (actualRevision != profile.PersistedRevision)
        {
            transaction.Rollback();
            throw new ProfileVersionConflictException(profile.Key, profile.PersistedRevision, actualRevision);
        }

        Write(connection, transaction, profile, at, actualRevision + 1);
        transaction.Commit();
    }

    private static ProfileStateDocument BuildDocument(AdaptiveProfile profile) => new()
{
        Series =
        [
            .. profile.Series.Select(pair => new BucketSeriesDocument
            {
                Window = pair.Key,
                WidthTicks = pair.Value.Width.Ticks,
                Buckets =
                [
                    .. pair.Value.Snapshot().Select(bucket => new BucketDocument
                    {
                        Index = bucket.Index,
                        Samples = bucket.SampleCount,
                        Recipients = bucket.RecipientCount,
                        Rejected = bucket.RejectedCount,
                        Dimensions =
                        [
                            .. bucket.Dimensions.Select(d => new AccumulatorDocument
                            {
                                Id = d.DimensionId,
                                Sum = d.Sum,
                                Count = d.Count,
                            }),
                        ],
                    }),
                ],
            }),
        ],
        FirstObservedAtUnixMs = profile.Observed.FirstObservedAt?.ToUnixTimeMilliseconds(),
        RejectedAttempts = profile.Observed.RejectedAttempts,
        RecipientsTruncated = profile.Recipients.Truncated,
        SeenRecipients = Convert.ToBase64String(profile.Recipients.Seen.ToBytes()),
        Recipients =
        [
            .. profile.Recipients.Entries.Select(entry => new RecipientDocument
            {
                Key = entry.Key,
                FirstSeenUnixMs = entry.FirstSeen.ToUnixTimeMilliseconds(),
                LastSeenUnixMs = entry.LastSeen.ToUnixTimeMilliseconds(),
            }),
        ],
        FrozenAtUnixMs = profile.Baseline.FrozenAt?.ToUnixTimeMilliseconds(),
        FreezeReason = profile.Baseline.FreezeReason,
        LastPromotedAtUnixMs = profile.Baseline.LastPromotedAt?.ToUnixTimeMilliseconds(),
    };


    /// <summary>
    /// The stored revision for a profile, or <c>0</c> when it has never been written.
    /// </summary>
    /// <remarks>
    /// A revision rather than <c>baseline_version</c>. The baseline version only advances on
    /// promotion, so an observation-only save would carry an unchanged token and a concurrent
    /// write would overwrite it unnoticed, the exact lost update this guards against.
    /// </remarks>
    private static long ReadRevision(SqliteConnection connection, SqliteTransaction? transaction, ProfileKey key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT revision FROM adaptive_profile_revision
            WHERE tenant_id = $tenant AND profile_scope = $scope AND profile_key = $key;
            """;
        command.Parameters.AddWithValue("$tenant", key.TenantId);
        command.Parameters.AddWithValue("$scope", key.Scope.ToString());
        command.Parameters.AddWithValue("$key", StorageKey(key));

        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private static void WriteRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProfileKey key,
        long revision)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO adaptive_profile_revision (tenant_id, profile_scope, profile_key, revision)
            VALUES ($tenant, $scope, $key, $revision)
            ON CONFLICT (tenant_id, profile_scope, profile_key) DO UPDATE SET revision = excluded.revision;
            """;
        command.Parameters.AddWithValue("$tenant", key.TenantId);
        command.Parameters.AddWithValue("$scope", key.Scope.ToString());
        command.Parameters.AddWithValue("$key", StorageKey(key));
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records one observation against a profile, in a single serialised transaction.
    /// </summary>
    /// <remarks>
    /// This is the ingest path, and it exists because compare-and-swap alone is not enough for
    /// it. <see cref="Save"/> protects a caller that already holds a profile, but under a burst
    /// many callers hold the <em>same</em> profile, so only one can win a round and the rest must
    /// reload and retry. Sixteen simultaneous messages from one sender is not a hot edge case,     /// it is the shape of the burst this system exists to notice, and the account being written
    /// by many messages at once is the compromised one.
    ///
    /// <para>
    /// Taking the write lock up front (<c>BEGIN IMMEDIATE</c>) and doing the read-modify-write
    /// inside it means there is nothing to conflict with: writers queue and each sees the
    /// previous one's result. No retry loop, no lost observations, and no need for a caller-side
    /// gate. <see cref="Save"/> remains for callers holding a profile and for the cross-process
    /// case, where a second process can still invalidate an in-memory snapshot.
    /// </para>
    ///
    /// <para>
    /// <b>Prefer this over <see cref="Update"/> for an append.</b> The two are equally safe under a
    /// burst, the difference is shape, not safety, because both take the write lock before
    /// reading. What this buys is that "this is an append" is stated once, here, rather than every
    /// caller reimplementing the merge inside an update delegate and getting it subtly different.
    /// </para>
    /// </remarks>
    public void ApplyObservation(ProfileKey key, ProfileObservation observation, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(observation);

        using var connection = _factory.Open();

        // Immediate rather than deferred, so the write lock is taken before the read and each
        // writer sees the previous writer's result.
        //
        // Honest note on the evidence: this is reasoning, not a discriminated test. Switching to a
        // deferred transaction leaves every test in this file green (mutation-checked, five runs),
        // because SQLite refuses to upgrade a stale snapshot and raises BUSY_SNAPSHOT rather than
        // letting the stale write land. That refusal is what protects the deferred version, and it
        // is SQLite's behaviour rather than ours. Taking the lock up front makes the ordering our
        // guarantee instead. If you change this line, change this comment with it: the tests will
        // not tell you.
        using var transaction = connection.BeginTransaction(deferred: false);

        var profile = Load(connection, transaction, key);
        if (profile is null)
        {
            // First message from a principal nobody has seen. Creating it here rather than
            // requiring a separate call means the ingest path has no "profile must already
            // exist" precondition for callers to get wrong.
            profile = new AdaptiveProfile(key);
        }

        profile.Observe(observation);
        Write(connection, transaction, profile, at, profile.PersistedRevision + 1);

        transaction.Commit();
    }

    /// <summary>Writes a profile's full state and bumps its revision. Caller owns the transaction.</summary>
    private static void Write(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AdaptiveProfile profile,
        DateTimeOffset at,
        long revision)
    {
        WriteProfileRow(connection, transaction, profile, BuildDocument(profile), at);
        ReplaceBaselineDimensions(connection, transaction, profile);
        WriteRevision(connection, transaction, profile.Key, revision);

        profile.PersistedRevision = revision;
    }

    /// <summary>
    /// Applies a decision to a profile inside the write transaction and returns the delegate's result.
    /// </summary>
    /// <remarks>
    /// This is the general form of <see cref="ApplyObservation"/>, for changes that need a
    /// <em>decision</em> rather than a pure append, a promotion has to consult label provenance,
    /// freeze state and the regime candidate, and that logic belongs to the caller, not to
    /// persistence. The delegate keeps the decision and this method supplies the transaction.
    ///
    /// <para>
    /// <b>Why this exists rather than compare-and-swap.</b> Under a burst, many callers hold the
    /// same profile, so a CAS-and-retry design lets only one win a round and makes the rest reload
    /// and retry. A promotion racing a burst is not a corner case, it is an operator intervening
    /// in exactly the incident that produces the burst, deciding that the sender's new behaviour is
    /// legitimate. The moment the operation most needs to succeed is the moment it is most likely
    /// to fail. Taking the write lock up front removes the conflict instead of retrying it.
    /// </para>
    ///
    /// <para>
    /// <b>The delegate holds the database's write lock while it runs.</b> SQLite has a single
    /// writer, so a slow callback serialises every other profile write in the process. Keep it to
    /// in-memory work: no I/O, no calls back into this store (a nested call would deadlock), and no
    /// awaiting anything. If it throws, the transaction rolls back and the exception propagates
    /// unchanged, nothing is half-applied.
    /// </para>
    ///
    /// <para>
    /// <b>It always writes, even when the delegate changes nothing</b>, the revision advances
    /// regardless. That is deliberate (deciding whether a profile "changed" means comparing
    /// everything it holds) but it means this is not a read path: calling it to inspect a profile
    /// takes the write lock and invalidates any other holder's compare-and-swap token. Use
    /// <see cref="Load"/> to read.
    /// </para>
    /// </remarks>
    public T Update<T>(ProfileKey key, DateTimeOffset at, Func<AdaptiveProfile, T> update)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(update);

        using var connection = _factory.Open();

        // Immediate rather than deferred, for the same reason as ApplyObservation: the write lock
        // is taken before the read, so each writer sees the previous writer's result.
        using var transaction = connection.BeginTransaction(deferred: false);

        var profile = Load(connection, transaction, key) ?? new AdaptiveProfile(key);
        var result = update(profile);

        Write(connection, transaction, profile, at, profile.PersistedRevision + 1);
        transaction.Commit();

        return result;
    }

    /// <summary>Loads one profile, or <see langword="null"/> when this tenant has none.</summary>
    public AdaptiveProfile? Load(ProfileKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var connection = _factory.Open();
        return Load(connection, transaction: null, key);
    }

    /// <summary>Every profile belonging to one tenant. Never returns another tenant's rows.</summary>
    public IReadOnlyList<AdaptiveProfile> LoadForTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        using var connection = _factory.Open();

        var keys = new List<ProfileKey>();
        using (var command = connection.CreateCommand())
        {
            // The tenant predicate is the isolation boundary, and it is never optional.
            command.CommandText =
                """
                SELECT profile_scope, profile_key, direction
                FROM profiles
                WHERE tenant_id = $tenant
                ORDER BY profile_scope, profile_key;
                """;
            command.Parameters.AddWithValue("$tenant", tenantId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (TryReadKey(
                        tenantId,
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        out var key))
                {
                    keys.Add(key);
                }
            }
        }

        var profiles = new List<AdaptiveProfile>(keys.Count);
        foreach (var key in keys)
        {
            if (Load(connection, transaction: null, key) is { } profile)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <summary>
    /// Evicts a profile by dehydrating it: the expensive baseline and trend state go, the
    /// observed counters stay.
    /// </summary>
    /// <remarks>
    /// Deleting the row would be simpler, and would silently hand the principal a fresh
    /// recipient quota, eviction would become a bypass. The counters are the last thing
    /// bounding the damage from a late detection, so they are the last thing eviction touches.
    /// </remarks>
    public void Evict(ProfileKey key, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Load(key) is not { } existing)
        {
            return;
        }

        existing.Dehydrate();
        Save(existing, at);
    }

    private static void WriteProfileRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AdaptiveProfile profile,
        ProfileStateDocument document,
        DateTimeOffset at)
    {
        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;

        // One row per (tenant, scope, key). Saving twice records the current state rather
        // than accumulating: a profile is a snapshot, not a log.
        upsert.CommandText =
            """
            INSERT INTO profiles (
                tenant_id, profile_scope, profile_key, direction,
                trusted_support, baseline_version, regime_id, baseline_frozen,
                observed_attempts, observed_recipients, last_observed_at,
                fast_average_json, slow_average_json, velocity_json, acceleration_json,
                bucket_state_json, updated_at)
            VALUES (
                $tenant, $scope, $key, $direction,
                $support, $version, $regime, $frozen,
                $attempts, $recipients, $lastObserved,
                $fast, $slow, NULL, NULL,
                $state, $updatedAt)
            ON CONFLICT (tenant_id, profile_scope, profile_key) DO UPDATE SET
                direction = excluded.direction,
                trusted_support = excluded.trusted_support,
                baseline_version = excluded.baseline_version,
                regime_id = excluded.regime_id,
                baseline_frozen = excluded.baseline_frozen,
                observed_attempts = excluded.observed_attempts,
                observed_recipients = excluded.observed_recipients,
                last_observed_at = excluded.last_observed_at,
                fast_average_json = excluded.fast_average_json,
                slow_average_json = excluded.slow_average_json,
                bucket_state_json = excluded.bucket_state_json,
                updated_at = excluded.updated_at;
            """;

        upsert.Parameters.AddWithValue("$tenant", profile.Key.TenantId);
        upsert.Parameters.AddWithValue("$scope", profile.Key.Scope.ToString());
        upsert.Parameters.AddWithValue("$key", StorageKey(profile.Key));
        upsert.Parameters.AddWithValue(
            "$direction",
            profile.Key.Direction is { } direction ? (object)(int)direction : DBNull.Value);
        upsert.Parameters.AddWithValue("$support", profile.Baseline.TrustedSupport);
        upsert.Parameters.AddWithValue("$version", profile.Baseline.Version);
        upsert.Parameters.AddWithValue("$regime", profile.CurrentRegimeId);
        upsert.Parameters.AddWithValue("$frozen", profile.Baseline.IsFrozen ? 1 : 0);
        upsert.Parameters.AddWithValue("$attempts", profile.Observed.Attempts);
        upsert.Parameters.AddWithValue("$recipients", profile.Observed.Recipients);
        upsert.Parameters.AddWithValue("$lastObserved", Stamp(profile.Observed.LastObservedAt));
        upsert.Parameters.AddWithValue(
            "$fast",
            JsonSerializer.Serialize(
                profile.FastAverages.ToDictionary(pair => pair.Key, pair => EwmaDocument.From(pair.Value)),
                Json));
        upsert.Parameters.AddWithValue(
            "$slow",
            JsonSerializer.Serialize(
                profile.SlowAverages.ToDictionary(pair => pair.Key, pair => EwmaDocument.From(pair.Value)),
                Json));
        upsert.Parameters.AddWithValue("$state", JsonSerializer.Serialize(document, Json));
        upsert.Parameters.AddWithValue("$updatedAt", at.ToUniversalTime().ToString("O"));

        upsert.ExecuteNonQuery();
    }

    private static void ReplaceBaselineDimensions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AdaptiveProfile profile)
    {
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText =
                """
                DELETE FROM adaptive_baseline_dimension
                WHERE tenant_id = $tenant AND profile_scope = $scope AND profile_key = $key;
                """;
            clear.Parameters.AddWithValue("$tenant", profile.Key.TenantId);
            clear.Parameters.AddWithValue("$scope", profile.Key.Scope.ToString());
            clear.Parameters.AddWithValue("$key", StorageKey(profile.Key));
            clear.ExecuteNonQuery();
        }

        foreach (var (dimensionId, moments) in profile.Baseline.Dimensions)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO adaptive_baseline_dimension
                    (tenant_id, profile_scope, profile_key, dimension_id, sample_count, mean, m2)
                VALUES ($tenant, $scope, $key, $dimension, $count, $mean, $m2);
                """;
            insert.Parameters.AddWithValue("$tenant", profile.Key.TenantId);
            insert.Parameters.AddWithValue("$scope", profile.Key.Scope.ToString());
            insert.Parameters.AddWithValue("$key", StorageKey(profile.Key));
            insert.Parameters.AddWithValue("$dimension", dimensionId);
            insert.Parameters.AddWithValue("$count", moments.Count);
            insert.Parameters.AddWithValue("$mean", moments.Mean);
            insert.Parameters.AddWithValue("$m2", moments.M2);
            insert.ExecuteNonQuery();
        }
    }

    private static AdaptiveProfile? Load(SqliteConnection connection, SqliteTransaction? transaction, ProfileKey key)
    {
        string scope;
        string storageKey;
        int? direction;
        int support;
        int version;
        string? regimeId;
        bool frozen;
        long attempts;
        long recipients;
        DateTimeOffset? lastObserved;
        string? fastJson;
        string? slowJson;
        string? stateJson;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT profile_scope, profile_key, direction,
                       trusted_support, baseline_version, regime_id, baseline_frozen,
                       observed_attempts, observed_recipients, last_observed_at,
                       fast_average_json, slow_average_json, bucket_state_json
                FROM profiles
                WHERE tenant_id = $tenant AND profile_scope = $scope AND profile_key = $key;
                """;
            command.Parameters.AddWithValue("$tenant", key.TenantId);
            command.Parameters.AddWithValue("$scope", key.Scope.ToString());
            command.Parameters.AddWithValue("$key", StorageKey(key));

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            scope = reader.GetString(0);
            storageKey = reader.GetString(1);
            direction = reader.IsDBNull(2) ? null : reader.GetInt32(2);
            support = reader.GetInt32(3);
            version = reader.GetInt32(4);
            regimeId = reader.IsDBNull(5) ? null : reader.GetString(5);
            frozen = reader.GetInt32(6) == 1;
            attempts = reader.GetInt64(7);
            recipients = reader.GetInt64(8);
            lastObserved = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9));
            fastJson = reader.IsDBNull(10) ? null : reader.GetString(10);
            slowJson = reader.IsDBNull(11) ? null : reader.GetString(11);
            stateJson = reader.IsDBNull(12) ? null : reader.GetString(12);
        }

        if (!TryReadKey(key.TenantId, scope, storageKey, direction, out var reconstructed))
        {
            return null;
        }

        var document = DeserializeState(stateJson);

        var baseline = new TrustedBaseline
        {
            Version = version,
            TrustedSupport = support,
            Dimensions = LoadBaselineDimensions(connection, transaction, key),
            IsFrozen = frozen,
            RegimeId = regimeId,
            FreezeReason = document.FreezeReason,
            FrozenAt = FromUnixMs(document.FrozenAtUnixMs),
            LastPromotedAt = FromUnixMs(document.LastPromotedAtUnixMs),
        }.WithRebuiltScale(new RobustScaleOptions());

        var observed = new ObservedState
        {
            Attempts = attempts,
            RejectedAttempts = document.RejectedAttempts,
            Recipients = recipients,
            FirstObservedAt = FromUnixMs(document.FirstObservedAtUnixMs),
            LastObservedAt = lastObserved,
        };

        var profile = new AdaptiveProfile(
            reconstructed,
            options: null,
            initialRegimeId: regimeId ?? DefaultRegimeId,
            observed,
            baseline,
            RestoreSeries(document),
            RestoreRecipients(document, observed));

        profile.RestoreAverages(DeserializeAverages(fastJson), DeserializeAverages(slowJson));

        // The token this instance must present to write back. Two instances loaded from the
        // same revision both carry it, and only one of them can save.
        profile.PersistedRevision = ReadRevision(connection, transaction, reconstructed);

        return profile;
    }

    private static Dictionary<string, RunningMoments> LoadBaselineDimensions(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProfileKey key)
    {
        var moments = new Dictionary<string, RunningMoments>(StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT dimension_id, sample_count, mean, m2
            FROM adaptive_baseline_dimension
            WHERE tenant_id = $tenant AND profile_scope = $scope AND profile_key = $key;
            """;
        command.Parameters.AddWithValue("$tenant", key.TenantId);
        command.Parameters.AddWithValue("$scope", key.Scope.ToString());
        command.Parameters.AddWithValue("$key", StorageKey(key));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            moments[reader.GetString(0)] = new RunningMoments
            {
                Count = reader.GetInt32(1),
                Mean = reader.GetDouble(2),
                M2 = reader.GetDouble(3),
            };
        }

        return moments;
    }

    /// <summary>
    /// Rebuilds the recipient history, or records honestly that it could not be rebuilt.
    /// </summary>
    /// <remarks>
    /// <b>The empty case is the dangerous one.</b> A profile with observed traffic whose
    /// membership filter was not stored would come back with an empty filter, and an empty filter
    /// reports every recipient as never seen: manufacturing the most alarming signal in the
    /// profile, at scale, on every restart, for exactly the senders with the most history.
    ///
    /// <para>
    /// So a history is only <em>complete</em> when it was actually restored, or when the principal
    /// genuinely has no past to cover. Anything in between is marked incomplete, which makes
    /// novelty unknown rather than falsely alarming.
    /// </para>
    ///
    /// <para>
    /// <b>Known limitation, deliberately left.</b> This restores with
    /// <see cref="RecipientHistory.DefaultCapacity"/> and <see cref="RecipientHistory.DefaultWindow"/>
    /// rather than the host's <c>AdaptiveOptions</c>, because <c>Load</c> is not given the options:
    /// consistent with the rest of this store, but now load-bearing for a bound rather than
    /// cosmetic. A host configured with a smaller capacity would restore into a history that
    /// saturates sooner.
    /// </para>
    ///
    /// <para>
    /// Left in place because <b>the failure direction is the safe one</b>: saturating sooner sets
    /// the floor flag sooner and makes the membership filter report more false positives, which
    /// means <em>missing</em> novelty rather than inventing it, and both are reported honestly
    /// rather than lied about. Worth fixing when a deployment actually configures a non-default
    /// capacity.
    /// </para>
    /// </remarks>
    private static RecipientHistory RestoreRecipients(ProfileStateDocument document, ObservedState observed)
    {
        var history = document.SeenRecipients is null
            ? new RecipientHistory()
            : RecipientHistory.Restore(
                RecipientHistory.DefaultCapacity,
                RecipientHistory.DefaultWindow,
                document.Recipients.Select(entry => new RecipientEntry
                {
                    Key = entry.Key,
                    FirstSeen = DateTimeOffset.FromUnixTimeMilliseconds(entry.FirstSeenUnixMs),
                    LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(entry.LastSeenUnixMs),
                }),
                RecipientBloomFilter.FromBytes(Convert.FromBase64String(document.SeenRecipients)),
                document.RecipientsTruncated);

        if (document.SeenRecipients is null && observed.Attempts > 0)
        {
            history.MarkIncomplete();
        }

        return history;
    }

    private static Dictionary<string, BucketSeries>? RestoreSeries(ProfileStateDocument document)
    {
        if (document.Series.Count == 0)
        {
            return null;
        }

        var restored = new Dictionary<string, BucketSeries>(StringComparer.Ordinal);
        foreach (var series in document.Series)
        {
            restored[series.Window] = BucketSeries.Restore(
                TimeSpan.FromTicks(series.WidthTicks),
                series.Buckets.Select(bucket => new BucketSnapshot
                {
                    Index = bucket.Index,
                    SampleCount = bucket.Samples,
                    RecipientCount = bucket.Recipients,
                    RejectedCount = bucket.Rejected,
                    Dimensions =
                    [
                        .. bucket.Dimensions.Select(d => new BucketDimensionAccumulator
                        {
                            DimensionId = d.Id,
                            Sum = d.Sum,
                            Count = d.Count,
                        }),
                    ],
                }));
        }

        return restored;
    }

    private static Dictionary<string, EwmaState> DeserializeAverages(string? json) =>
        string.IsNullOrEmpty(json)
            ? new Dictionary<string, EwmaState>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, EwmaDocument>>(json, Json)?
                  .ToDictionary(pair => pair.Key, pair => pair.Value.ToState(), StringComparer.Ordinal)
              ?? new Dictionary<string, EwmaState>(StringComparer.Ordinal);

    private static ProfileStateDocument DeserializeState(string? json) =>
        string.IsNullOrEmpty(json)
            ? new ProfileStateDocument()
            : JsonSerializer.Deserialize<ProfileStateDocument>(json, Json) ?? new ProfileStateDocument();

    /// <summary>
    /// The key actually stored, which folds direction in.
    /// </summary>
    /// <remarks>
    /// The shared <c>profiles</c> primary key is (tenant, scope, key) with no direction, but
    /// this engine keeps inbound and outbound statistics distinct for the same pair, an
    /// inbound stranger and an outbound authenticated principal mean different things by the
    /// same number. Folding direction into the stored key preserves that separation without
    /// migrating a table another agent owns.
    /// </remarks>
    private static string StorageKey(ProfileKey key) => key.Direction switch
    {
        MailDirection.Inbound => key.Key + InboundSuffix,
        MailDirection.Outbound => key.Key + OutboundSuffix,
        _ => key.Key,
    };

    private static bool TryReadKey(
        string tenantId,
        string scope,
        string storageKey,
        int? direction,
        out ProfileKey key)
    {
        key = null!;

        if (!Enum.TryParse<ProfileScopeKind>(scope, ignoreCase: false, out var kind))
        {
            return false;
        }

        MailDirection? resolved = direction is null ? null : (MailDirection)direction;
        var identity = storageKey;

        if (resolved is null)
        {
            // Recover direction from the key for rows the centroid store created directly,
            // which write the raw key and leave `direction` null.
            if (storageKey.EndsWith(OutboundSuffix, StringComparison.Ordinal))
            {
                identity = storageKey[..^OutboundSuffix.Length];
                resolved = MailDirection.Outbound;
            }
            else if (storageKey.EndsWith(InboundSuffix, StringComparison.Ordinal))
            {
                identity = storageKey[..^InboundSuffix.Length];
                resolved = MailDirection.Inbound;
            }
        }
        else
        {
            identity = TrimSuffix(storageKey, resolved.Value);
        }

        key = new ProfileKey
        {
            TenantId = tenantId,
            Scope = kind,
            Key = identity,
            Direction = resolved,
        };

        return true;
    }

    private static string TrimSuffix(string value, MailDirection direction)
    {
        var suffix = direction == MailDirection.Inbound ? InboundSuffix : OutboundSuffix;
        return value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;
    }

    private static object Stamp(DateTimeOffset? at) =>
        at is null ? DBNull.Value : at.Value.ToUniversalTime().ToString("O");

    private static DateTimeOffset? FromUnixMs(long? value) =>
        value is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);

    /// <summary>
    /// Trusted baseline moments.
    /// </summary>
    /// <remarks>
    /// The shared <c>profiles</c> table keeps the baseline's summary columns (support, version,
    /// regime, frozen) but has nowhere to put the per-dimension moments the baseline is
    /// actually made of, and the fast/slow/velocity/acceleration JSON columns describe
    /// observed trends, not trusted history. Rather than mislabel one of those, the moments
    /// live in a table this project owns, cascading from the profile row.
    /// </remarks>
    private const string AdaptiveTablesDdl =
        """
        CREATE TABLE IF NOT EXISTS adaptive_baseline_dimension (
            tenant_id     TEXT    NOT NULL,
            profile_scope TEXT    NOT NULL,
            profile_key   TEXT    NOT NULL,
            dimension_id  TEXT    NOT NULL,
            sample_count  INTEGER NOT NULL,
            mean          REAL    NOT NULL,
            m2            REAL    NOT NULL,
            PRIMARY KEY (tenant_id, profile_scope, profile_key, dimension_id),
            FOREIGN KEY (tenant_id, profile_scope, profile_key)
                REFERENCES profiles (tenant_id, profile_scope, profile_key) ON DELETE CASCADE
        );

        -- Optimistic-concurrency token. Written with the profile in the same transaction, so a
        -- refused write leaves it untouched alongside everything else it guards.
        CREATE TABLE IF NOT EXISTS adaptive_profile_revision (
            tenant_id     TEXT    NOT NULL,
            profile_scope TEXT    NOT NULL,
            profile_key   TEXT    NOT NULL,
            revision      INTEGER NOT NULL,
            PRIMARY KEY (tenant_id, profile_scope, profile_key),
            FOREIGN KEY (tenant_id, profile_scope, profile_key)
                REFERENCES profiles (tenant_id, profile_scope, profile_key) ON DELETE CASCADE
        );
        """;

    internal sealed record ProfileStateDocument
    {
        public int SchemaVersion { get; init; } = 1;

        public List<BucketSeriesDocument> Series { get; init; } = [];

        public long? FirstObservedAtUnixMs { get; init; }

        /// <summary>
        /// Kept alongside the profile row's attempt counter. The shared table has one column
        /// for attempts, and abuse detection needs the rejected share of them.
        /// </summary>
        public long RejectedAttempts { get; init; }

        public long? FrozenAtUnixMs { get; init; }

        public string? FreezeReason { get; init; }

        public long? LastPromotedAtUnixMs { get; init; }

        /// <summary>
        /// Recipient keys held for distinct counting, with their first and last sighting.
        /// </summary>
        public List<RecipientDocument> Recipients { get; init; } = [];

        /// <summary>
        /// The membership filter, base64. Absent means novelty cannot be answered for this
        /// principal: the restore path marks the history incomplete rather than starting it empty.
        /// </summary>
        public string? SeenRecipients { get; init; }

        /// <summary>Whether the distinct-count set had already stopped being complete.</summary>
        public bool RecipientsTruncated { get; init; }
    }

    internal sealed record RecipientDocument
    {
        public string Key { get; init; } = string.Empty;

        public long FirstSeenUnixMs { get; init; }

        public long LastSeenUnixMs { get; init; }
    }

    internal sealed record EwmaDocument(double? Value, long? UpdatedAtUnixMs, int Updates)
    {
        public static EwmaDocument From(EwmaState state) =>
            new(state.Value, state.LastUpdatedAt?.ToUnixTimeMilliseconds(), state.Updates);

        public EwmaState ToState() => new()
        {
            Value = Value,
            LastUpdatedAt = UpdatedAtUnixMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
            Updates = Updates,
        };
    }

    internal sealed record BucketSeriesDocument
    {
        public string Window { get; init; } = string.Empty;

        public long WidthTicks { get; init; }

        public List<BucketDocument> Buckets { get; init; } = [];
    }

    internal sealed record BucketDocument
    {
        public long Index { get; init; }

        public int Samples { get; init; }

        public int Recipients { get; init; }

        public int Rejected { get; init; }

        public List<AccumulatorDocument> Dimensions { get; init; } = [];
    }

    internal sealed record AccumulatorDocument
    {
        public string Id { get; init; } = string.Empty;

        public double Sum { get; init; }

        public int Count { get; init; }
    }
}
