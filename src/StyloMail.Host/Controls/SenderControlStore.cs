using Microsoft.Data.Sqlite;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Controls;

/// <summary>Whether one principal's outbound delivery is currently paused, and by whom.</summary>
public sealed record SenderControlState
{
    public required string PrincipalId { get; init; }

    public required bool Paused { get; init; }

    public DateTimeOffset? PausedAt { get; init; }

    /// <summary>Why the pause was applied. Retained after a resume, so the intervention stays auditable.</summary>
    public string? Reason { get; init; }

    public DateTimeOffset? ResumedAt { get; init; }

    public string? ResumedBy { get; init; }

    /// <summary>Why the pause was lifted.</summary>
    public string? ResumeReason { get; init; }

    /// <summary>Whoever acted last, the pause or the resume.</summary>
    public required string UpdatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Operator controls over a sending principal.
/// </summary>
/// <remarks>
/// Scoped by tenant and principal together. A principal identifier is only unique within its own
/// tenant, two tenants may legitimately use the same one, so an unscoped lookup would let one
/// tenant's control-plane action reach the other's senders.
/// </remarks>
public interface ISenderControlStore
{
    Task PauseAsync(
        string tenantId,
        string principalId,
        string reason,
        string updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lifts a pause. Idempotent, and does not erase the pause it lifted.
    /// </summary>
    Task ResumeAsync(
        string tenantId,
        string principalId,
        string reason,
        string updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<SenderControlState?> GetAsync(
        string tenantId,
        string principalId,
        CancellationToken cancellationToken);
}

/// <summary>SQLite-backed sender controls.</summary>
/// <remarks>
/// Separate from queue state on purpose. Pausing an account is a control-plane fact and resuming
/// it must not resurrect, discard or reorder any mail, which it would if the pause were
/// implemented as a queue state rather than alongside it.
/// </remarks>
public sealed class SqliteSenderControlStore : ISenderControlStore
{
    private readonly HostDatabase _database;

    public SqliteSenderControlStore(HostDatabase database)
    {
        _database = database;
    }

    public Task PauseAsync(
        string tenantId,
        string principalId,
        string reason,
        string updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();

            // Idempotent by construction: pausing an already-paused sender updates the reason and
            // the audit stamp rather than failing. A control action a client cannot safely retry
            // is one that gets made twice anyway, with worse bookkeeping.
            command.CommandText =
                """
                INSERT INTO sender_control (tenant_id, principal_id, paused, paused_at, reason, updated_by, updated_at)
                VALUES ($tenant, $principal, 1, $pausedAt, $reason, $by, $at)
                ON CONFLICT (tenant_id, principal_id) DO UPDATE SET
                    paused     = 1,
                    paused_at  = COALESCE(sender_control.paused_at, $pausedAt),
                    reason     = $reason,
                    updated_by = $by,
                    updated_at = $at;
                """;

            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$principal", principalId);
            command.Parameters.AddWithValue("$pausedAt", now.ToString("O"));
            command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$by", updatedBy);
            command.Parameters.AddWithValue("$at", now.ToString("O"));

            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The sender control could not be written.", ex);
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(
        string tenantId,
        string principalId,
        string reason,
        string updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();

            // paused_at and reason are deliberately absent from the UPDATE: lifting a pause must
            // not erase the record that one was imposed. Resuming a sender who was never paused
            // simply writes a not-paused row, which is the same state they were already in.
            command.CommandText =
                """
                INSERT INTO sender_control
                    (tenant_id, principal_id, paused, resumed_at, resumed_by, resume_reason, updated_by, updated_at)
                VALUES ($tenant, $principal, 0, $at, $by, $reason, $by, $at)
                ON CONFLICT (tenant_id, principal_id) DO UPDATE SET
                    paused        = 0,
                    resumed_at    = $at,
                    resumed_by    = $by,
                    resume_reason = $reason,
                    updated_by    = $by,
                    updated_at    = $at;
                """;

            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$principal", principalId);
            command.Parameters.AddWithValue("$at", now.ToString("O"));
            command.Parameters.AddWithValue("$by", updatedBy);
            command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);

            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The sender control could not be written.", ex);
        }

        return Task.CompletedTask;
    }

    public Task<SenderControlState?> GetAsync(
        string tenantId,
        string principalId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT principal_id, paused, paused_at, reason, updated_by, updated_at,
                       resumed_at, resumed_by, resume_reason
                FROM sender_control
                WHERE tenant_id = $tenant AND principal_id = $principal;
                """;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$principal", principalId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return Task.FromResult<SenderControlState?>(null);
            }

            return Task.FromResult<SenderControlState?>(new SenderControlState
            {
                PrincipalId = reader.GetString(0),
                Paused = reader.GetInt32(1) != 0,
                PausedAt = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
                Reason = reader.IsDBNull(3) ? null : reader.GetString(3),
                UpdatedBy = reader.GetString(4),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(5)),
                ResumedAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                ResumedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
                ResumeReason = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The sender control could not be read.", ex);
        }
    }
}
