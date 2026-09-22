using Microsoft.Extensions.Options;
using StyloMail.Chat;
using StyloMail.Chat.Slack;
using StyloMail.Core;
using StyloMail.Host.Decisions;
using StyloMail.Host.Hosting;

namespace StyloMail.Host.Chat;

/// <summary>
/// Assesses the chat events waiting in the intake, off the request path.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the other half of the acknowledgement.</b> The endpoint answers the platform only after
/// the event is stored, which makes the answer true; this is what makes it honoured. An intake that
/// is never drained is a very careful way of losing messages.
/// </para>
/// <para>
/// <b>The stored payload is re-read rather than a parsed message being stored.</b> The verified bytes
/// are the authoritative record of what the platform sent, and reading them again here means a change
/// to the reader applies to events already waiting rather than only to new ones.
/// </para>
/// </remarks>
public sealed class ChatIntakeDrain : BackgroundService
{
    /// <summary>How many events one pass takes. Bounded so a burst cannot hold the whole intake in memory.</summary>
    private const int BatchSize = 64;

    /// <summary>How long to wait before looking again when the intake is empty.</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long an assessed event is remembered so a late retry is recognised.
    /// </summary>
    /// <remarks>
    /// <b>Must exceed the platform's retry window</b>, or a retry arriving after the row is pruned
    /// finds nothing, is admitted again, and is assessed twice. Slack retries for well under an
    /// hour, and this is the margin over that rather than a retention policy.
    /// </remarks>
    private static readonly TimeSpan RetainedAfterAssessment = TimeSpan.FromHours(2);

    private readonly IChatIntakeStore _intake;
    private readonly IServiceProvider _services;
    private readonly IOptions<SlackIngressOptions> _configured;
    private readonly TimeProvider _clock;

    public ChatIntakeDrain(
        IChatIntakeStore intake,
        IServiceProvider services,
        IOptions<SlackIngressOptions> configured,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(clock);

        _intake = intake;
        _services = services;
        _configured = configured;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Checked here rather than at registration, following the traffic port and the SMTP
        // listener: the composition root runs before a test host layers its own configuration in, so
        // a decision taken at registration reads the wrong values.
        if (!_configured.Value.Enabled)
        {
            return;
        }

        // Resolved after that check, and not in the constructor, so a deployment with no chat intake
        // never builds an assessor it has no use for. The assessor refuses to be built without the
        // profile master key, which is right for a deployment that runs one and wrong to demand of
        // one that does not.
        var assessor = _services.GetRequiredService<IChatAssessor>();
        var ledger = _services.GetRequiredService<IDecisionLedger>();

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _configured.Value;

            var waiting = _intake.Waiting(BatchSize);

            if (waiting.Count == 0)
            {
                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                continue;
            }

            var progressed = 0;

            foreach (var entry in waiting)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                if (await AssessAsync(entry, options, assessor, ledger, stoppingToken).ConfigureAwait(false))
                {
                    progressed++;
                }
            }

            _intake.Prune(_clock.GetUtcNow() - RetainedAfterAssessment);

            if (progressed == 0)
            {
                // Nothing was dealt with, so the batch will be offered again identically on the next
                // turn of this loop. Waiting first is what paces that: without it, an event that
                // cannot be assessed is retried as fast as the loop can run, which hammers the
                // assessor and the database for as long as the fault lasts. The delay is the same one
                // an empty intake uses, because both cases are "there is nothing useful to do yet".
                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Assesses one event. Returns true when it has been dealt with, so the caller can tell progress
    /// from an event that is still waiting.
    /// </summary>
    private async Task<bool> AssessAsync(
        ChatIntakeEntry entry,
        SlackIngressOptions options,
        IChatAssessor assessor,
        IDecisionLedger ledger,
        CancellationToken cancellationToken)
    {
        // Read again from the bytes the platform was answered for. A payload that no longer reads as
        // a message is completed rather than left: it will not start reading on a later pass, and
        // leaving it would occupy the intake forever while looking like a queue that is merely busy.
        if (!SlackEventReader.TryRead(entry.Payload, options.Identity(), out var message, out _))
        {
            _intake.Complete(entry.EventId, _clock.GetUtcNow());
            return true;
        }

        var context = new AssessmentContext
        {
            // Configured, not taken from the event. The platform says which workspace it belongs to
            // and nothing it sends is a claim about which of our tenants that is.
            TenantId = options.InboundTenantId,

            // Chat takes no action, so there is no shadow mode to be in and no proposal withheld.
            ShadowMode = false,

            // Assessment-only: this path never sends anything and has no delivery to account for.
            AssessmentOnly = true,

            // The platform's own event id, so a decision can be traced to the event it came from.
            CorrelationId = entry.EventId,

            // Null, because the platform's retry is the only replay this path has and it is resolved
            // by the intake rather than by an idempotency key. A value here would be an id nobody
            // sends back.
            ClientIdempotencyKey = null,

            TimeProvider = _clock,
        };

        try
        {
            var assessment = await assessor
                .AssessAsync(ChatInputFactory.From(message), context, cancellationToken)
                .ConfigureAwait(false);

            await ledger.RecordAsync(assessment, cancellationToken).ConfigureAwait(false);

            _intake.Complete(entry.EventId, _clock.GetUtcNow());
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Left in the intake rather than completed or discarded. It has not been assessed, and
            // the row is the only record that the platform was told it would be, so dropping it here
            // would be the loss the durable intake exists to prevent. It stays visible: an event
            // that never clears is diagnosable, whereas one that vanished is not.
            //
            // The next pass picks it up again, paced by the caller's idle delay, so a transient fault
            // clears itself without the loop spinning on it. A fault that does not clear stays a
            // waiting row rather than disappearing, which is the honest failure.
            return false;
        }
    }
}
