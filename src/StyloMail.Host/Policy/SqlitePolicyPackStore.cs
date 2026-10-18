using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;
using StyloMail.Policy;

namespace StyloMail.Host.Policy;

/// <summary>Where a stored rule set sits in its lifecycle.</summary>
public enum PolicyRuleSetState
{
    Candidate = 0,
    Live = 1,
    Retired = 2,
}

/// <summary>The effective lifecycle state of a binding, derived at read time.</summary>
public enum PolicyBindingState
{
    Active = 0,
    Revoked = 1,
    Expired = 2,
    Pending = 3,
}

/// <summary>A pack refused because its content violates the policy contract.</summary>
public sealed class PolicyValidationException : Exception
{
    public PolicyValidationException(string code, string message, IReadOnlyList<string>? violationCodes = null)
        : base(message)
    {
        Code = code;
        ViolationCodes = violationCodes ?? [];
    }

    public string Code { get; }

    public IReadOnlyList<string> ViolationCodes { get; }
}

/// <summary>A lifecycle transition refused because the row is in the wrong state.</summary>
public sealed class PolicyLifecycleException : Exception
{
    public PolicyLifecycleException(string code, string message)
        : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>A stored binding with its state computed at the instant it was read.</summary>
public sealed record PolicyBindingRecord
{
    public required PolicyBinding Binding { get; init; }

    public required PolicyBindingState State { get; init; }
}

/// <summary>A stored rule set with its lifecycle state.</summary>
public sealed record PolicyRuleSetRecord
{
    public required PolicyRuleSet RuleSet { get; init; }

    public required PolicyRuleSetState State { get; init; }

    public required string ContentSha256 { get; init; }

    public DateTimeOffset? PromotedAt { get; init; }

    public string? Approver { get; init; }
}

/// <summary>
/// SQLite-backed policy packs, rule sets and bindings.
/// </summary>
/// <remarks>
/// <para>
/// <b>The decision path is read-only.</b> <see cref="SqlitePolicyPackStore"/> implements the
/// read-only <see cref="IPolicyPackReader"/>; the lifecycle methods are additional members reached
/// only by the administration endpoints. No evaluator or assessment composition can call them.
/// </para>
/// <para>
/// <b>An approved pack is immutable and named by its bytes.</b> The content hash is computed over a
/// canonical serialisation with ordinally sorted category keys, so the same pack authored twice
/// hashes the same and an approval names the payload rather than a version label.
/// </para>
/// <para>
/// <b>Tenant is an explicit argument and part of every predicate.</b> There is no ambient tenant,
/// and no route accepts one from the body.
/// </para>
/// </remarks>
public sealed class SqlitePolicyPackStore : IPolicyPackReader
{
    private const string Effective = "effective";
    private const string Approved = "approved";
    private const string Draft = "draft";
    private const string Superseded = "superseded";
    private const string Withdrawn = "withdrawn";

    private readonly HostDatabase _database;
    private readonly IPolicyRuleSetReader _ruleSets;
    private readonly TimeProvider _clock;

    public SqlitePolicyPackStore(
        HostDatabase database,
        IPolicyRuleSetReader ruleSets,
        TimeProvider clock)
    {
        _database = database;
        _ruleSets = ruleSets;
        _clock = clock;
    }

    // ---------------------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------------------

    public Task<PolicyPack?> GetVersionAsync(
        string tenantId,
        string packId,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            var row = ReadRow(connection, transaction: null, tenantId, packId, version);
            return Task.FromResult(row is null ? null : Deserialize(row.Payload));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy pack could not be read.", ex);
        }
    }

    public Task<PolicyPack?> GetEffectiveAsync(
        string tenantId,
        string jurisdiction,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jurisdiction);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var row = ReadLatestDue(connection, transaction, tenantId, jurisdiction, now);

            if (row is null)
            {
                transaction.Commit();
                return Task.FromResult<PolicyPack?>(null);
            }

            if (row.State == Approved)
            {
                // A scheduled pack became due. Promotion happens where the read observes it, so no
                // background job is required for the pack to take effect. The transaction is what
                // makes "one effective pack per jurisdiction" true under concurrent readers.
                SupersedeCurrent(connection, transaction, tenantId, row.Jurisdiction, row, now);
                SetState(connection, transaction, tenantId, row.PackId, row.Version, Effective);
                InsertEvent(connection, transaction, tenantId, row.PackId, row.Version,
                    "activated", actor: "policy-scheduler", reason: "scheduled effective date reached", now, null);
            }

            transaction.Commit();
            return Task.FromResult<PolicyPack?>(Deserialize(row.Payload));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The effective policy pack could not be read.", ex);
        }
    }

    public Task<PolicyPackSummary?> GetSummaryAsync(
        string tenantId,
        string packId,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            var row = ReadRow(connection, transaction: null, tenantId, packId, version);
            return Task.FromResult(row is null ? null : Summary(tenantId, row));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy pack summary could not be read.", ex);
        }
    }

    public Task<IReadOnlyList<PolicyPackSummary>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT pack_id, version, state, jurisdiction, content_sha256, author, approver,
                       second_approver, effective_from, review_due, created_at, approved_at, superseded_by
                FROM host_policy_pack
                WHERE tenant_id = $tenant
                ORDER BY created_at DESC, pack_id DESC, version DESC;
                """;
            command.Parameters.AddWithValue("$tenant", tenantId);

            var summaries = new List<PolicyPackSummary>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                summaries.Add(Summary(tenantId, reader));
            }

            return Task.FromResult<IReadOnlyList<PolicyPackSummary>>(summaries);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy packs could not be listed.", ex);
        }
    }

    /// <summary>The currently effective row for a jurisdiction, if any, ignoring the clock.</summary>
    internal PackRow? FindEffectiveRow(string tenantId, string jurisdiction)
    {
        using var connection = _database.Open();
        return ReadEffective(connection, transaction: null, tenantId, jurisdiction);
    }

    // ---------------------------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------------------------

    public Task<PolicyPackSummary> SaveDraftAsync(
        string tenantId,
        PolicyPack pack,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(pack.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new PolicyLifecycleException("pack.tenant_mismatch", "A pack may only be stored in its own tenant.");
        }

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var existing = ReadRow(connection, transaction, tenantId, pack.Id, pack.Version);
            if (existing is not null && existing.State != Draft)
            {
                throw new PolicyLifecycleException(
                    "pack.version_immutable",
                    $"'{pack.Id}@{pack.Version}' is {existing.State} and cannot be edited. A change is a new version.");
            }

            // A draft is validated for content, but not against the previous pack: the second
            // approver who is required for a lowering does not exist until approval. The approval
            // step validates against the effective pack with the approver and countersignature in
            // hand, so a lowering cannot become effective without both.
            Validate(pack, previous: null, validateApprover: false);

            var payload = Canonical(pack);
            var hash = Hash(payload);

            if (existing is null)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO host_policy_pack
                        (tenant_id, pack_id, version, state, jurisdiction, content_sha256, payload,
                         author, approver, second_approver, effective_from, review_due, created_at,
                         approved_at, superseded_by)
                    VALUES ($tenant, $pack, $version, $state, $jurisdiction, $hash, $payload,
                            $author, NULL, NULL, $effective, $reviewDue, $created, NULL, NULL);
                    """;
                AddPackParameters(insert, pack, payload, hash, Draft, DateTimeOffset.Parse(
                    existing?.CreatedAt ?? Stamp(_clock.GetUtcNow()), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                insert.ExecuteNonQuery();
            }
            else
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE host_policy_pack
                    SET payload = $payload, content_sha256 = $hash, jurisdiction = $jurisdiction,
                        author = $author, effective_from = $effective, review_due = $reviewDue
                    WHERE tenant_id = $tenant AND pack_id = $pack AND version = $version AND state = 'draft';
                    """;
                update.Parameters.AddWithValue("$tenant", tenantId);
                update.Parameters.AddWithValue("$pack", pack.Id);
                update.Parameters.AddWithValue("$version", pack.Version);
                update.Parameters.AddWithValue("$payload", payload);
                update.Parameters.AddWithValue("$hash", hash);
                update.Parameters.AddWithValue("$jurisdiction", pack.Context.Jurisdiction);
                update.Parameters.AddWithValue("$author", pack.Author);
                update.Parameters.AddWithValue("$effective", Stamp(pack.EffectiveFrom));
                update.Parameters.AddWithValue("$reviewDue", pack.ReviewDue is { } due ? Stamp(due) : DBNull.Value);
                update.ExecuteNonQuery();
            }

            InsertEvent(connection, transaction, tenantId, pack.Id, pack.Version,
                existing is null ? "drafted" : "edited", actor, "draft saved", _clock.GetUtcNow(), null);

            transaction.Commit();

            return Task.FromResult(new PolicyPackSummary
            {
                TenantId = tenantId,
                PackId = pack.Id,
                Version = pack.Version,
                State = PolicyPackState.Draft,
                Jurisdiction = pack.Context.Jurisdiction,
                ContentSha256 = hash,
                Author = pack.Author,
                EffectiveFrom = pack.EffectiveFrom,
                ReviewDue = pack.ReviewDue,
                CreatedAt = existing is null ? _clock.GetUtcNow() : ParseStamp(existing.CreatedAt!),
            });
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy pack draft could not be stored.", ex);
        }
    }

    public Task<PolicyPackSummary> ApproveAsync(
        string tenantId,
        string packId,
        string version,
        string approver,
        string? secondApprover,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var row = ReadRow(connection, transaction, tenantId, packId, version)
                ?? throw new PolicyLifecycleException("pack.not_found", "No such pack version.");

            if (row.State != Draft)
            {
                throw new PolicyLifecycleException(
                    "pack.version_immutable",
                    $"'{packId}@{version}' is {row.State} and cannot be approved again. An approved pack is immutable.");
            }

            var pack = Deserialize(row.Payload) with
            {
                Approver = approver,
                SecondApprover = secondApprover,
            };

            var previous = ReadEffective(connection, transaction, tenantId, pack.Context.Jurisdiction);
            Validate(pack, previous is null ? null : Deserialize(previous.Payload), validateApprover: true);

            var payload = Canonical(pack);
            var hash = Hash(payload);
            var now = _clock.GetUtcNow();

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE host_policy_pack
                SET state = 'approved', payload = $payload, content_sha256 = $hash,
                    approver = $approver, second_approver = $second, approved_at = $now
                WHERE tenant_id = $tenant AND pack_id = $pack AND version = $version;
                """;
            update.Parameters.AddWithValue("$tenant", tenantId);
            update.Parameters.AddWithValue("$pack", packId);
            update.Parameters.AddWithValue("$version", version);
            update.Parameters.AddWithValue("$payload", payload);
            update.Parameters.AddWithValue("$hash", hash);
            update.Parameters.AddWithValue("$approver", approver);
            update.Parameters.AddWithValue("$second", (object?)secondApprover ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", Stamp(now));
            update.ExecuteNonQuery();

            InsertEvent(connection, transaction, tenantId, packId, version, "approved", approver,
                secondApprover is null ? "approved" : $"approved with second approver {secondApprover}", now, null);

            transaction.Commit();

            return Task.FromResult(new PolicyPackSummary
            {
                TenantId = tenantId,
                PackId = packId,
                Version = version,
                State = PolicyPackState.Approved,
                Jurisdiction = pack.Context.Jurisdiction,
                ContentSha256 = hash,
                Author = pack.Author,
                Approver = approver,
                SecondApprover = secondApprover,
                EffectiveFrom = pack.EffectiveFrom,
                ReviewDue = pack.ReviewDue,
                CreatedAt = row.CreatedAt is null ? null : ParseStamp(row.CreatedAt),
                ApprovedAt = now,
            });
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy pack approval could not be stored.", ex);
        }
    }

    public async Task<PolicyPackSummary> ActivateAsync(
        string tenantId,
        string packId,
        string version,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();

        var row = await GetSummaryRowAsync(tenantId, packId, version, cancellationToken).ConfigureAwait(false)
            ?? throw new PolicyLifecycleException("pack.not_found", "No such pack version.");

        if (row.State == PolicyPackState.Effective)
        {
            return row;
        }

        if (row.State != PolicyPackState.Approved)
        {
            throw new PolicyLifecycleException(
                "pack.not_approved",
                $"'{packId}@{version}' is {row.State}; only an approved pack can be activated.");
        }

        var pack = await GetVersionAsync(tenantId, packId, version, cancellationToken).ConfigureAwait(false)
            ?? throw new PolicyLifecycleException("pack.not_found", "No such pack version.");

        if (row.ApprovedAt is null || pack.EffectiveFrom < row.ApprovedAt)
        {
            throw new PolicyValidationException(
                "pack.effective_before_approval",
                "A pack cannot take effect before it was approved. Backdating would change the explanation of decisions already made.");
        }

        var ruleSet = await _ruleSets
            .FindLiveByVersionAsync(tenantId, pack.RuleSetVersion, cancellationToken)
            .ConfigureAwait(false);

        if (ruleSet is null)
        {
            throw new PolicyValidationException(
                "pack.rule_set_not_live",
                $"Rule set '{pack.RuleSetVersion}' is not live for this tenant, so the pack would reference rules that cannot be replayed.");
        }

        var now = _clock.GetUtcNow();

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            if (pack.EffectiveFrom > now)
            {
                InsertEvent(connection, transaction, tenantId, packId, version, "scheduled", actor,
                    $"scheduled for {Stamp(pack.EffectiveFrom)}", now, null);
                transaction.Commit();

                return row with { State = PolicyPackState.Approved };
            }

            var later = ReadLaterDue(connection, transaction, tenantId, pack.Context.Jurisdiction, pack.EffectiveFrom, now);
            if (later is not null)
            {
                throw new PolicyLifecycleException(
                    "pack.later_pack_pending",
                    $"'{later.PackId}@{later.Version}' is due later than this pack and already governs. Activating this version would move policy backwards.");
            }

            var current = ReadEffective(connection, transaction, tenantId, pack.Context.Jurisdiction);
            if (current is not null)
            {
                SetSuperseded(connection, transaction, tenantId, current, packId, version, now, actor);
            }

            SetState(connection, transaction, tenantId, packId, version, Effective);
            InsertEvent(connection, transaction, tenantId, packId, version, "activated", actor,
                "pack is effective", now, null);

            transaction.Commit();

            return row with { State = PolicyPackState.Effective, SupersededBy = null };
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy pack could not be activated.", ex);
        }
    }

    public Task<PolicyPackSummary> WithdrawAsync(
        string tenantId,
        string packId,
        string version,
        string actor,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var row = ReadRow(connection, transaction, tenantId, packId, version)
                ?? throw new PolicyLifecycleException("pack.not_found", "No such pack version.");

            if (row.State == Effective)
            {
                throw new PolicyLifecycleException(
                    "pack.effective_requires_deactivate",
                    "An effective pack is not withdrawn in place. Activate a successor, or deactivate the pack path explicitly.");
            }

            if (row.State is not (Draft or Approved))
            {
                throw new PolicyLifecycleException("pack.not_withdrawable", $"'{packId}@{version}' is {row.State}.");
            }

            var now = _clock.GetUtcNow();
            SetState(connection, transaction, tenantId, packId, version, Withdrawn);
            InsertEvent(connection, transaction, tenantId, packId, version, "withdrawn", actor,
                string.IsNullOrWhiteSpace(reason) ? "withdrawn" : reason, now, null);
            transaction.Commit();

            return Task.FromResult(Summary(tenantId, row with { State = Withdrawn }));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy pack could not be withdrawn.", ex);
        }
    }

    public Task<PolicyPackSummary> DeactivateAsync(
        string tenantId,
        string packId,
        string version,
        string actor,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var row = ReadRow(connection, transaction, tenantId, packId, version)
                ?? throw new PolicyLifecycleException("pack.not_found", "No such pack version.");

            if (row.State != Effective)
            {
                throw new PolicyLifecycleException(
                    "pack.not_effective",
                    $"'{packId}@{version}' is {row.State}; only an effective pack can be deactivated.");
            }

            var now = _clock.GetUtcNow();
            SetState(connection, transaction, tenantId, packId, version, Withdrawn);
            InsertEvent(connection, transaction, tenantId, packId, version, "deactivated", actor,
                string.IsNullOrWhiteSpace(reason) ? "pack path returned to the legacy engine" : reason, now, null);
            transaction.Commit();

            return Task.FromResult(Summary(tenantId, row with { State = Withdrawn }));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy pack could not be deactivated.", ex);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Rows
    // ---------------------------------------------------------------------------------------------

    internal sealed record PackRow(
        string PackId,
        string Version,
        string State,
        string Jurisdiction,
        string Payload,
        string ContentSha256,
        string Author,
        string? Approver,
        string? SecondApprover,
        string? EffectiveFrom,
        string? ReviewDue,
        string? CreatedAt,
        string? ApprovedAt,
        string? SupersededBy);

    internal static PolicyPackSummary Summary(string tenantId, PackRow row) => new()
    {
        TenantId = tenantId,
        PackId = row.PackId,
        Version = row.Version,
        State = ParseState(row.State),
        Jurisdiction = row.Jurisdiction,
        ContentSha256 = row.ContentSha256,
        Author = row.Author,
        Approver = row.Approver,
        SecondApprover = row.SecondApprover,
        EffectiveFrom = row.EffectiveFrom is null ? null : ParseStamp(row.EffectiveFrom),
        ReviewDue = row.ReviewDue is null ? null : ParseStamp(row.ReviewDue),
        CreatedAt = row.CreatedAt is null ? null : ParseStamp(row.CreatedAt),
        ApprovedAt = row.ApprovedAt is null ? null : ParseStamp(row.ApprovedAt),
        SupersededBy = row.SupersededBy,
    };

    private static PolicyPackSummary Summary(string tenantId, SqliteDataReader reader) => new()
    {
        TenantId = tenantId,
        PackId = reader.GetString(0),
        Version = reader.GetString(1),
        State = ParseState(reader.GetString(2)),
        Jurisdiction = reader.GetString(3),
        ContentSha256 = reader.GetString(4),
        Author = reader.GetString(5),
        Approver = reader.IsDBNull(6) ? null : reader.GetString(6),
        SecondApprover = reader.IsDBNull(7) ? null : reader.GetString(7),
        EffectiveFrom = reader.IsDBNull(8) ? null : ParseStamp(reader.GetString(8)),
        ReviewDue = reader.IsDBNull(9) ? null : ParseStamp(reader.GetString(9)),
        CreatedAt = reader.IsDBNull(10) ? null : ParseStamp(reader.GetString(10)),
        ApprovedAt = reader.IsDBNull(11) ? null : ParseStamp(reader.GetString(11)),
        SupersededBy = reader.IsDBNull(12) ? null : reader.GetString(12),
    };

    private async Task<PolicyPackSummary?> GetSummaryRowAsync(
        string tenantId,
        string packId,
        string version,
        CancellationToken cancellationToken)
    {
        var list = await ListAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault(summary =>
            string.Equals(summary.PackId, packId, StringComparison.Ordinal)
            && string.Equals(summary.Version, version, StringComparison.Ordinal));
    }

    private static PackRow? ReadRow(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tenantId,
        string packId,
        string version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT pack_id, version, state, jurisdiction, payload, content_sha256, author, approver,
                   second_approver, effective_from, review_due, created_at, approved_at, superseded_by
            FROM host_policy_pack
            WHERE tenant_id = $tenant AND pack_id = $pack AND version = $version;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$pack", packId);
        command.Parameters.AddWithValue("$version", version);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new PackRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13))
            : null;
    }

    private static PackRow? ReadEffective(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tenantId,
        string jurisdiction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT pack_id, version, state, jurisdiction, payload, content_sha256, author, approver,
                   second_approver, effective_from, review_due, created_at, approved_at, superseded_by
            FROM host_policy_pack
            WHERE tenant_id = $tenant AND jurisdiction = $jurisdiction AND state = 'effective'
            ORDER BY effective_from DESC, approved_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$jurisdiction", jurisdiction);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new PackRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13))
            : null;
    }

    /// <summary>
    /// The latest due pack. Ordering is descending because a pack takes effect in chronological
    /// order: when a scheduled replacement becomes due it must supersede the pack it replaces, not
    /// lose to it because the older pack's effective instant sorts first.
    /// </summary>
    private static PackRow? ReadLatestDue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string jurisdiction,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT pack_id, version, state, jurisdiction, payload, content_sha256, author, approver,
                   second_approver, effective_from, review_due, created_at, approved_at, superseded_by
            FROM host_policy_pack
            WHERE tenant_id = $tenant AND jurisdiction = $jurisdiction
              AND state IN ('effective', 'approved')
              AND effective_from IS NOT NULL AND effective_from <= $now
            ORDER BY effective_from DESC, approved_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$jurisdiction", jurisdiction);
        command.Parameters.AddWithValue("$now", Stamp(now));

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new PackRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13))
            : null;
    }

    private static PackRow? ReadLaterDue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string jurisdiction,
        DateTimeOffset effectiveFrom,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT pack_id, version, state, jurisdiction, payload, content_sha256, author, approver,
                   second_approver, effective_from, review_due, created_at, approved_at, superseded_by
            FROM host_policy_pack
            WHERE tenant_id = $tenant AND jurisdiction = $jurisdiction AND state = 'approved'
              AND effective_from IS NOT NULL AND effective_from <= $now AND effective_from > $this
            ORDER BY effective_from ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$jurisdiction", jurisdiction);
        command.Parameters.AddWithValue("$now", Stamp(now));
        command.Parameters.AddWithValue("$this", Stamp(effectiveFrom));

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new PackRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13))
            : null;
    }

    private static void SupersedeCurrent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string jurisdiction,
        PackRow incoming,
        DateTimeOffset now)
    {
        var current = ReadEffective(connection, transaction, tenantId, jurisdiction);
        if (current is not null)
        {
            SetSuperseded(connection, transaction, tenantId, current, incoming.PackId, incoming.Version, now, "policy-scheduler");
        }
    }

    private static void SetSuperseded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        PackRow current,
        string byPackId,
        string byVersion,
        DateTimeOffset now,
        string actor)
    {
        using var supersede = connection.CreateCommand();
        supersede.Transaction = transaction;
        supersede.CommandText =
            """
            UPDATE host_policy_pack
            SET state = 'superseded', superseded_by = $by
            WHERE tenant_id = $tenant AND pack_id = $pack AND version = $version AND state = 'effective';
            """;
        supersede.Parameters.AddWithValue("$tenant", tenantId);
        supersede.Parameters.AddWithValue("$pack", current.PackId);
        supersede.Parameters.AddWithValue("$version", current.Version);
        supersede.Parameters.AddWithValue("$by", $"{byPackId}@{byVersion}");
        supersede.ExecuteNonQuery();

        InsertEvent(connection, transaction, tenantId, current.PackId, current.Version, "superseded",
            actor, $"superseded by {byPackId}@{byVersion}", now, null);
    }

    private static void SetState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string packId,
        string version,
        string state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE host_policy_pack SET state = $state
            WHERE tenant_id = $tenant AND pack_id = $pack AND version = $version;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$pack", packId);
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }

    private static void InsertEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string packId,
        string version,
        string @event,
        string actor,
        string reason,
        DateTimeOffset at,
        string? payload)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO host_policy_pack_event
                (event_id, tenant_id, pack_id, version, event, actor, reason, at, payload)
            VALUES ($id, $tenant, $pack, $version, $event, $actor, $reason, $at, $payload);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$pack", packId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$event", @event);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$at", Stamp(at));
        command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void Validate(PolicyPack pack, PolicyPack? previous, bool validateApprover)
    {
        var validation = PolicyPackValidator.Validate(pack, previous, validateApprover);
        if (validation.IsValid)
        {
            return;
        }

        throw new PolicyValidationException(
            "pack.invalid",
            string.Join("; ", validation.Violations.Select(violation => $"{violation.Code}: {violation.Message}")),
            [.. validation.Violations.Select(violation => violation.Code)]);
    }

    private static void AddPackParameters(
        SqliteCommand command,
        PolicyPack pack,
        string payload,
        string hash,
        string state,
        DateTimeOffset createdAt)
    {
        command.Parameters.AddWithValue("$tenant", pack.TenantId);
        command.Parameters.AddWithValue("$pack", pack.Id);
        command.Parameters.AddWithValue("$version", pack.Version);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$jurisdiction", pack.Context.Jurisdiction);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$author", pack.Author);
        command.Parameters.AddWithValue("$effective", Stamp(pack.EffectiveFrom));
        command.Parameters.AddWithValue("$reviewDue", pack.ReviewDue is { } due ? Stamp(due) : DBNull.Value);
        command.Parameters.AddWithValue("$created", Stamp(createdAt));
    }

    private static PolicyPack Deserialize(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<PolicyPack>(payload, HostJson.Options)
                ?? throw new JsonException("The policy pack payload was empty.");
        }
        catch (JsonException ex)
        {
            throw new StorageUnavailableException("A stored policy pack could not be read.", ex);
        }
    }

    internal static string Canonical(PolicyPack pack)
    {
        var sorted = new SortedDictionary<string, ToleranceLevel>(StringComparer.Ordinal);
        foreach (var (category, level) in pack.CategoryLevels)
        {
            sorted[category] = level;
        }

        return JsonSerializer.Serialize(pack with { CategoryLevels = sorted }, HostJson.Options);
    }

    internal static string Hash(string payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    internal static PolicyPackState ParseState(string state) => state switch
    {
        Draft => PolicyPackState.Draft,
        Approved => PolicyPackState.Approved,
        Effective => PolicyPackState.Effective,
        Superseded => PolicyPackState.Superseded,
        Withdrawn => PolicyPackState.Withdrawn,
        _ => PolicyPackState.Draft,
    };

    internal static string Stamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
