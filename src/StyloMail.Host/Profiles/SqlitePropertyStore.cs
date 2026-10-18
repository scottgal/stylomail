using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;
using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Profiles;

/// <summary>Whether this deployment lets a model propose entity properties.</summary>
/// <remarks>
/// <b>Off by default, and that default is the spec's</b> (§9.16): age inference is enabled only for
/// deployments whose audience is mixed or unknown. A deployment that never opts in refuses an
/// inferred write with <c>inference_not_enabled</c> rather than silently storing a value that no
/// later reader could tell was never authorised.
/// </remarks>
public sealed class PropertyOptions
{
    public const string SectionName = "StyloMail:Properties";

    public bool AllowInferredWrites { get; set; }
}

/// <summary>SQLite-backed entity properties and their history.</summary>
/// <remarks>
/// <para>
/// <b>One transaction per change.</b> A set, a confirm and a clear each read the old row, write the
/// new one and append the audit event inside one transaction, so an audit trail with a gap cannot
/// be produced by a crash between two of those steps.
/// </para>
/// <para>
/// <b>Expiry is evaluated on read and recorded once.</b> A row whose instant has passed is not
/// deleted by a timer; it is observed during a read, an idempotent <c>Expire</c> event is written,
/// and the row is removed with a delete guarded on the expiry instant that was observed. That keeps
/// "history retained" true without a background sweep, and the guard means a value written between
/// the read and the delete is never removed by the expiry of the one before it.
/// </para>
/// <para>
/// <b>Tenant is an explicit argument and part of every predicate.</b> There is no ambient tenant and
/// no default, so a cross-tenant read is not a permission question: there is nothing to name.
/// </para>
/// </remarks>
public sealed class SqlitePropertyStore : IPropertyStore
{
    private readonly HostDatabase _database;

    public SqlitePropertyStore(HostDatabase database) => _database = database;

    // ---------------------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<EffectiveProperty>> GetEffectiveAsync(
        string tenantId,
        PropertyScopeChain chain,
        IReadOnlyList<string> keys,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var key in keys)
        {
            PropertyKeys.Require(key);
        }

        if (keys.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<EffectiveProperty>>([]);
        }

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var rows = ReadChain(connection, transaction, tenantId, chain, keys);

            foreach (var expired in rows.Where(row => !row.IsLiveAt(now)).ToList())
            {
                RecordExpiry(connection, transaction, tenantId, expired, now);
                rows.Remove(expired);
            }

            transaction.Commit();

            return Task.FromResult(PropertyResolver.ResolveAll(keys, rows, now));
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The entity properties could not be read.", ex);
        }
    }

    public Task<PropertyRecord?> GetStoredAsync(
        string tenantId,
        PropertyScope scope,
        string key,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        _ = PropertyKeys.Require(key);

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var row = ReadRow(connection, transaction, tenantId, scope, key);

            if (row is not null && !row.IsLiveAt(now))
            {
                RecordExpiry(connection, transaction, tenantId, row, now);
                row = null;
            }

            transaction.Commit();

            return Task.FromResult(row);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The entity property could not be read.", ex);
        }
    }

    public Task<IReadOnlyList<PropertyAuditEvent>> GetHistoryAsync(
        string tenantId,
        PropertyScope scope,
        string key,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        _ = PropertyKeys.Require(key);

        var bounded = Math.Clamp(limit, 1, 200);

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT event_id, action, actor, recorded_at, old_value, new_value, old_source,
                       new_source, old_confirmed_by, new_confirmed_by, reason
                FROM host_entity_property_event
                WHERE tenant_id = $tenant AND scope_kind = $kind AND scope_id = $id
                  AND property_key = $key
                ORDER BY recorded_at DESC, event_id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$kind", scope.Kind.ToString());
            command.Parameters.AddWithValue("$id", scope.Id);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$limit", bounded);

            var events = new List<PropertyAuditEvent>();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // A row whose action or source no longer parses is skipped rather than allowed to
                // break the whole history. The property read path has the same policy: a corrupt
                // row must not make an unrelated question unanswerable.
                if (!Enum.TryParse<PropertyAuditAction>(reader.GetString(1), out var action))
                {
                    continue;
                }

                events.Add(new PropertyAuditEvent
                {
                    EventId = reader.GetString(0),
                    Scope = scope,
                    Key = key,
                    Action = action,
                    Actor = reader.GetString(2),
                    RecordedAt = ParseStamp(reader.GetString(3)),
                    OldValue = reader.IsDBNull(4) ? null : reader.GetString(4),
                    NewValue = reader.IsDBNull(5) ? null : reader.GetString(5),
                    OldSource = reader.IsDBNull(6) ? null : ParseSource(reader.GetString(6)),
                    NewSource = reader.IsDBNull(7) ? null : ParseSource(reader.GetString(7)),
                    OldConfirmedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
                    NewConfirmedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Reason = reader.IsDBNull(10) ? null : reader.GetString(10),
                });
            }

            return Task.FromResult<IReadOnlyList<PropertyAuditEvent>>(events);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The property history could not be read.", ex);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Writes
    // ---------------------------------------------------------------------------------------------

    public Task<PropertyRecord> SetAsync(
        string tenantId,
        PropertyWrite write,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        var definition = PropertyKeys.Require(write.Key);
        if (!PropertyKeys.TryNormalise(definition, write.Value, out var normalised, out var reason))
        {
            // The endpoint checks first and answers with a named code. Reaching here means a writer
            // other than the endpoint skipped the rule, so refuse loudly rather than store a value
            // the read path would then have to treat as ineligible.
            throw new ArgumentException(reason, nameof(write));
        }

        var record = new PropertyRecord
        {
            Scope = write.Scope,
            Key = write.Key,
            Value = normalised,
            Source = write.Source,
            Confidence = write.Confidence,
            Model = write.Model,
            EvidenceRefs = [.. write.EvidenceRefs],
            Coverage = write.Coverage,
            SetBy = write.Actor,
            SetAt = now,
            ExpiresAt = write.ExpiresAt,
            ConfirmedBy = null,
            ConfirmedAt = null,
        };

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            // Expire first so the row this write replaces is recorded as expired rather than as a
            // value that silently vanished. If it had already expired, the old value for *this*
            // event is nothing: the effective binding was already unset.
            var previous = ReadRow(connection, transaction, tenantId, write.Scope, write.Key);
            if (previous is not null && !previous.IsLiveAt(now))
            {
                RecordExpiry(connection, transaction, tenantId, previous, now);
                previous = null;
            }

            Upsert(connection, transaction, tenantId, record);
            InsertEvent(
                connection,
                transaction,
                tenantId,
                record.Scope,
                record.Key,
                PropertyAuditAction.Set,
                actor: write.Actor,
                recordedAt: now,
                previous,
                record,
                write.Reason);

            transaction.Commit();
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The entity property could not be stored.", ex);
        }

        return Task.FromResult(record);
    }

    public Task<PropertyMutationResult> ConfirmAsync(
        string tenantId,
        PropertyScope scope,
        string key,
        string actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();

        var definition = PropertyKeys.Require(key);

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var row = ReadRow(connection, transaction, tenantId, scope, key);
            if (row is not null && !row.IsLiveAt(now))
            {
                RecordExpiry(connection, transaction, tenantId, row, now);
                row = null;
            }

            if (row is null)
            {
                transaction.Commit();

                // Nothing at this exact scope. That is a fact to report, not a value to invent at a
                // different scope: a confirm that copied an inherited value downward would create a
                // binding the operator never made.
                return Task.FromResult(new PropertyMutationResult
                {
                    Applied = false,
                    Stored = null,
                    Effective = EffectiveProperty.Unknown(key),
                });
            }

            // A second confirm by the same or another human does not move the instant of the first
            // confirmation. The first is when a human took responsibility; recording it again would
            // overwrite that with a later click.
            if (!string.IsNullOrWhiteSpace(row.ConfirmedBy))
            {
                transaction.Commit();

                return Task.FromResult(new PropertyMutationResult
                {
                    Applied = true,
                    Stored = row,
                    Effective = PropertyResolver.Resolve(definition, [row], now),
                });
            }

            var confirmed = row with { ConfirmedBy = actor, ConfirmedAt = now };

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE host_entity_property
                    SET confirmed_by = $by, confirmed_at = $at
                    WHERE tenant_id = $tenant AND scope_kind = $kind AND scope_id = $id
                      AND property_key = $key;
                    """;
                update.Parameters.AddWithValue("$by", actor);
                update.Parameters.AddWithValue("$at", Stamp(now));
                update.Parameters.AddWithValue("$tenant", tenantId);
                update.Parameters.AddWithValue("$kind", scope.Kind.ToString());
                update.Parameters.AddWithValue("$id", scope.Id);
                update.Parameters.AddWithValue("$key", key);
                update.ExecuteNonQuery();
            }

            InsertEvent(
                connection,
                transaction,
                tenantId,
                scope,
                key,
                PropertyAuditAction.Confirm,
                actor,
                now,
                row,
                confirmed,
                reason);

            transaction.Commit();

            return Task.FromResult(new PropertyMutationResult
            {
                Applied = true,
                Stored = confirmed,
                Effective = PropertyResolver.Resolve(definition, [confirmed], now),
            });
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The entity property could not be confirmed.", ex);
        }
    }

    public Task<PropertyMutationResult> ClearAsync(
        string tenantId,
        PropertyScope scope,
        string key,
        string actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();

        var definition = PropertyKeys.Require(key);

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var row = ReadRow(connection, transaction, tenantId, scope, key);
            if (row is not null && !row.IsLiveAt(now))
            {
                RecordExpiry(connection, transaction, tenantId, row, now);
                row = null;
            }

            if (row is null)
            {
                transaction.Commit();

                return Task.FromResult(new PropertyMutationResult
                {
                    Applied = false,
                    Stored = null,
                    Effective = EffectiveProperty.Unknown(key),
                });
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText =
                    """
                    DELETE FROM host_entity_property
                    WHERE tenant_id = $tenant AND scope_kind = $kind AND scope_id = $id
                      AND property_key = $key;
                    """;
                delete.Parameters.AddWithValue("$tenant", tenantId);
                delete.Parameters.AddWithValue("$kind", scope.Kind.ToString());
                delete.Parameters.AddWithValue("$id", scope.Id);
                delete.Parameters.AddWithValue("$key", key);
                delete.ExecuteNonQuery();
            }

            InsertEvent(
                connection,
                transaction,
                tenantId,
                scope,
                key,
                PropertyAuditAction.Clear,
                actor,
                now,
                row,
                null,
                reason);

            transaction.Commit();

            return Task.FromResult(new PropertyMutationResult
            {
                Applied = true,
                Stored = null,

                // The addressed scope is now empty. Whether a wider scope takes over is the caller's
                // question and is answered by the next effective read over its full chain; this
                // result deliberately does not pretend to know the chain.
                Effective = PropertyResolver.Resolve(definition, [], now),
            });
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The entity property could not be cleared.", ex);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Row helpers
    // ---------------------------------------------------------------------------------------------

    private const string SelectColumns =
        """
        SELECT scope_kind, scope_id, property_key, value, source, confidence, model_id,
               model_version, prompt_contract, spans, evidence_refs, set_by, set_at, expires_at,
               confirmed_by, confirmed_at,
               coverage_analysed, coverage_total, coverage_window_start, coverage_window_end,
               coverage_unavailable_reason
        FROM host_entity_property
        """;

    private static List<PropertyRecord> ReadChain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        PropertyScopeChain chain,
        IReadOnlyList<string> keys)
    {
        var rows = new List<PropertyRecord>();

        foreach (var scope in chain.Scopes)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"{SelectColumns} WHERE tenant_id = $tenant AND scope_kind = $kind AND scope_id = $id "
                + "AND property_key IN (" + string.Join(", ", keys.Select((_, index) => $"$k{index}")) + ");";
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$kind", scope.Kind.ToString());
            command.Parameters.AddWithValue("$id", scope.Id);
            for (var index = 0; index < keys.Count; index++)
            {
                command.Parameters.AddWithValue($"$k{index}", keys[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Read(reader) is { } record)
                {
                    rows.Add(record);
                }
            }
        }

        return rows;
    }

    private static PropertyRecord? ReadRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        PropertyScope scope,
        string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"{SelectColumns} WHERE tenant_id = $tenant AND scope_kind = $kind AND scope_id = $id "
            + "AND property_key = $key;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$kind", scope.Kind.ToString());
        command.Parameters.AddWithValue("$id", scope.Id);
        command.Parameters.AddWithValue("$key", key);

        using var reader = command.ExecuteReader();

        return reader.Read() ? Read(reader) : null;
    }

    private static void Upsert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        PropertyRecord record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO host_entity_property
                (tenant_id, scope_kind, scope_id, property_key, value, source, confidence,
                 model_id, model_version, prompt_contract, spans, evidence_refs,
                 coverage_analysed, coverage_total, coverage_window_start, coverage_window_end,
                 coverage_unavailable_reason, set_by, set_at, expires_at, confirmed_by, confirmed_at)
            VALUES
                ($tenant, $kind, $id, $key, $value, $source, $confidence,
                 $model, $version, $contract, $spans, $refs,
                 $cAnalysed, $cTotal, $cStart, $cEnd, $cReason, $by, $at,
                 $expires, $confirmedBy, $confirmedAt)
            ON CONFLICT (tenant_id, scope_kind, scope_id, property_key) DO UPDATE SET
                value           = $value,
                source          = $source,
                confidence      = $confidence,
                model_id        = $model,
                model_version   = $version,
                prompt_contract = $contract,
                spans           = $spans,
                evidence_refs   = $refs,
                coverage_analysed          = $cAnalysed,
                coverage_total             = $cTotal,
                coverage_window_start      = $cStart,
                coverage_window_end        = $cEnd,
                coverage_unavailable_reason = $cReason,
                set_by          = $by,
                set_at          = $at,
                expires_at      = $expires,
                confirmed_by    = $confirmedBy,
                confirmed_at    = $confirmedAt;
            """;

        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$kind", record.Scope.Kind.ToString());
        command.Parameters.AddWithValue("$id", record.Scope.Id);
        command.Parameters.AddWithValue("$key", record.Key);
        command.Parameters.AddWithValue("$value", record.Value);
        command.Parameters.AddWithValue("$source", record.Source.ToString());
        Bind(command, "$confidence", record.Confidence);
        Bind(command, "$model", record.Model?.Id);
        Bind(command, "$version", record.Model?.Version);
        Bind(command, "$contract", record.Model?.PromptContract);
        Bind(command, "$spans", record.Model is null ? null : JsonSerializer.Serialize(record.Model.Spans));

        // Named enums, like everything else the host stores. A numeric retention or access outcome
        // would change meaning the day a member is inserted, and this JSON is read years later.
        Bind(command, "$refs", JsonSerializer.Serialize(record.EvidenceRefs, HostJson.Options));
        Bind(command, "$cAnalysed", record.Coverage?.Analysed);
        Bind(command, "$cTotal", record.Coverage?.Total);
        Bind(command, "$cStart", record.Coverage?.WindowStart is { } start ? Stamp(start) : null);
        Bind(command, "$cEnd", record.Coverage?.WindowEnd is { } end ? Stamp(end) : null);
        Bind(command, "$cReason", record.Coverage?.UnavailableReason);
        command.Parameters.AddWithValue("$by", record.SetBy);
        command.Parameters.AddWithValue("$at", Stamp(record.SetAt));
        Bind(command, "$expires", record.ExpiresAt is { } expires ? Stamp(expires) : null);
        Bind(command, "$confirmedBy", record.ConfirmedBy);
        Bind(command, "$confirmedAt", record.ConfirmedAt is { } confirmed ? Stamp(confirmed) : null);

        command.ExecuteNonQuery();
    }

    private static void RecordExpiry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        PropertyRecord row,
        DateTimeOffset now)
    {
        var expiresAt = row.ExpiresAt!.Value;

        // Deterministic, so two readers that observe the same expired row write one event between
        // them rather than one each. The set instant is part of it so that a later row which happens
        // to share an expiry instant is still its own expiry.
        var eventId = string.Create(
            CultureInfo.InvariantCulture,
            $"pexp-{row.Scope.Kind}-{row.Scope.Id}-{row.Key}-{expiresAt.UtcTicks}-{row.SetAt.UtcTicks}");

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO host_entity_property_event
                    (tenant_id, event_id, scope_kind, scope_id, property_key, action, actor,
                     recorded_at, old_value, new_value, old_source, new_source,
                     old_confirmed_by, new_confirmed_by, reason)
                VALUES
                    ($tenant, $event, $kind, $id, $key, 'Expire', 'system:expiry',
                     $at, $oldValue, NULL, $oldSource, NULL, $oldConfirmedBy, NULL, $reason);
                """;
            insert.Parameters.AddWithValue("$tenant", tenantId);
            insert.Parameters.AddWithValue("$event", eventId);
            insert.Parameters.AddWithValue("$kind", row.Scope.Kind.ToString());
            insert.Parameters.AddWithValue("$id", row.Scope.Id);
            insert.Parameters.AddWithValue("$key", row.Key);
            insert.Parameters.AddWithValue("$at", Stamp(now));
            insert.Parameters.AddWithValue("$oldValue", row.Value);
            insert.Parameters.AddWithValue("$oldSource", row.Source.ToString());
            Bind(insert, "$oldConfirmedBy", row.ConfirmedBy);
            insert.Parameters.AddWithValue(
                "$reason",
                "The recorded expiry passed; the value is no longer in force.");
            insert.ExecuteNonQuery();
        }

        // Guarded on both the instant and the value that were observed. A write that replaced the
        // row between the read and this delete has a different instant or value, so it survives:
        // an expiry must never remove a value it did not observe.
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText =
            """
            DELETE FROM host_entity_property
            WHERE tenant_id = $tenant AND scope_kind = $kind AND scope_id = $id
              AND property_key = $key AND expires_at = $expires AND value = $value;
            """;
        delete.Parameters.AddWithValue("$tenant", tenantId);
        delete.Parameters.AddWithValue("$kind", row.Scope.Kind.ToString());
        delete.Parameters.AddWithValue("$id", row.Scope.Id);
        delete.Parameters.AddWithValue("$key", row.Key);
        delete.Parameters.AddWithValue("$expires", Stamp(expiresAt));
        delete.Parameters.AddWithValue("$value", row.Value);
        delete.ExecuteNonQuery();
    }

    private static void InsertEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        PropertyScope scope,
        string key,
        PropertyAuditAction action,
        string actor,
        DateTimeOffset recordedAt,
        PropertyRecord? previous,
        PropertyRecord? next,
        string? reason)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO host_entity_property_event
                (tenant_id, event_id, scope_kind, scope_id, property_key, action, actor,
                 recorded_at, old_value, new_value, old_source, new_source,
                 old_confirmed_by, new_confirmed_by, reason)
            VALUES
                ($tenant, $event, $kind, $id, $key, $action, $actor, $at,
                 $oldValue, $newValue, $oldSource, $newSource,
                 $oldConfirmedBy, $newConfirmedBy, $reason);
            """;

        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$event", $"pevt-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("$kind", scope.Kind.ToString());
        command.Parameters.AddWithValue("$id", scope.Id);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$action", action.ToString());
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$at", Stamp(recordedAt));
        Bind(command, "$oldValue", previous?.Value);
        Bind(command, "$newValue", next?.Value);
        Bind(command, "$oldSource", previous?.Source.ToString());
        Bind(command, "$newSource", next?.Source.ToString());
        Bind(command, "$oldConfirmedBy", previous?.ConfirmedBy);
        Bind(command, "$newConfirmedBy", next?.ConfirmedBy);
        Bind(command, "$reason", reason);

        command.ExecuteNonQuery();
    }

    private static PropertyRecord? Read(SqliteDataReader reader)
    {
        if (!Enum.TryParse<PropertyScopeKind>(reader.GetString(0), out var kind))
        {
            return null;
        }

        if (!Enum.TryParse<PropertySource>(reader.GetString(4), out var source))
        {
            return null;
        }

        var modelId = reader.IsDBNull(6) ? null : reader.GetString(6);

        return new PropertyRecord
        {
            Scope = new PropertyScope(kind, reader.GetString(1)),
            Key = reader.GetString(2),
            Value = reader.GetString(3),
            Source = source,
            Confidence = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            Model = modelId is null
                ? null
                : new ModelContribution
                {
                    Id = modelId,
                    Version = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    PromptContract = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    Spans = ParseStrings(reader.IsDBNull(9) ? null : reader.GetString(9)),
                },
            EvidenceRefs = ParseEvidenceRefs(reader.IsDBNull(10) ? null : reader.GetString(10)),
            Coverage = ReadCoverage(reader),
            SetBy = reader.GetString(11),
            SetAt = ParseStamp(reader.GetString(12)),
            ExpiresAt = reader.IsDBNull(13) ? null : ParseStamp(reader.GetString(13)),
            ConfirmedBy = reader.IsDBNull(14) ? null : reader.GetString(14),
            ConfirmedAt = reader.IsDBNull(15) ? null : ParseStamp(reader.GetString(15)),
        };
    }

    private static PropertySource? ParseSource(string value)
        => Enum.TryParse<PropertySource>(value, out var source) ? source : null;

    private static IReadOnlyList<string> ParseStrings(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            // A span list that cannot be parsed is not evidence. Reporting an empty list is honest:
            // inventing a reference, or failing the whole read, would be worse than showing none.
            return [];
        }
    }

    private static IReadOnlyList<EvidenceReference> ParseEvidenceRefs(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            var references = JsonSerializer.Deserialize<List<EvidenceReference>>(json, HostJson.Options);
            if (references is not null)
            {
                return references;
            }
        }
        catch (JsonException)
        {
            // Fall through to the legacy shape below. A row written before metadata was part of the
            // contract holds plain strings, and refusing to read it would hide the property too.
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<List<string>>(json);
            return legacy is null
                ? []
                : [.. legacy
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => new EvidenceReference { Id = id })];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static PropertyCoverage? ReadCoverage(SqliteDataReader reader)
    {
        var analysed = reader.IsDBNull(16) ? (int?)null : reader.GetInt32(16);
        var total = reader.IsDBNull(17) ? (int?)null : reader.GetInt32(17);
        var start = reader.IsDBNull(18) ? (DateTimeOffset?)null : ParseStamp(reader.GetString(18));
        var end = reader.IsDBNull(19) ? (DateTimeOffset?)null : ParseStamp(reader.GetString(19));
        var reason = reader.IsDBNull(20) ? null : reader.GetString(20);

        if (analysed is null && total is null && start is null && end is null && reason is null)
        {
            return null;
        }

        return new PropertyCoverage
        {
            Analysed = analysed,
            Total = total,
            WindowStart = start,
            WindowEnd = end,
            UnavailableReason = reason,
        };
    }

    private static string Stamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    private static void Bind(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
