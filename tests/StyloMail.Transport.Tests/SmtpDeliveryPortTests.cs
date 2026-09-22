using System.Net;
using System.Net.Sockets;
using StyloMail.Core;
using StyloMail.Queue;
using StyloMail.Transport.Delivery;
using StyloMail.Transport.Smtp;
using StyloMail.Transport.Tests.Support;

namespace StyloMail.Transport.Tests;

/// <summary>
/// The delivery port is the queue's only door to the outside world, so what it reports back is what
/// the queue's retry logic believes.
/// </summary>
/// <remarks>
/// These drive the port through the queue's own <see cref="IDeliveryPort"/> and
/// <see cref="DeliveryRequest"/> rather than a transport-local shape, so the contract is exercised
/// as the worker will actually call it.
/// </remarks>
public sealed class SmtpDeliveryPortTests
{
    private const string Sender = "sender@example.test";
    private const string WorkerId = "worker-1";

    private static DeliveryRequest Request(
        IReadOnlyList<string> recipients,
        byte[]? payload = null,
        string mailFrom = Sender,
        DateTimeOffset? expiresAt = null) => new()
        {
            QueueId = "q_01",
            InternalMessageId = "msg_01",
            TenantId = "tenant-a",
            Direction = MailDirection.Outbound,
            TrustedPrincipalId = "principal-1",
            MailFrom = mailFrom,
            Recipients = recipients,
            Payload = payload ?? SmtpTestRig.CanonicalMessage(),
            ExpiresAt = expiresAt,
            UntrustedMessageIdHeader = "<abc@example.test>",
        };

    private static SmtpDeliveryPort Port(
        FakeSmtpServer server,
        SmtpBounds? bounds = null,
        SmtpUpstream? upstream = null,
        Action<string>? transcriptSink = null) =>
        new(
            upstream ?? SmtpTestRig.PlainUpstream(server),
            TimeProvider.System,
            bounds,
            transcriptSink: transcriptSink,
            certificateValidation: SmtpTestRig.TrustAnyCertificate);

    /// <summary>
    /// A port nothing can be listening on, for the genuine connection-refused case.
    /// </summary>
    /// <remarks>
    /// Port 1 is in the privileged range, so an unprivileged test process — which this is — cannot
    /// bind it, and no other test in the assembly can take it either. That is what makes it a
    /// deterministic refusal rather than the probe-then-release race this helper used to be, where
    /// the port was released before the dial and anyone could claim it in between.
    ///
    /// <para>
    /// Kept alongside <see cref="DeadEndpoint"/> because they exercise <b>different</b> branches:
    /// this one fails inside the connect, while an endpoint that accepts and closes fails at the
    /// first read of the greeting. Both must produce per-recipient outcomes.
    /// </para>
    /// </remarks>
    private static int UnbindablePort() => 1;

    [Fact]
    public async Task EveryRecipientGetsItsOwnOutcome()
    {
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        var result = await port.DeliverAsync(
            Request(["a@example.test", "b@example.test", "c@example.test"]), CancellationToken.None);

        Assert.Equal(3, result.Recipients.Count);
        Assert.All(result.Recipients, r => Assert.Equal(DeliveryAttemptOutcome.Delivered, r.Outcome));
        Assert.Equal(
            ["a@example.test", "b@example.test", "c@example.test"],
            result.Recipients.Select(r => r.Recipient));
    }

    [Fact]
    public async Task EveryReportedOutcomeIsOneTheQueueAccepts()
    {
        // The port may only report what it genuinely witnessed on the wire. The queue's own events —
        // an elapsed hold, a lapsed lease, a reviewer's decision — are not ours to invent.
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        var delivered = await port.DeliverAsync(Request(["a@example.test"]), CancellationToken.None);

        await using var refusing = FakeSmtpServer.Start(new FakeSmtpBehaviour { RcptCode = 550 });
        await using var refusingPort = Port(refusing);
        var refused = await refusingPort.DeliverAsync(Request(["b@example.test"]), CancellationToken.None);

        await using var ambiguous = FakeSmtpServer.Start(new FakeSmtpBehaviour { DropAfterDataTerminator = true });
        await using var ambiguousPort = Port(ambiguous);
        var inDoubt = await ambiguousPort.DeliverAsync(Request(["c@example.test"]), CancellationToken.None);

        var reported = delivered.Recipients
            .Concat(refused.Recipients)
            .Concat(inDoubt.Recipients)
            .Select(r => r.Outcome)
            .Distinct();

        Assert.All(reported, outcome => Assert.Contains(outcome, DeliveryPortContract.ReportableOutcomes));
        Assert.Contains(DeliveryAttemptOutcome.Delivered, reported);
        Assert.Contains(DeliveryAttemptOutcome.PermanentFailure, reported);
        Assert.Contains(DeliveryAttemptOutcome.InDoubt, reported);
    }

    [Fact]
    public async Task OneRefusedRecipientDoesNotCondemnTheOthers()
    {
        // The spec's rule: persist individual dispositions and retry only the pending recipients.
        // One item-level failure would make the queue re-send two messages that already went out,
        // and duplicate delivery is the harm that rule exists to prevent.
        await using var server = FakeSmtpServer.Start();
        server.Behaviour.RcptCodesByRecipient["b@example.test"] = 550;

        await using var port = Port(server);

        var result = await port.DeliverAsync(
            Request(["a@example.test", "b@example.test", "c@example.test"]), CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.Delivered, result.Recipients[0].Outcome);
        Assert.Equal(DeliveryAttemptOutcome.PermanentFailure, result.Recipients[1].Outcome);
        Assert.Equal(DeliveryAttemptOutcome.Delivered, result.Recipients[2].Outcome);
    }

    [Fact]
    public async Task RecipientsAreDeliveredInSeparateTransactions()
    {
        // Sending them in one RCPT list would disclose the whole recipient set to the upstream, which
        // is how a Bcc leaks, and would make per-recipient outcomes impossible.
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        await port.DeliverAsync(Request(["a@example.test", "b@example.test"]), CancellationToken.None);

        await server.WaitForMessagesAsync(2);
        Assert.Equal(2, server.Commands.Count(c => c.StartsWith("MAIL FROM", StringComparison.Ordinal)));
        Assert.Equal(2, server.Commands.Count(c => c.StartsWith("RCPT TO", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task LostAcknowledgementBecomesInDoubtRatherThanTemporaryFailure()
    {
        // The mapping is the whole point of the port. Collapsing this into TemporaryFailure would
        // tell the queue "retry freely, no duplicate risk" when neither half of that is known.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { DropAfterDataTerminator = true });
        await using var port = Port(server);

        var result = await port.DeliverAsync(Request(["a@example.test"]), CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.InDoubt, result.Recipients[0].Outcome);
    }

    [Fact]
    public async Task ADeadSessionIsReplacedForTheRecipientsStillOwedAnAttempt()
    {
        // One lost connection must not condemn the rest of the message. The first recipient's
        // outcome is ambiguous because its message may have landed; the second gets a clean run.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { DropAfterMessageCount = 1 });
        await using var port = Port(server);

        var result = await port.DeliverAsync(
            Request(["a@example.test", "b@example.test"]), CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.InDoubt, result.Recipients[0].Outcome);
        Assert.Equal(DeliveryAttemptOutcome.Delivered, result.Recipients[1].Outcome);

        Assert.True(
            server.Commands.Count(c => c.StartsWith("EHLO", StringComparison.Ordinal)) >= 2,
            "The port did not re-establish a session for the remaining recipient.");
    }

    [Fact]
    public async Task ATransientUpstreamRefusalIsReportedAsTemporary()
    {
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { RcptCode = 451 });
        await using var port = Port(server);

        var result = await port.DeliverAsync(Request(["a@example.test"]), CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, result.Recipients[0].Outcome);
    }

    [Fact]
    public async Task ATemporaryHandshakeFailureIsTemporaryForEveryRecipient()
    {
        // A TLS downgrade, a refused connection or a timeout must stay retryable: an attacker able
        // to cause one should not be able to make mail disappear permanently.
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server, upstream: SmtpTestRig.TlsUpstream(server));

        var result = await port.DeliverAsync(
            Request(["a@example.test", "b@example.test"]), CancellationToken.None);

        Assert.Equal(2, result.Recipients.Count);
        Assert.All(result.Recipients, r => Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, r.Outcome));
    }

    [Fact]
    public async Task APermanentHandshakeFailureIsReportedAsPermanent()
    {
        // An upstream that refuses AUTH outright will refuse it again; retrying forever would just
        // hold the message until its expiry.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour
        {
            AdvertiseStartTls = true,
            AdvertiseAuth = true,
            AuthUsername = "stylomail",
            AuthPassword = "right",
        });

        var credentials = new SmtpCredentials { Username = "stylomail", Password = "wrong" };
        await using var port = Port(server, upstream: SmtpTestRig.TlsUpstream(server, credentials));

        var result = await port.DeliverAsync(Request(["a@example.test"]), CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.PermanentFailure, result.Recipients[0].Outcome);
    }

    [Fact]
    public async Task ARefusedConnectionIsAnOutcomeForEveryRecipientNotAnException()
    {
        // The worker cannot act on an exception: it says nothing about which recipients were tried,
        // so the queue would be guessing about a message that may already be delivered.
        var upstream = new SmtpUpstream
        {
            Host = "127.0.0.1",
            Port = UnbindablePort(),
            Tls = SmtpTlsMode.None,
            HeloName = "stylomail.test",
        };

        await using var port = new SmtpDeliveryPort(
            upstream,
            TimeProvider.System,
            certificateValidation: SmtpTestRig.TrustAnyCertificate);

        var result = await port.DeliverAsync(
            Request(["a@example.test", "b@example.test"]), CancellationToken.None);

        Assert.Equal(2, result.Recipients.Count);
        Assert.All(result.Recipients, r => Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, r.Outcome));
    }

    [Fact]
    public async Task AnUpstreamThatAcceptsThenClosesIsAnOutcomeForEveryRecipientNotAnException()
    {
        // The second unreachable-upstream branch, and a different one from a refused connect: here
        // the TCP connection succeeds and the failure arrives at the first read of the greeting, so
        // it throws from a different place in the session. Both must still come back as one outcome
        // per recipient.
        await using var endpoint = DeadEndpoint.Start();

        var upstream = new SmtpUpstream
        {
            Host = "127.0.0.1",
            Port = endpoint.Port,
            Tls = SmtpTlsMode.None,
            HeloName = "stylomail.test",
        };

        await using var port = new SmtpDeliveryPort(
            upstream,
            TimeProvider.System,
            certificateValidation: SmtpTestRig.TrustAnyCertificate);

        var result = await port.DeliverAsync(
            Request(["a@example.test", "b@example.test"]), CancellationToken.None);

        Assert.Equal(2, result.Recipients.Count);
        Assert.All(result.Recipients, r => Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, r.Outcome));

        // The connection was genuinely established before failing, which is what distinguishes this
        // from the refused-connect test above. Without this assertion the two would be
        // interchangeable and the claim that they cover different branches would be decoration.
        Assert.Equal(1, endpoint.AcceptedCount);
    }

    [Fact]
    public async Task MoreRecipientsThanTheBoundIsRefusedAsAWholeRatherThanTruncated()
    {
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server, bounds: new SmtpBounds { MaxRecipients = 2 });

        var result = await port.DeliverAsync(
            Request(["a@example.test", "b@example.test", "c@example.test"]), CancellationToken.None);

        Assert.Equal(3, result.Recipients.Count);
        Assert.All(result.Recipients, r => Assert.Equal(DeliveryAttemptOutcome.PermanentFailure, r.Outcome));
        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task NoRecipientsMeansNoConnection()
    {
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        var result = await port.DeliverAsync(Request([]), CancellationToken.None);

        Assert.Empty(result.Recipients);
        Assert.Empty(server.Commands);
    }

    [Fact]
    public async Task AnInvalidRecipientFailsAloneWhileTheRestAreDelivered()
    {
        // A crafted envelope address is data, not a crash. It must not take out the other recipients
        // of the same message, nor the worker handling it.
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        var result = await port.DeliverAsync(
            Request(["bad\r\nRCPT TO:<evil@example.test>", "good@example.test"]), CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.PermanentFailure, result.Recipients[0].Outcome);
        Assert.Equal(DeliveryAttemptOutcome.Delivered, result.Recipients[1].Outcome);
        Assert.DoesNotContain(server.Commands, c => c.Contains("evil@example.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOriginalBytesReachTheUpstreamUnchanged()
    {
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        var payload = SmtpTestRig.CanonicalMessage("A body with a . leading dot line.\r\n.hidden");

        await port.DeliverAsync(Request(["a@example.test"], payload), CancellationToken.None);

        await server.WaitForMessagesAsync(1);
        Assert.Equal(payload, server.Messages[0].Recovered);
    }

    // ---- Message lifetime ----------------------------------------------------------------------

    [Fact]
    public async Task AnAlreadyExpiredMessageIsNotAttemptedAtAll()
    {
        // Fail fast rather than consume the budget of a message that will be given up on. Nothing is
        // sent, and the outcome is a retryable failure — the queue owns expiry and we do not
        // pre-empt its decision by declaring a permanent one.
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        var result = await port.DeliverAsync(
            Request(["a@example.test"], expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
            CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, result.Recipients[0].Outcome);
        Assert.Contains("lifetime", result.Recipients[0].Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(server.Commands);
    }

    [Fact]
    public async Task ABudgetTooSmallToAttemptIsFailedFast()
    {
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        var result = await port.DeliverAsync(
            Request(["a@example.test"], expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(200)),
            CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, result.Recipients[0].Outcome);
        Assert.Empty(server.Commands);
    }

    [Fact]
    public async Task AnAttemptThatOutlivesTheMessagesLifetimeIsAbandonedRatherThanRunOn()
    {
        // The budget is the message's real remaining lifetime, not a fixed guess. A silent upstream
        // would otherwise hold this for the full greeting timeout while the message expired under it.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { Silent = true });

        // The floor is lowered so the attempt actually starts, rather than being refused up front.
        await using var port = new SmtpDeliveryPort(
            SmtpTestRig.PlainUpstream(server),
            TimeProvider.System,
            minimumAttemptBudget: TimeSpan.FromMilliseconds(1),
            certificateValidation: SmtpTestRig.TrustAnyCertificate);

        var result = await port.DeliverAsync(
            Request(["a@example.test"], expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(300)),
            CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, result.Recipients[0].Outcome);
    }

    [Fact]
    public async Task AMessageWithNoDeadlineIsNotClamped()
    {
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        var result = await port.DeliverAsync(Request(["a@example.test"]), CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.Delivered, result.Recipients[0].Outcome);
    }

    // ---- Cancellation --------------------------------------------------------------------------
    //
    // The port's contract is one outcome per recipient, and it knows which recipients it reached.
    // Throwing on cancellation would discard exactly that information and leave the worker recording
    // an item-level lease expiry much later instead of the per-recipient truth now.

    [Fact]
    public async Task AnAlreadyCancelledCallerGetsOutcomesNotAnException()
    {
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await port.DeliverAsync(
            Request(["a@example.test", "b@example.test"]), cancelled.Token);

        Assert.Equal(2, result.Recipients.Count);
        Assert.All(result.Recipients, r => Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, r.Outcome));
        Assert.Empty(server.Commands);
    }

    [Fact]
    public async Task ACallerCancellationMidAttemptYieldsOutcomesNotAnException()
    {
        // A shut-down worker cancelling its in-flight token. The message never got far enough for the
        // upstream to have an opinion, so nothing is ambiguous — but the caller still learns what
        // happened to each recipient.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { Silent = true });
        await using var port = Port(server);

        using var cts = new CancellationTokenSource();
        var pending = port.DeliverAsync(Request(["a@example.test"]), cts.Token).AsTask();

        await Task.Delay(200);
        await cts.CancelAsync();

        var result = await pending;

        Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, result.Recipients[0].Outcome);
    }

    [Fact]
    public async Task ACancellationLandingAfterTheTerminatorIsInDoubtNotTemporaryFailure()
    {
        // The sharpest case, and the one a drain window actually produces: the body and its
        // terminator are on the wire, the verdict is outstanding, and the caller pulls the token.
        // The upstream may have accepted the message, so this is ambiguous — reporting it as a
        // plain temporary failure would invite a retry with no duplicate risk recorded.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour
        {
            FinalReplyDelay = TimeSpan.FromSeconds(30),
        });

        await using var port = Port(server);

        using var cts = new CancellationTokenSource();
        var pending = port.DeliverAsync(Request(["a@example.test"]), cts.Token).AsTask();

        // Long enough for the body and terminator to be written and the reply to be awaited.
        await Task.Delay(300);
        await cts.CancelAsync();

        var result = await pending;

        Assert.Equal(DeliveryAttemptOutcome.InDoubt, result.Recipients[0].Outcome);
    }

    [Fact]
    public async Task ACancellationIsAlsoReportedForEveryRecipientNotYetReached()
    {
        // The first recipient is in flight when the token is pulled; the rest must still each get an
        // outcome rather than vanishing from the result.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour
        {
            FinalReplyDelay = TimeSpan.FromSeconds(30),
        });

        await using var port = Port(server);

        using var cts = new CancellationTokenSource();
        var pending = port
            .DeliverAsync(Request(["a@example.test", "b@example.test", "c@example.test"]), cts.Token)
            .AsTask();

        await Task.Delay(300);
        await cts.CancelAsync();

        var result = await pending;

        Assert.Equal(3, result.Recipients.Count);
        Assert.Equal(DeliveryAttemptOutcome.InDoubt, result.Recipients[0].Outcome);
        Assert.All(result.Recipients.Skip(1), r =>
            Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, r.Outcome));
    }

    // ---- Contract composition ------------------------------------------------------------------

    [Fact]
    public void TheResultComposesStraightIntoAReport()
    {
        // AsReport hands back the same list instance rather than a copy, which is what makes "no
        // mapping layer that can drift" true rather than aspirational.
        var recipients = new List<RecipientDeliveryResult>
        {
            new() { Recipient = "a@example.test", Outcome = DeliveryAttemptOutcome.Delivered },
        };

        var result = new DeliveryPortResult { Recipients = recipients, Detail = "ok" };
        var report = result.AsReport(WorkerId);

        Assert.Equal(WorkerId, report.WorkerId);
        Assert.Same(recipients, report.Recipients);
        Assert.Equal("ok", report.Detail);
    }

    [Fact]
    public async Task TheResultComposesIntoAReportUnchanged()
    {
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        var result = await port.DeliverAsync(Request(["a@example.test", "b@example.test"]), CancellationToken.None);
        var report = result.AsReport(WorkerId);

        Assert.Equal(2, report.Recipients.Count);
        Assert.All(report.Recipients, r => Assert.Contains(r.Outcome, DeliveryPortContract.ReportableOutcomes));
    }

    // ---- Concurrency ---------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentAttemptsForTheSameMessageDoNotCorruptEachOther()
    {
        // A lapsed lease means two workers can hold the same item at once, so the port can be called
        // concurrently for the same message. It must not mix one call's recipients into another's
        // result.
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server);

        var calls = Enumerable.Range(0, 6)
            .Select(i => port.DeliverAsync(Request([$"r{i}@example.test"]), CancellationToken.None).AsTask())
            .ToArray();

        var results = await Task.WhenAll(calls);

        for (var i = 0; i < results.Length; i++)
        {
            var single = Assert.Single(results[i].Recipients);
            Assert.Equal($"r{i}@example.test", single.Recipient);
            Assert.Equal(DeliveryAttemptOutcome.Delivered, single.Outcome);
        }
    }

    // ---- Diagnostics ---------------------------------------------------------------------------

    [Fact]
    public async Task TheUpstreamReplyIsCarriedIntoTheResultDetail()
    {
        // The reply code is the only evidence of why a message was refused, and it is gone once the
        // socket is. This is what lands on the audit path.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour
        {
            RcptCode = 550,
        });

        await using var port = Port(server);

        var result = await port.DeliverAsync(Request(["a@example.test"]), CancellationToken.None);

        Assert.Contains("550", result.Recipients[0].Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsCannotForgeExtraLinesInTheLedger()
    {
        // Detail is stored on a security component's audit path, so reply text must not be able to
        // inject a line that reads as a separate record.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour
        {
            RcptCode = 550,
        });

        await using var port = Port(server);

        var result = await port.DeliverAsync(Request(["a@example.test"]), CancellationToken.None);
        var detail = result.Recipients[0].Detail!;

        Assert.DoesNotContain('\n', detail);
        Assert.DoesNotContain('\r', detail);
    }

    [Fact]
    public async Task TheTranscriptIsOfferedToTheSinkAndNeverFailsTheDelivery()
    {
        await using var server = FakeSmtpServer.Start();

        var captured = new List<string>();
        await using var port = Port(server, transcriptSink: captured.Add);

        var result = await port.DeliverAsync(Request(["a@example.test"]), CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.Delivered, result.Recipients[0].Outcome);
        var transcript = Assert.Single(captured);
        Assert.Contains("MAIL FROM", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrowingTranscriptSinkCannotFailADelivery()
    {
        // Losing a diagnostic is a nuisance; losing mail because the ledger was unavailable is the
        // failure this component exists to prevent.
        await using var server = FakeSmtpServer.Start();
        await using var port = Port(server, transcriptSink: _ => throw new IOException("ledger down"));

        var result = await port.DeliverAsync(Request(["a@example.test"]), CancellationToken.None);

        Assert.Equal(DeliveryAttemptOutcome.Delivered, result.Recipients[0].Outcome);
    }
}
