using StyloMail.Adaptive.Profiles;
using StyloMail.Core;
using StyloMail.Queue;

namespace StyloMail.Assessment;

/// <summary>
/// Resolves an envelope's payload reference to the original message bytes.
/// </summary>
/// <remarks>
/// The pipeline parses an analysis copy from the original bytes, and those bytes live wherever the
/// transport boundary put them — the spool for an accepted message, an in-memory buffer for a
/// caller that already holds them. This port is how the composition root reaches them without
/// knowing which.
///
/// <para>
/// <b>Returning <see langword="null"/> is a normal answer, not a failure.</b> Assessment-only calls
/// carry <see cref="PayloadReferences.Ephemeral"/> by definition and have no payload to resolve.
/// The pipeline records that deterministic extraction did not run rather than pretending it did,
/// because a message assessed without deterministic evidence is a weaker assessment and the ledger
/// has to say so.
/// </para>
/// </remarks>
public interface IRawMessageSource
{
    ValueTask<ReadOnlyMemory<byte>?> TryGetAsync(MailEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>A source that never has bytes. The default when no payload store is configured.</summary>
public sealed class NullRawMessageSource : IRawMessageSource
{
    public static NullRawMessageSource Instance { get; } = new();

    public ValueTask<ReadOnlyMemory<byte>?> TryGetAsync(MailEnvelope envelope, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
}

/// <summary>
/// Reads durably spooled payloads back through the queue's own spool.
/// </summary>
/// <remarks>
/// Reuses <see cref="SpoolStore"/> rather than reimplementing payload addressing, because the
/// spool's layout, its atomic write and its orphan sweep are one contract — a second implementation
/// that agreed with the first by coincidence would disagree eventually, and the disagreement would
/// look like a message that was accepted and then could not be read.
/// </remarks>
public sealed class SpoolRawMessageSource : IRawMessageSource
{
    /// <summary>
    /// Ceiling on bytes read back for analysis.
    /// </summary>
    /// <remarks>
    /// The MIME adapter imposes its own limits, but reading an unbounded file into memory before
    /// handing it over would let an attacker-sized payload exhaust the process regardless of what
    /// the parser would have done with it. The check happens before the read, not after.
    /// </remarks>
    public long MaxBytes { get; init; } = 64L * 1024 * 1024;

    private readonly SpoolStore _spool;

    public SpoolRawMessageSource(SpoolStore spool)
    {
        ArgumentNullException.ThrowIfNull(spool);
        _spool = spool;
    }

    public async ValueTask<ReadOnlyMemory<byte>?> TryGetAsync(
        MailEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!PayloadReferences.IsDurable(envelope.PayloadReference))
        {
            return null;
        }

        await using var stream = _spool.OpenRead(envelope.PayloadReference);
        if (stream is null)
        {
            return null;
        }

        if (stream.CanSeek && stream.Length > MaxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>An in-memory payload source, for tests and for callers that already hold the bytes.</summary>
public sealed class InMemoryRawMessageSource : IRawMessageSource
{
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _payloads = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Add(string payloadReference, ReadOnlyMemory<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadReference);

        lock (_gate)
        {
            _payloads[payloadReference] = bytes;
        }
    }

    public ValueTask<ReadOnlyMemory<byte>?> TryGetAsync(MailEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        lock (_gate)
        {
            return ValueTask.FromResult(
                _payloads.TryGetValue(envelope.PayloadReference, out var bytes)
                    ? bytes
                    : (ReadOnlyMemory<byte>?)null);
        }
    }
}

/// <summary>
/// The adaptive engine's profile state, as the pipeline needs it.
/// </summary>
/// <remarks>
/// A port rather than a direct dependency on <c>SqliteAdaptiveProfileStore</c>, so the composition
/// root is testable without a database. Both mutating operations take the store's write lock before
/// they read, which is what makes them safe under the burst a compromised account produces — and
/// the reason this port has no whole-profile save: an optimistic write would put back the retry
/// loop that a burst defeats.
/// </remarks>
public interface IAdaptiveProfileStore
{
    AdaptiveProfile? Load(ProfileKey key);

    /// <summary>
    /// Folds one observation into a profile, creating it if this is the first anyone has seen.
    /// </summary>
    /// <remarks>
    /// This is a merge under the store's own write lock, so concurrent observations of one profile
    /// queue rather than race — no retry loop, and no lost update. Synchronous because the underlying
    /// SQLite work is; wrapping it in a task would move the block to another thread, not remove it.
    /// </remarks>
    void ApplyObservation(ProfileKey key, ProfileObservation observation, DateTimeOffset at);

    /// <summary>
    /// Applies a change that needs a <em>decision</em>, under the same transaction as the read.
    /// </summary>
    /// <remarks>
    /// The general form of <see cref="ApplyObservation"/>, for changes that must consult current
    /// state — a promotion checks label provenance, freeze state and the regime candidate, and that
    /// judgement belongs to the caller rather than to persistence. The delegate keeps the decision;
    /// the store supplies the transaction and the write lock.
    ///
    /// <para>
    /// <b>The delegate holds the database's write lock while it runs.</b> In-memory work only: no
    /// I/O, no awaiting, and no calling back into the store.
    /// </para>
    ///
    /// <para>
    /// This replaces an optimistic compare-and-swap with a bounded retry, which could not survive a
    /// burst — under many concurrent writers only one wins a round, so the bounded budget loses the
    /// rest, and a promotion racing a burst is an operator intervening in the very incident that
    /// produced it.
    /// </para>
    /// </remarks>
    T Update<T>(ProfileKey key, DateTimeOffset at, Func<AdaptiveProfile, T> update);
}

/// <summary>Backs <see cref="IAdaptiveProfileStore"/> with the adaptive engine's SQLite store.</summary>
public sealed class SqliteAdaptiveProfileStoreAdapter : IAdaptiveProfileStore
{
    private readonly StyloMail.Adaptive.Storage.SqliteAdaptiveProfileStore _inner;

    public SqliteAdaptiveProfileStoreAdapter(StyloMail.Adaptive.Storage.SqliteAdaptiveProfileStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public AdaptiveProfile? Load(ProfileKey key) => _inner.Load(key);

    public void ApplyObservation(ProfileKey key, ProfileObservation observation, DateTimeOffset at) =>
        _inner.ApplyObservation(key, observation, at);

    public T Update<T>(ProfileKey key, DateTimeOffset at, Func<AdaptiveProfile, T> update) =>
        _inner.Update(key, at, update);
}

/// <summary>
/// Durable acceptance of a submission.
/// </summary>
/// <remarks>
/// A port so the acceptance step can be observed and refused in tests without a spool on disk, and
/// so the assessment-only path can be shown to reach no acceptance at all. The production adapter
/// is <c>QueueStore</c> unchanged — this is a seam, not a reimplementation.
/// </remarks>
public interface IMessageAcceptanceQueue
{
    Task<QueueAcceptResult> AcceptAsync(QueueSubmission submission, CancellationToken cancellationToken);
}

/// <summary>Backs <see cref="IMessageAcceptanceQueue"/> with the durable queue.</summary>
public sealed class QueueStoreAcceptanceQueue : IMessageAcceptanceQueue
{
    private readonly QueueStore _queue;

    public QueueStoreAcceptanceQueue(QueueStore queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
    }

    public Task<QueueAcceptResult> AcceptAsync(QueueSubmission submission, CancellationToken cancellationToken) =>
        _queue.AcceptAsync(submission, cancellationToken);
}

/// <summary>
/// Operator and tenant state policy needs, which cannot be inferred from the message.
/// </summary>
/// <remarks>
/// The kill switch, quota posture, allowlists and recipient preferences are configuration and
/// operational state. They are read through a port so that nothing in the assessment path can
/// discover them from message content — a message that could set its own kill-switch state would
/// be a message that could authorise itself.
/// </remarks>
public interface IAssessmentPolicyContextSource
{
    ValueTask<PolicyContextInput> GetAsync(
        MailAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken);
}

/// <summary>What the policy context source supplies. Mapped to <c>PolicyContext</c> by the assessor.</summary>
public sealed record PolicyContextInput
{
    public bool EmergencyKillSwitchEngaged { get; init; }

    /// <summary>
    /// True when the authenticated principal has exhausted its outbound recipient budget.
    /// </summary>
    /// <remarks>
    /// <b>Supply this from live state, and never cache it across requests.</b> The budget rolls over
    /// on a window, so an exhaustion flag held for longer than the window keeps deferring traffic
    /// after the budget has reopened on its own — the pipeline would be refusing mail on the
    /// strength of a fact that stopped being true up to an hour ago. Re-read it, or leave it false
    /// and let the pipeline discover exhaustion itself: the assessor attempts the reservation and
    /// derives the same answer from the attempt, which is always current.
    /// </remarks>
    public bool OutboundQuotaExhausted { get; init; }

    public IReadOnlyList<string> VerifiedSecurityRuleViolations { get; init; } = [];

    public bool AllowlistEntryValid { get; init; }

    public bool RecipientPrefersThisTrafficClass { get; init; }

    public bool BaselineFrozenForSuspectedCompromise { get; init; }

    /// <summary>
    /// Traffic class declared for this message, when the deployment has one.
    /// </summary>
    /// <remarks>
    /// Used for fan-out comparison, where the class is the only thing separating a newsletter
    /// sending five hundred copies from a compromised account sending five hundred lures. Absent is
    /// the safe default: without a declared expectation, fan-out is reported as unavailable rather
    /// than judged against a class nobody named.
    /// </remarks>
    public string? TrafficClassName { get; init; }

    public double ExpectedRecipientsPerSecond { get; init; }

    public double NoveltyTolerance { get; init; } = 3.0;

    public int MinimumFanOutSupport { get; init; } = 5;
}

/// <summary>Supplies nothing. The default until an operator wires real state in.</summary>
/// <remarks>
/// Every field defaults to the conservative value: no kill switch, no quota exhaustion, no verified
/// violations, no allowlist, no preference. The exception is the traffic class, which is absent
/// rather than assumed — an unnamed class makes fan-out evidence unavailable instead of silently
/// judging every sender against a default expectation that belongs to nobody.
/// </remarks>
public sealed class StaticPolicyContextSource : IAssessmentPolicyContextSource
{
    public static StaticPolicyContextSource Instance { get; } = new();

    public ValueTask<PolicyContextInput> GetAsync(
        MailAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PolicyContextInput());
}
