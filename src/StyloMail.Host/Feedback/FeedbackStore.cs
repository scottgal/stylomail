using Microsoft.Data.Sqlite;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Feedback;

/// <summary>
/// How far a label reaches.
/// </summary>
/// <remarks>
/// There is deliberately no tenant-wide or global member. "This message was fine" must not be
/// expressible as "this sender is fine", because that is how a single correction becomes a
/// permanent bypass of every check the system makes — and a label that says so should be
/// impossible to write down, not merely discouraged.
/// </remarks>
public enum FeedbackScope
{
    /// <summary>Applies to one recipient of one message. The narrowest and most common case.</summary>
    Recipient = 0,

    /// <summary>Applies to the relationship between one sender identity and one recipient.</summary>
    Relationship = 1,
}

/// <summary>The correction being asserted. Provenance matters as much as the value.</summary>
public enum FeedbackLabel
{
    Legitimate = 0,
    Suspicious = 1,

    /// <summary>A scoped recipient preference: this recipient wants this kind of traffic.</summary>
    WantedPromotion = 2,

    Unwanted = 3,
}

/// <summary>One recorded label, with who asserted it and when.</summary>
public sealed record FeedbackEntry
{
    public required string FeedbackId { get; init; }

    public required string TenantId { get; init; }

    public required string DecisionId { get; init; }

    public required FeedbackScope Scope { get; init; }

    public required FeedbackLabel Label { get; init; }

    /// <summary>Required when the scope is <see cref="FeedbackScope.Recipient"/> or
    /// <see cref="FeedbackScope.Relationship"/> — a scoped label with no scope to bind to is not
    /// scoped at all.</summary>
    public string? Recipient { get; init; }

    public required string RecordedBy { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public string? Note { get; init; }
}

/// <summary>Append-only store of authorised labels.</summary>
/// <remarks>
/// Labels are never updated in place. A correction is a new label, and the history of what was
/// believed and when is exactly what makes a baseline rebuild auditable.
/// </remarks>
public interface IFeedbackStore
{
    Task RecordAsync(FeedbackEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<FeedbackEntry>> ListAsync(
        string tenantId,
        string decisionId,
        CancellationToken cancellationToken);
}

/// <summary>SQLite-backed feedback store.</summary>
public sealed class SqliteFeedbackStore : IFeedbackStore
{
    private readonly HostDatabase _database;

    public SqliteFeedbackStore(HostDatabase database)
    {
        _database = database;
    }

    public Task RecordAsync(FeedbackEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO host_feedback_record
                    (feedback_id, tenant_id, decision_id, scope, label, recipient, recorded_by, recorded_at, note)
                VALUES ($id, $tenant, $decision, $scope, $label, $recipient, $by, $at, $note);
                """;

            command.Parameters.AddWithValue("$id", entry.FeedbackId);
            command.Parameters.AddWithValue("$tenant", entry.TenantId);
            command.Parameters.AddWithValue("$decision", entry.DecisionId);
            command.Parameters.AddWithValue("$scope", entry.Scope.ToString());
            command.Parameters.AddWithValue("$label", entry.Label.ToString());
            command.Parameters.AddWithValue("$recipient", (object?)entry.Recipient ?? DBNull.Value);
            command.Parameters.AddWithValue("$by", entry.RecordedBy);
            command.Parameters.AddWithValue("$at", entry.RecordedAt.ToString("O"));
            command.Parameters.AddWithValue("$note", (object?)entry.Note ?? DBNull.Value);

            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The feedback record could not be written.", ex);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FeedbackEntry>> ListAsync(
        string tenantId,
        string decisionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        cancellationToken.ThrowIfCancellationRequested();

        var entries = new List<FeedbackEntry>();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();

            // Tenant is part of the predicate, so a cross-tenant read cannot return a row at all.
            command.CommandText =
                """
                SELECT feedback_id, tenant_id, decision_id, scope, label, recipient, recorded_by, recorded_at, note
                FROM host_feedback_record
                WHERE tenant_id = $tenant AND decision_id = $decision
                ORDER BY recorded_at;
                """;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$decision", decisionId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new FeedbackEntry
                {
                    FeedbackId = reader.GetString(0),
                    TenantId = reader.GetString(1),
                    DecisionId = reader.GetString(2),
                    Scope = Enum.Parse<FeedbackScope>(reader.GetString(3)),
                    Label = Enum.Parse<FeedbackLabel>(reader.GetString(4)),
                    Recipient = reader.IsDBNull(5) ? null : reader.GetString(5),
                    RecordedBy = reader.GetString(6),
                    RecordedAt = DateTimeOffset.Parse(reader.GetString(7)),
                    Note = reader.IsDBNull(8) ? null : reader.GetString(8),
                });
            }
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The feedback record could not be read.", ex);
        }

        return Task.FromResult<IReadOnlyList<FeedbackEntry>>(entries);
    }
}
