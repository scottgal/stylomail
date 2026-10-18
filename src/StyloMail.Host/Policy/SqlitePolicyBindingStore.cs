using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;
using StyloMail.Policy;

namespace StyloMail.Host.Policy;

/// <summary>SQLite-backed scope bindings and their audit events.</summary>
/// <remarks>
/// <para>
/// A binding is validated against its pack at creation, including the hard-floor rule and the
/// capture rule. It is never edited: revocation is an event, and a replacement is a new binding.
/// </para>
/// <para>
/// Expiry is derived at read time. There is no sweeper, so a binding that has passed its expiry
/// cannot be read as active because a background job did not run.
/// </para>
/// </remarks>
public sealed class SqlitePolicyBindingStore
{
    private readonly HostDatabase _database;
    private readonly IPolicyPackReader _packs;
    private readonly TimeProvider _clock;

    public SqlitePolicyBindingStore(HostDatabase database, IPolicyPackReader packs, TimeProvider clock)
    {
        _database = database;
        _packs = packs;
        _clock = clock;
    }

    public Task<PolicyBindingRecord?> GetAsync(
        string tenantId,
        string bindingId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            var row = ReadRow(connection, transaction: null, tenantId, bindingId);
            return Task.FromResult(row is null ? null : Record(row, _clock.GetUtcNow()));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy binding could not be read.", ex);
        }
    }

    public Task<IReadOnlyList<PolicyBindingRecord>> ListAsync(
        string tenantId,
        BindingScope? scopeKind,
        string? scopeId,
        bool activeOnly,
        DateTimeOffset now,
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
                SELECT binding_id, state, payload, starts_at, expires_at, review_by
                FROM host_policy_binding
                WHERE tenant_id = $tenant
                  AND ($kind IS NULL OR scope_kind = $kind)
                  AND ($scope IS NULL OR scope_id = $scope)
                ORDER BY starts_at DESC, binding_id DESC;
                """;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$kind", scopeKind is null ? DBNull.Value : scopeKind.Value.ToString());
            command.Parameters.AddWithValue("$scope", (object?)scopeId ?? DBNull.Value);

            var records = new List<PolicyBindingRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var payload = reader.GetString(2);
                var binding = Deserialize(payload);
                var state = StateOf(
                    reader.GetString(1),
                    ParseStamp(reader.GetString(3)),
                    ParseStamp(reader.GetString(4)),
                    now);

                if (activeOnly && state != PolicyBindingState.Active)
                {
                    continue;
                }

                records.Add(new PolicyBindingRecord { Binding = binding, State = state });
            }

            return Task.FromResult<IReadOnlyList<PolicyBindingRecord>>(records);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy bindings could not be listed.", ex);
        }
    }

    public async Task<PolicyBindingRecord> CreateAsync(
        string tenantId,
        PolicyBinding binding,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(binding.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new PolicyLifecycleException("binding.tenant_mismatch", "A binding may only be stored in its own tenant.");
        }

        if (string.IsNullOrWhiteSpace(binding.PackId) || string.IsNullOrWhiteSpace(binding.PackVersion))
        {
            throw new PolicyValidationException(
                "binding.pack_required",
                "A binding must name the pack id and version it was authored against.");
        }

        var summary = await _packs
            .GetSummaryAsync(tenantId, binding.PackId, binding.PackVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PolicyValidationException(
                "binding.pack_not_found",
                $"Pack '{binding.PackId}@{binding.PackVersion}' was not found for this tenant.");

        if (summary.State != PolicyPackState.Effective)
        {
            throw new PolicyValidationException(
                "binding.pack_not_effective",
                $"Pack '{binding.PackId}@{binding.PackVersion}' is {summary.State}; a binding must be authored against the effective pack.");
        }

        var pack = await _packs
            .GetVersionAsync(tenantId, binding.PackId, binding.PackVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PolicyValidationException(
                "binding.pack_not_found",
                $"Pack '{binding.PackId}@{binding.PackVersion}' was not found for this tenant.");

        if (!string.Equals(pack.Id, binding.PackId, StringComparison.Ordinal)
            || !string.Equals(pack.Version, binding.PackVersion, StringComparison.Ordinal))
        {
            throw new PolicyValidationException(
                "binding.pack_mismatch",
                "The binding names a pack id and version that do not match the stored pack.");
        }

        var validation = PolicyBindingValidator.Validate(binding, pack);
        if (!validation.IsValid)
        {
            throw new PolicyValidationException(
                "binding.invalid",
                string.Join("; ", validation.Violations.Select(violation => $"{violation.Code}: {violation.Message}")),
                [.. validation.Violations.Select(violation => violation.Code)]);
        }

        var payload = JsonSerializer.Serialize(binding, HostJson.Options);
        var now = _clock.GetUtcNow();

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO host_policy_binding
                        (tenant_id, binding_id, state, scope_kind, scope_id, action, category_id,
                         target_level, starts_at, expires_at, review_by, payload)
                    VALUES ($tenant, $id, 'active', $kind, $scope, $action, $category,
                            $level, $starts, $expires, $review, $payload);
                    """;
                command.Parameters.AddWithValue("$tenant", tenantId);
                command.Parameters.AddWithValue("$id", binding.Id);
                command.Parameters.AddWithValue("$kind", binding.ScopeKind.ToString());
                command.Parameters.AddWithValue("$scope", binding.ScopeValue);
                command.Parameters.AddWithValue("$action", binding.Action.ToString());
                command.Parameters.AddWithValue("$category", (object?)binding.CategoryId ?? DBNull.Value);
                command.Parameters.AddWithValue("$level", binding.TargetLevel is null ? DBNull.Value : binding.TargetLevel.Value.ToString());
                command.Parameters.AddWithValue("$starts", Stamp(binding.StartsAt));
                command.Parameters.AddWithValue("$expires", Stamp(binding.ExpiresAt));
                command.Parameters.AddWithValue("$review", Stamp(binding.ReviewBy));
                command.Parameters.AddWithValue("$payload", payload);
                command.ExecuteNonQuery();
            }

            InsertEvent(connection, transaction, tenantId, binding.Id, "created", actor, binding.Reason, now);
            transaction.Commit();

            return new PolicyBindingRecord
            {
                Binding = binding,
                State = StateOf("active", binding.StartsAt, binding.ExpiresAt, now),
            };
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new PolicyLifecycleException("binding.duplicate", "That binding id already exists.");
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy binding could not be stored.", ex);
        }
    }

    public Task<PolicyBindingRecord> RevokeAsync(
        string tenantId,
        string bindingId,
        string actor,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var row = ReadRow(connection, transaction, tenantId, bindingId)
                ?? throw new PolicyLifecycleException("binding.not_found", "No such binding.");

            if (row.State == "revoked")
            {
                throw new PolicyLifecycleException("binding.already_revoked", "That binding is already revoked.");
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE host_policy_binding SET state = 'revoked' WHERE tenant_id = $tenant AND binding_id = $id;";
                command.Parameters.AddWithValue("$tenant", tenantId);
                command.Parameters.AddWithValue("$id", bindingId);
                command.ExecuteNonQuery();
            }

            var now = _clock.GetUtcNow();
            InsertEvent(connection, transaction, tenantId, bindingId, "revoked", actor,
                string.IsNullOrWhiteSpace(reason) ? "revoked" : reason, now);
            transaction.Commit();

            return Task.FromResult(new PolicyBindingRecord
            {
                Binding = Deserialize(row.Payload),
                State = PolicyBindingState.Revoked,
            });
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The policy binding could not be revoked.", ex);
        }
    }

    private sealed record BindingRow(string BindingId, string State, string Payload, string StartsAt, string ExpiresAt, string ReviewBy);

    private static BindingRow? ReadRow(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tenantId,
        string bindingId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT binding_id, state, payload, starts_at, expires_at, review_by
            FROM host_policy_binding
            WHERE tenant_id = $tenant AND binding_id = $id;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", bindingId);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new BindingRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5))
            : null;
    }

    private static PolicyBindingRecord Record(BindingRow row, DateTimeOffset now) => new()
    {
        Binding = Deserialize(row.Payload),
        State = StateOf(row.State, ParseStamp(row.StartsAt), ParseStamp(row.ExpiresAt), now),
    };

    private static PolicyBindingState StateOf(
        string state,
        DateTimeOffset startsAt,
        DateTimeOffset expiresAt,
        DateTimeOffset now) =>
        state == "revoked"
            ? PolicyBindingState.Revoked
            : now >= expiresAt
                ? PolicyBindingState.Expired
                : now < startsAt
                    ? PolicyBindingState.Pending
                    : PolicyBindingState.Active;

    private static void InsertEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string bindingId,
        string @event,
        string actor,
        string reason,
        DateTimeOffset at)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO host_policy_binding_event
                (event_id, tenant_id, binding_id, event, actor, reason, at)
            VALUES ($id, $tenant, $binding, $event, $actor, $reason, $at);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$binding", bindingId);
        command.Parameters.AddWithValue("$event", @event);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$at", Stamp(at));
        command.ExecuteNonQuery();
    }

    private static PolicyBinding Deserialize(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<PolicyBinding>(payload, HostJson.Options)
                ?? throw new JsonException("The binding payload was empty.");
        }
        catch (JsonException ex)
        {
            throw new StorageUnavailableException("A stored policy binding could not be read.", ex);
        }
    }

    private static string Stamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
