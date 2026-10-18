using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;
using StyloMail.Policy;

namespace StyloMail.Host.Policy;

/// <summary>SQLite-backed rule sets. A pack can only reference a live version.</summary>
/// <remarks>
/// A rule set is immutable once live: a threshold change is a new version. The store enforces that
/// by refusing an in-place change to a live or retired row, the same rule a pack follows.
/// </remarks>
public sealed class SqlitePolicyRuleSetStore : IPolicyRuleSetReader
{
    private readonly HostDatabase _database;
    private readonly TimeProvider _clock;

    public SqlitePolicyRuleSetStore(HostDatabase database, TimeProvider clock)
    {
        _database = database;
        _clock = clock;
    }

    public Task<PolicyRuleSet?> GetAsync(
        string tenantId,
        string ruleSetId,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleSetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            var row = ReadRow(connection, transaction: null, tenantId, ruleSetId, version);
            return Task.FromResult(row is null ? null : Deserialize(row.Payload));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The rule set could not be read.", ex);
        }
    }

    public Task<PolicyRuleSet?> FindLiveByVersionAsync(
        string tenantId,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT rule_set_id, version, state, content_sha256, payload, created_at, promoted_at, approver
                FROM host_policy_rule_set
                WHERE tenant_id = $tenant AND version = $version AND state = 'live'
                ORDER BY promoted_at DESC
                LIMIT 2;
                """;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$version", version);

            var rows = new List<RuleSetRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(Row(reader));
            }

            if (rows.Count > 1)
            {
                throw new PolicyLifecycleException(
                    "rule_set.ambiguous",
                    $"More than one live rule set has version '{version}', so a pack naming that version cannot be replayed unambiguously.");
            }

            return Task.FromResult(rows.Count == 0 ? null : Deserialize(rows[0].Payload));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The live rule set could not be read.", ex);
        }
    }

    public Task<IReadOnlyList<PolicyRuleSetRecord>> ListAsync(
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
                SELECT rule_set_id, version, state, content_sha256, payload, created_at, promoted_at, approver
                FROM host_policy_rule_set
                WHERE tenant_id = $tenant
                ORDER BY created_at DESC, rule_set_id DESC, version DESC;
                """;
            command.Parameters.AddWithValue("$tenant", tenantId);

            var records = new List<PolicyRuleSetRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = Row(reader);
                records.Add(new PolicyRuleSetRecord
                {
                    RuleSet = Deserialize(row.Payload),
                    State = ParseState(row.State),
                    ContentSha256 = row.ContentSha256,
                    PromotedAt = row.PromotedAt is null ? null : ParseStamp(row.PromotedAt),
                    Approver = row.Approver,
                });
            }

            return Task.FromResult<IReadOnlyList<PolicyRuleSetRecord>>(records);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The rule sets could not be listed.", ex);
        }
    }

    /// <summary>Stores a rule set at a lifecycle state. Used by the promotion slice and by tests.</summary>
    public Task<PolicyRuleSetRecord> SaveAsync(
        string tenantId,
        PolicyRuleSet ruleSet,
        PolicyRuleSetState state,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();

        var violations = RuleSetValidator.Validate(ruleSet);
        if (violations.Count > 0)
        {
            throw new PolicyValidationException(
                "rule_set.invalid",
                string.Join("; ", violations.Select(violation => $"{violation.Code}: {violation.Message}")),
                [.. violations.Select(violation => violation.Code)]);
        }

        var payload = JsonSerializer.Serialize(ruleSet, HostJson.Options);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var now = _clock.GetUtcNow();

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var existing = ReadRow(connection, transaction, tenantId, ruleSet.Id, ruleSet.Version);
            if (existing is not null
                && existing.State is "live" or "retired"
                && !string.Equals(existing.ContentSha256, hash, StringComparison.Ordinal))
            {
                throw new PolicyLifecycleException(
                    "rule_set.immutable",
                    $"'{ruleSet.Id}@{ruleSet.Version}' is {existing.State}. A threshold change is a new version.");
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO host_policy_rule_set
                        (tenant_id, rule_set_id, version, state, content_sha256, payload,
                         created_at, promoted_at, approver)
                    VALUES ($tenant, $id, $version, $state, $hash, $payload, $created, $promoted, $approver)
                    ON CONFLICT (tenant_id, rule_set_id, version) DO UPDATE SET
                        state = excluded.state,
                        content_sha256 = excluded.content_sha256,
                        payload = excluded.payload,
                        promoted_at = COALESCE(excluded.promoted_at, host_policy_rule_set.promoted_at),
                        approver = COALESCE(excluded.approver, host_policy_rule_set.approver);
                    """;
                command.Parameters.AddWithValue("$tenant", tenantId);
                command.Parameters.AddWithValue("$id", ruleSet.Id);
                command.Parameters.AddWithValue("$version", ruleSet.Version);
                command.Parameters.AddWithValue("$state", state.ToString().ToLowerInvariant());
                command.Parameters.AddWithValue("$hash", hash);
                command.Parameters.AddWithValue("$payload", payload);
                command.Parameters.AddWithValue("$created", Stamp(now));
                command.Parameters.AddWithValue("$promoted", state == PolicyRuleSetState.Live ? Stamp(now) : DBNull.Value);
                command.Parameters.AddWithValue("$approver", (object?)actor ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            using (var @event = connection.CreateCommand())
            {
                @event.Transaction = transaction;
                @event.CommandText =
                    """
                    INSERT INTO host_policy_rule_event
                        (event_id, tenant_id, rule_set_id, version, event, actor, reason, at, payload)
                    VALUES ($eventId, $tenant, $id, $version, $event, $actor, $reason, $at, NULL);
                    """;
                @event.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("N"));
                @event.Parameters.AddWithValue("$tenant", tenantId);
                @event.Parameters.AddWithValue("$id", ruleSet.Id);
                @event.Parameters.AddWithValue("$version", ruleSet.Version);
                @event.Parameters.AddWithValue("$event", state.ToString().ToLowerInvariant());
                @event.Parameters.AddWithValue("$actor", actor);
                @event.Parameters.AddWithValue("$reason", "rule set stored");
                @event.Parameters.AddWithValue("$at", Stamp(now));
                @event.ExecuteNonQuery();
            }

            transaction.Commit();

            return Task.FromResult(new PolicyRuleSetRecord
            {
                RuleSet = ruleSet,
                State = state,
                ContentSha256 = hash,
                PromotedAt = state == PolicyRuleSetState.Live ? now : null,
                Approver = actor,
            });
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The rule set could not be stored.", ex);
        }
    }

    private sealed record RuleSetRow(
        string RuleSetId,
        string Version,
        string State,
        string ContentSha256,
        string Payload,
        string CreatedAt,
        string? PromotedAt,
        string? Approver);

    private static RuleSetRow? ReadRow(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tenantId,
        string ruleSetId,
        string version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT rule_set_id, version, state, content_sha256, payload, created_at, promoted_at, approver
            FROM host_policy_rule_set
            WHERE tenant_id = $tenant AND rule_set_id = $id AND version = $version;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", ruleSetId);
        command.Parameters.AddWithValue("$version", version);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Row(reader) : null;
    }

    private static RuleSetRow Row(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static PolicyRuleSet Deserialize(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<PolicyRuleSet>(payload, HostJson.Options)
                ?? throw new JsonException("The rule-set payload was empty.");
        }
        catch (JsonException ex)
        {
            throw new StorageUnavailableException("A stored rule set could not be read.", ex);
        }
    }

    private static PolicyRuleSetState ParseState(string state) => state switch
    {
        "candidate" => PolicyRuleSetState.Candidate,
        "live" => PolicyRuleSetState.Live,
        "retired" => PolicyRuleSetState.Retired,
        _ => PolicyRuleSetState.Candidate,
    };

    private static string Stamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
