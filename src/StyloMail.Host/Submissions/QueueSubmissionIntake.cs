using Microsoft.Data.Sqlite;
using StyloMail.Host.Storage;
using StyloMail.Queue;

namespace StyloMail.Host.Submissions;

/// <summary>
/// Adapts the durable queue to <see cref="ISubmissionIntake"/>.
/// </summary>
/// <remarks>
/// Thin by design. The only thing it does beyond forwarding is translate the queue's own failure
/// signals into the single exception the routes understand, so that every route has one way to say
/// "this could not be made durable" and no route has to know which of SQLite, the spool or the
/// filesystem underneath was the one that gave up.
/// </remarks>
public sealed class QueueSubmissionIntake : ISubmissionIntake
{
    private readonly QueueStore _store;

    public QueueSubmissionIntake(QueueStore store)
    {
        _store = store;
    }

    public async Task<QueueAcceptResult> AcceptAsync(
        QueueSubmission submission,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.AcceptAsync(submission, cancellationToken).ConfigureAwait(false);
        }
        catch (SpoolUnavailableException ex)
        {
            // Disk full, an unmounted volume, a permissions change. The queue already refused to
            // accept; this is that refusal travelling upward as a temporary failure.
            throw new StorageUnavailableException(
                "The message could not be durably spooled, so it was not accepted.", ex);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException(
                "The queue metadata could not be committed, so the message was not accepted.", ex);
        }
    }

    public Task<SubmissionLookup?> FindAsync(
        string tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken)
        => _store.FindSubmissionAsync(tenantId, idempotencyKey, cancellationToken);

    public Task<QueueItem?> GetAsync(string queueId, string tenantId, CancellationToken cancellationToken)
        => _store.GetItemAsync(queueId, tenantId, cancellationToken);

    public Task<bool> ResolveQuarantineAsync(
        string queueId,
        string tenantId,
        string decidedBy,
        CancellationToken cancellationToken)
        => _store.ResolveQuarantineAsync(
            queueId,
            QuarantineResolution.Release,
            decidedBy,
            tenantId,
            cancellationToken);
}
