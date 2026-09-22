using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloMail.Core;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Decisions;

/// <summary>SQLite-backed decision ledger.</summary>
/// <remarks>
/// The whole assessment is stored as JSON alongside its indexed columns. The columns exist so the
/// ledger can be queried and audited without deserialising anything; the document exists because a
/// decision is a versioned artefact whose full shape matters more than its queryability, and
/// normalising it into tables would create a migration burden every time the assessment contract
/// gains a field.
/// </remarks>
public sealed class SqliteDecisionLedger : IDecisionLedger
{
    private readonly HostDatabase _database;

    public SqliteDecisionLedger(HostDatabase database)
    {
        _database = database;
    }

    public Task RecordAsync(MailAssessment assessment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = JsonSerializer.Serialize(assessment, HostJson.Options);

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR REPLACE INTO host_decision_ledger
                    (tenant_id, assessment_id, internal_message_id, action, recorded_at, payload)
                VALUES ($tenant, $assessment, $message, $action, $recorded, $payload);
                """;

            command.Parameters.AddWithValue("$tenant", assessment.TenantId);
            command.Parameters.AddWithValue("$assessment", assessment.AssessmentId);
            command.Parameters.AddWithValue("$message", assessment.InternalMessageId);
            command.Parameters.AddWithValue("$action", assessment.Action.ToString());
            command.Parameters.AddWithValue("$recorded", assessment.AssessedAt.ToString("O"));
            command.Parameters.AddWithValue("$payload", payload);

            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The decision ledger could not be written.", ex);
        }

        return Task.CompletedTask;
    }

    public Task<MailAssessment?> FindAsync(
        string tenantId,
        string assessmentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assessmentId);
        cancellationToken.ThrowIfCancellationRequested();

        string? payload;

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();

            // Tenant is part of the WHERE clause rather than a check performed afterwards, so a
            // row belonging to another tenant is never even materialised.
            command.CommandText =
                "SELECT payload FROM host_decision_ledger WHERE tenant_id = $tenant AND assessment_id = $assessment;";
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$assessment", assessmentId);

            payload = command.ExecuteScalar() as string;
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The decision ledger could not be read.", ex);
        }

        return Task.FromResult(
            payload is null ? null : JsonSerializer.Deserialize<MailAssessment>(payload, HostJson.Options));
    }
}
