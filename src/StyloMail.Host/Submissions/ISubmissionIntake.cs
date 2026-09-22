using StyloMail.Queue;

namespace StyloMail.Host.Submissions;

/// <summary>
/// What the submission routes need from the durable queue, and nothing more.
/// </summary>
/// <remarks>
/// The port speaks the Queue project's own contracts rather than a parallel set of host DTOs. The
/// queue owns what a queued message <em>is</em>, <see cref="QueueSubmission"/>,
/// <see cref="QueueAcceptResult"/>, <see cref="QueueItem"/>, and a second vocabulary here would be
/// a second definition of acceptance, which is the one concept in this system that must have
/// exactly one.
///
/// <para>
/// It exists as an interface so the routes can be exercised against a storage failure that is
/// injected rather than staged. Manufacturing a genuinely unwritable spool in a test is
/// environment-dependent, it depends on the user the suite runs as, whereas "the queue could not
/// accept this" is a case the host must handle regardless of why.
/// </para>
/// </remarks>
public interface ISubmissionIntake
{
    /// <summary>
    /// Offers a message for durable acceptance.
    /// </summary>
    /// <exception cref="Storage.StorageUnavailableException">
    /// Durable storage could not be written. Callers must answer with a temporary failure.
    /// </exception>
    Task<QueueAcceptResult> AcceptAsync(QueueSubmission submission, CancellationToken cancellationToken);

    /// <summary>
    /// Finds an existing submission by tenant-scoped idempotency key, or null.
    /// </summary>
    /// <remarks>
    /// Used to answer a replay without spending a parse, an assessment or a provider call. The
    /// queue would also deduplicate on acceptance, so this is an optimisation, but it is the
    /// difference between a retry costing nothing and a retry costing a semantic classification.
    /// </remarks>
    Task<SubmissionLookup?> FindAsync(string tenantId, string idempotencyKey, CancellationToken cancellationToken);

    Task<QueueItem?> GetAsync(string queueId, string tenantId, CancellationToken cancellationToken);

    /// <summary>Applies a reviewer's decision to a quarantined message, recording who made it.</summary>
    Task<bool> ResolveQuarantineAsync(
        string queueId,
        string tenantId,
        string decidedBy,
        CancellationToken cancellationToken);
}
