using System.Net.Security;
using StyloMail.Queue;
using StyloMail.Transport.Smtp;

namespace StyloMail.Transport.Delivery;

/// <summary>
/// Delivers accepted mail to a configured upstream, one recipient at a time.
/// </summary>
/// <remarks>
/// <para>
/// This is the implementation of the port the queue's delivery worker calls, and the only place in
/// the system that opens an outbound SMTP connection. It speaks the queue's own contracts —
/// <see cref="DeliveryRequest"/> in, <see cref="DeliveryPortResult"/> out, carrying
/// <see cref="RecipientDeliveryResult"/> — rather than a transport-local vocabulary. There is no
/// mapping layer to drift: the types the worker reads are the types this produces.
/// </para>
/// <para>
/// Four properties the queue depends on and cannot check for itself:
/// </para>
/// <list type="number">
/// <item><b>Every recipient gets exactly one outcome.</b> A message sent to five people where two
/// are refused must come back as five results, or the queue cannot retry only the ones still owed
/// an attempt.</item>
/// <item><b>Ambiguity is reported, not resolved.</b> A connection lost after the end-of-data
/// terminator returns <see cref="DeliveryAttemptOutcome.InDoubt"/> rather than a failure, because
/// the upstream may have accepted the message.</item>
/// <item><b>Exceptions do not escape for ordinary delivery failures.</b> A thrown exception says
/// nothing about which recipients were tried, which would leave the queue guessing about a message
/// that may already be delivered. <b>Nothing propagates for a cancellation either</b> — not even
/// the caller's own. A cancelled attempt is reported per recipient like any other outcome: the ones
/// not reached are temporary failures, and one cut off after the end-of-data terminator is
/// in-doubt. An exception here would discard exactly the per-recipient information the queue needs,
/// and turn a precise outcome into a lease expiry recorded much later.</item>
/// <item><b>The message's lifetime is respected.</b> <see cref="DeliveryRequest.ExpiresAt"/> bounds
/// the attempt, so work does not consume the budget of a message that is about to be given up on —
/// and a deadline that lands <em>after</em> the terminator still reports in-doubt, not "cancelled".</item>
/// </list>
/// </remarks>
public sealed class SmtpDeliveryPort : IDeliveryPort, IAsyncDisposable
{
    private readonly SmtpUpstream _upstream;
    private readonly SmtpBounds _bounds;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minimumAttemptBudget;
    private readonly RemoteCertificateValidationCallback? _certificateValidation;
    private readonly Action<string>? _transcriptSink;
    private readonly SemaphoreSlim _connectionLimit;
    private bool _disposed;

    /// <param name="upstream">The configured submission target. Exactly one; there is no routing table.</param>
    /// <param name="timeProvider">
    /// Source of "now" for the message-lifetime budget. Injected here rather than taken from the
    /// request: a clock in the request record would let the caller's clock govern this component's
    /// own timeouts, and timeouts are this component's policy.
    /// </param>
    /// <param name="bounds">Hard limits. Defaults are the production values.</param>
    /// <param name="minimumAttemptBudget">
    /// How much of a message's remaining lifetime is worth starting an attempt for. Below this, the
    /// attempt is failed fast instead of burning the budget of a message that will be given up on.
    /// </param>
    /// <param name="transcriptSink">
    /// Optional receiver for the redacted SMTP conversation, for the decision ledger. Off by
    /// default: the port result carries a concise diagnostic, and a full transcript is only worth
    /// materialising when something is there to store it. Any exception it throws is swallowed —
    /// an audit sink must never be able to fail a delivery.
    /// </param>
    /// <param name="certificateValidation">
    /// <b>Test only.</b> Production must leave this null so the platform trust store decides which
    /// upstreams are who they claim to be.
    /// </param>
    public SmtpDeliveryPort(
        SmtpUpstream upstream,
        TimeProvider? timeProvider = null,
        SmtpBounds? bounds = null,
        TimeSpan? minimumAttemptBudget = null,
        Action<string>? transcriptSink = null,
        RemoteCertificateValidationCallback? certificateValidation = null)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        upstream.Validate();

        _upstream = upstream;
        _bounds = bounds ?? new SmtpBounds();
        _bounds.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _minimumAttemptBudget = minimumAttemptBudget ?? TimeSpan.FromSeconds(5);
        _transcriptSink = transcriptSink;
        _certificateValidation = certificateValidation;

        if (_minimumAttemptBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumAttemptBudget), _minimumAttemptBudget, "Must be positive.");
        }

        // Bounds concurrent outbound connections. Exceeding it waits rather than failing, so a burst
        // queues instead of erroring. Safe to share across concurrent calls: it is the only mutable
        // state on this object, and everything else is per-call.
        _connectionLimit = new SemaphoreSlim(_bounds.MaxConcurrentConnections, _bounds.MaxConcurrentConnections);
    }

    /// <inheritdoc />
    public async ValueTask<DeliveryPortResult> DeliverAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (request.Recipients.Count == 0)
        {
            return new DeliveryPortResult { Recipients = [] };
        }

        if (request.Recipients.Count > _bounds.MaxRecipients)
        {
            // Refused as a whole rather than truncated. Silently delivering to the first N and
            // reporting nothing about the rest is the multi-recipient failure the spec forbids, and
            // it would look like success.
            return new DeliveryPortResult
            {
                Recipients = request.Recipients
                    .Select(r => Result(
                        r,
                        DeliveryAttemptOutcome.PermanentFailure,
                        $"The delivery request names {request.Recipients.Count} recipients, over the " +
                        $"configured limit of {_bounds.MaxRecipients}. Refused as a whole rather than " +
                        "delivered in part."))
                    .ToList(),
                Detail = "Recipient count exceeded the transport's bound.",
            };
        }

        var budget = RemainingBudget(request);

        // No deadline means no clamping — not "expired". A message without an explicit lifetime is
        // bounded by the per-operation timeouts, which is what those bounds are for.
        if (budget is { } remaining)
        {
            if (remaining <= TimeSpan.Zero)
            {
                return AllFor(request, DeliveryAttemptOutcome.TemporaryFailure,
                    "The message's lifetime had already elapsed, so no attempt was made.");
            }

            if (remaining < _minimumAttemptBudget)
            {
                return AllFor(request, DeliveryAttemptOutcome.TemporaryFailure,
                    $"The message has {remaining.TotalSeconds:F1}s of lifetime left, below the " +
                    $"{_minimumAttemptBudget.TotalSeconds:F1}s needed to attempt delivery.");
            }
        }

        // The attempt is bounded by the message's real remaining lifetime, not by a fixed guess, so
        // it cannot run on past a deadline the queue is about to enforce anyway.
        using var budgetSource = budget is { } b
            ? new CancellationTokenSource(b, _timeProvider)
            : null;

        using var linked = budgetSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetSource.Token);

        var attemptToken = linked?.Token ?? cancellationToken;

        try
        {
            await _connectionLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled while queued for a connection slot. Nothing was attempted, so nothing is
            // ambiguous — but the caller still gets one outcome per recipient rather than an
            // exception, because the port's contract is that a caller always learns what happened to
            // each recipient, and "we never got to it" is something it can act on.
            return AllFor(
                request,
                DeliveryAttemptOutcome.TemporaryFailure,
                "The attempt was cancelled before it began.");
        }

        try
        {
            return await DeliverAllAsync(request, attemptToken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionLimit.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _connectionLimit.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<DeliveryPortResult> DeliverAllAsync(
        DeliveryRequest request,
        CancellationToken attemptToken,
        CancellationToken callerToken)
    {
        var results = new List<RecipientDeliveryResult>(request.Recipients.Count);
        SmtpSession? session = null;
        var detail = (string?)null;

        // One transcript for the whole attempt, shared across any session reconnects, so the
        // operator sees a single conversation rather than fragments.
        var transcript = _transcriptSink is null ? null : new SmtpTranscript();

        try
        {
            foreach (var recipient in request.Recipients)
            {
                if (attemptToken.IsCancellationRequested)
                {
                    FillRemaining(request, results, DeliveryAttemptOutcome.TemporaryFailure,
                        "The message's remaining lifetime was exhausted before this recipient was attempted.");
                    break;
                }

                if (session is null)
                {
                    try
                    {
                        session = await OpenSessionAsync(attemptToken, transcript).ConfigureAwait(false);
                    }
                    catch (SmtpSessionException ex)
                    {
                        // Nothing about the upstream will differ between recipients, so the whole
                        // batch takes the same outcome rather than each paying for its own failed
                        // handshake. A permanent refusal is permanent — an attacker who can cause a
                        // transient one must not be able to make mail disappear.
                        FillRemaining(
                            request,
                            results,
                            ex.IsPermanent ? DeliveryAttemptOutcome.PermanentFailure : DeliveryAttemptOutcome.TemporaryFailure,
                            $"The session could not be established at the {ex.Stage} stage: {ex.Message}");
                        detail = $"Session setup failed at {ex.Stage}.";
                        break;
                    }
                }

                var outcome = await session.SendAsync(
                    request.MailFrom, recipient, request.Payload, attemptToken).ConfigureAwait(false);

                results.Add(Result(recipient, Map(outcome.Outcome), Describe(outcome)));

                if (!session.IsUsable)
                {
                    // A broken session cannot be resynchronised. The next recipient gets a fresh one,
                    // so one lost connection does not condemn the rest of the message.
                    await session.DisposeAsync().ConfigureAwait(false);
                    session = null;
                }
            }
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            // Reporting a caller cancellation rather than throwing it.
            //
            // The port's contract is one outcome per recipient, and this component always knows
            // which recipients it reached — so an exception here would discard per-recipient facts
            // the caller cannot reconstruct, and would leave a worker recording an item-level lease
            // expiry much later instead of the per-recipient truth now.
            //
            // The classification is already right without guesswork: a recipient that was not
            // reached had unambiguously nothing committed, so it is a temporary failure; and one cut
            // off *after* the end-of-data terminator never reaches this handler at all, because the
            // session has already returned in-doubt for it rather than throwing.
            FillRemaining(
                request,
                results,
                DeliveryAttemptOutcome.TemporaryFailure,
                "The attempt was cancelled before this recipient was reached.");
            detail = "Attempt cancelled by the caller.";
        }
        catch (OperationCanceledException)
        {
            // Cancelled by this port's own budget rather than by the caller, so the message's
            // lifetime ran out mid-attempt. That is an outcome, not an exception to hand to the
            // worker — and if the terminator had already been written, the session will have
            // reported in-doubt for that recipient rather than reaching here for it.
            FillRemaining(
                request,
                results,
                DeliveryAttemptOutcome.TemporaryFailure,
                "The attempt was abandoned when the message's remaining lifetime ran out.");
            detail = "Attempt abandoned at the message's lifetime boundary.";
        }
        catch (Exception ex) when (IsTransportFault(ex))
        {
            // Reached only for a fault that escaped before any per-recipient outcome was produced —
            // a refused connection, a DNS failure, a TLS error. Converting it keeps the promise the
            // port makes: one outcome per recipient, exceptions reserved for the unexpected.
            FillRemaining(request, results, DeliveryAttemptOutcome.TemporaryFailure, ex.Message);
            detail = "The connection to the upstream failed.";
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            PublishTranscript(transcript);
        }

        return new DeliveryPortResult { Recipients = results, Detail = detail };
    }

    /// <summary>
    /// Hands the transcript to the configured sink, if any.
    /// </summary>
    /// <remarks>
    /// Every failure is swallowed. An audit sink that can throw must never be able to fail a
    /// delivery: losing a diagnostic is a nuisance, losing mail because the ledger was unavailable
    /// is the failure this whole component exists to prevent.
    /// </remarks>
    private void PublishTranscript(SmtpTranscript? transcript)
    {
        if (_transcriptSink is null || transcript is null)
        {
            return;
        }

        try
        {
            _transcriptSink(transcript.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately discarded — see the remarks above.
        }
    }

    private async ValueTask<SmtpSession> OpenSessionAsync(
        CancellationToken attemptToken,
        SmtpTranscript? transcript)
    {
        var channel = await SocketSmtpChannel.ConnectAsync(
            _upstream.Host,
            _upstream.Port,
            _upstream.ImplicitTls,
            _bounds,
            _timeProvider,
            _certificateValidation,
            attemptToken).ConfigureAwait(false);

        return await SmtpSession.OpenAsync(
            channel, _upstream, _bounds, _timeProvider, transcript, attemptToken).ConfigureAwait(false);
    }

    /// <summary>How much of the message's lifetime is left, or null when it has no deadline.</summary>
    private TimeSpan? RemainingBudget(DeliveryRequest request) =>
        request.ExpiresAt is { } expiresAt ? expiresAt - _timeProvider.GetUtcNow() : null;

    /// <summary>Builds a one-outcome-per-recipient result covering everyone.</summary>
    private static DeliveryPortResult AllFor(
        DeliveryRequest request,
        DeliveryAttemptOutcome outcome,
        string detail)
    {
        var results = new List<RecipientDeliveryResult>(request.Recipients.Count);
        FillRemaining(request, results, outcome, detail);
        return new DeliveryPortResult { Recipients = results, Detail = detail };
    }

    /// <summary>Adds an outcome for every recipient not already reported.</summary>
    private static void FillRemaining(
        DeliveryRequest request,
        List<RecipientDeliveryResult> results,
        DeliveryAttemptOutcome outcome,
        string detail)
    {
        foreach (var recipient in request.Recipients)
        {
            if (results.Any(r => string.Equals(r.Recipient, recipient, StringComparison.Ordinal)))
            {
                continue;
            }

            results.Add(Result(recipient, outcome, detail));
        }
    }

    private static RecipientDeliveryResult Result(
        string recipient,
        DeliveryAttemptOutcome outcome,
        string? detail) => new()
        {
            Recipient = recipient,
            Outcome = outcome,
            Detail = detail,
        };

    /// <summary>
    /// Renders the outcome as the short diagnostic the attempt history stores.
    /// </summary>
    /// <remarks>
    /// The upstream's reply code is the only evidence of <em>why</em> a message was refused, and it
    /// is gone once the socket is. It is carried into the ledger here — bounded and sanitised, since
    /// this text lands on a security component's audit path.
    /// </remarks>
    private static string Describe(SmtpTransactionResult outcome)
    {
        var reply = outcome.Reply is null
            ? null
            : outcome.Reply.EnhancedStatusCode is { } enhanced
                ? $"{outcome.Reply.Code} {enhanced} {outcome.Reply.Text}"
                : $"{outcome.Reply.Code} {outcome.Reply.Text}";

        var text = reply ?? outcome.Detail ?? outcome.Outcome.ToString();
        return Sanitise(text);
    }

    /// <summary>Strips anything that could forge a line in a log or ledger entry, and bounds the length.</summary>
    private static string Sanitise(string text)
    {
        var cleaned = text.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return cleaned.Length <= 400 ? cleaned : string.Concat(cleaned.AsSpan(0, 400), "...");
    }

    private static bool IsTransportFault(Exception ex) =>
        ex is SmtpTimeoutException or SmtpConnectionLostException or SmtpProtocolException
            or IOException or System.Net.Sockets.SocketException;

    /// <summary>
    /// Maps a protocol outcome onto the queue's delivery vocabulary.
    /// </summary>
    /// <remarks>
    /// <c>InDoubt</c> passes through unchanged, and that is the point of the mapping existing at all.
    /// Collapsing it into <c>TemporaryFailure</c> would look defensible and be wrong: it would tell
    /// the queue "nothing was delivered, retry freely, no duplicate risk", when the whole reason it
    /// is separate is that neither half of that is known.
    ///
    /// <para>
    /// Every value produced here is in <see cref="DeliveryPortContract.ReportableOutcomes"/> — the
    /// queue refuses the rest, and its own events (an elapsed hold, a lapsed lease, a reviewer's
    /// decision) are not ours to report.
    /// </para>
    /// </remarks>
    private static DeliveryAttemptOutcome Map(SmtpTransactionOutcome outcome) => outcome switch
    {
        SmtpTransactionOutcome.Accepted => DeliveryAttemptOutcome.Delivered,
        SmtpTransactionOutcome.TransientRejection => DeliveryAttemptOutcome.TemporaryFailure,
        SmtpTransactionOutcome.PermanentRejection => DeliveryAttemptOutcome.PermanentFailure,
        SmtpTransactionOutcome.InDoubt => DeliveryAttemptOutcome.InDoubt,
        _ => DeliveryAttemptOutcome.TemporaryFailure,
    };
}
