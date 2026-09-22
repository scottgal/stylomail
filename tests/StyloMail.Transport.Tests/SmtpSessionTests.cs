using System.Text;
using StyloMail.Transport.Smtp;
using StyloMail.Transport.Tests.Support;

namespace StyloMail.Transport.Tests;

/// <summary>
/// End-to-end protocol behaviour against a real socket on loopback, with a real TLS handshake.
/// </summary>
public sealed class SmtpSessionTests
{
    private const string Sender = "sender@example.test";
    private const string Recipient = "recipient@example.test";

    [Fact]
    public async Task DeliversAMessageAndRecordsTheEnvelope()
    {
        await using var server = FakeSmtpServer.Start();
        await using var session = await SmtpTestRig.OpenAsync(server);

        var payload = SmtpTestRig.CanonicalMessage();

        var result = await session.SendAsync(Sender, Recipient, payload, CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.Accepted, result.Outcome);
        Assert.Equal(250, result.Reply!.Code);

        await server.WaitForMessagesAsync(1);
        var received = server.Messages[0];
        Assert.Equal(Sender, received.MailFrom);
        Assert.Equal(Recipient, received.Recipient);
    }

    [Fact]
    public async Task OriginalBytesArriveUnchangedThroughTheHandoff()
    {
        // The property the whole "do not rewrite signed content" rule rests on. A client library
        // that round-tripped the message through its own object model would re-fold headers and
        // break DKIM at the receiving end; this asserts the bytes themselves survived.
        await using var server = FakeSmtpServer.Start();
        await using var session = await SmtpTestRig.OpenAsync(server);

        var payload = SmtpTestRig.CanonicalMessage("The body has  multiple   spaces and\ttabs.");

        var result = await session.SendAsync(Sender, Recipient, payload, CancellationToken.None);
        Assert.Equal(SmtpTransactionOutcome.Accepted, result.Outcome);

        await server.WaitForMessagesAsync(1);
        Assert.Equal(payload, server.Messages[0].Recovered);
    }

    [Fact]
    public async Task HeaderBlockIsNotRefoldedOrReordered()
    {
        await using var server = FakeSmtpServer.Start();
        await using var session = await SmtpTestRig.OpenAsync(server);

        // A long, deliberately foldable header and an unusual header order: a serialiser that parsed
        // the message would be tempted to normalise both.
        var payload = Encoding.UTF8.GetBytes(
            "X-Last: deliberately first\r\n"
            + "Subject: a subject long enough that a well-meaning serialiser would want to refold it \r\n"
            + "\tand it continues on a folded line\r\n"
            + "From: sender@example.test\r\n"
            + "\r\n"
            + "body\r\n");

        await session.SendAsync(Sender, Recipient, payload, CancellationToken.None);

        await server.WaitForMessagesAsync(1);
        Assert.Equal(payload, server.Messages[0].Recovered);
    }

    [Fact]
    public async Task ALineBeginningWithADotSurvivesDotStuffing()
    {
        // Without stuffing, a line that is exactly "." would end the message early and silently
        // truncate it, a real content-integrity failure, not a formatting nicety.
        await using var server = FakeSmtpServer.Start();
        await using var session = await SmtpTestRig.OpenAsync(server);

        var payload = Encoding.UTF8.GetBytes(
            "From: sender@example.test\r\n\r\n"
            + ".\r\n"
            + ".hidden\r\n"
            + "..double\r\n"
            + "normal\r\n");

        await session.SendAsync(Sender, Recipient, payload, CancellationToken.None);

        await server.WaitForMessagesAsync(1);
        Assert.Equal(payload, server.Messages[0].Recovered);
        Assert.Contains(".hidden", server.Messages[0].RecoveredText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareLineFeedPayload_IsNormalisedToCrLfAndNothingElseChanges()
    {
        // The one deviation from stored bytes, and it is required by RFC 5321. Stated exactly so it
        // cannot quietly widen into "we may rewrite the message".
        await using var server = FakeSmtpServer.Start();
        await using var session = await SmtpTestRig.OpenAsync(server);

        var payload = Encoding.UTF8.GetBytes("From: sender@example.test\nSubject: lf only\n\nbody line\n");

        await session.SendAsync(Sender, Recipient, payload, CancellationToken.None);

        await server.WaitForMessagesAsync(1);
        Assert.Equal(SmtpTestRig.NormaliseToCrLf(payload), server.Messages[0].Recovered);
    }

    [Fact]
    public async Task MultipleRecipientsBecomeSeparateTransactions()
    {
        // Per-recipient transactions are what make a per-recipient outcome possible and what stops
        // a Bcc leaking to the upstream as part of a shared recipient list.
        await using var server = FakeSmtpServer.Start();
        await using var session = await SmtpTestRig.OpenAsync(server);

        var payload = SmtpTestRig.CanonicalMessage();

        await session.SendAsync(Sender, "a@example.test", payload, CancellationToken.None);
        await session.SendAsync(Sender, "b@example.test", payload, CancellationToken.None);

        await server.WaitForMessagesAsync(2);
        Assert.Equal(["a@example.test", "b@example.test"], server.Messages.Select(m => m.Recipient));
        Assert.Equal(2, server.Commands.Count(c => c.StartsWith("MAIL FROM", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NullSenderRendersAsBareAngleBrackets()
    {
        // The null sender is what a DSN uses, and rendering it as <> rather than <""> is the
        // difference between a valid bounce and a malformed one.
        await using var server = FakeSmtpServer.Start();
        await using var session = await SmtpTestRig.OpenAsync(server);

        await session.SendAsync(string.Empty, Recipient, SmtpTestRig.CanonicalMessage(), CancellationToken.None);

        await server.WaitForMessagesAsync(1);
        Assert.Contains(server.Commands, c => c.StartsWith("MAIL FROM:<>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransientRejectionAtRcptIsReportedAsRetryable()
    {
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { RcptCode = 452 });
        await using var session = await SmtpTestRig.OpenAsync(server);

        var result = await session.SendAsync(Sender, Recipient, SmtpTestRig.CanonicalMessage(), CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.TransientRejection, result.Outcome);
        Assert.Equal(SmtpDeliveryStage.RcptTo, result.Stage);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task PermanentRejectionAtRcptIsReportedAsNotRetryable()
    {
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { RcptCode = 550 });
        await using var session = await SmtpTestRig.OpenAsync(server);

        var result = await session.SendAsync(Sender, Recipient, SmtpTestRig.CanonicalMessage(), CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.PermanentRejection, result.Outcome);
        Assert.False(result.IsRetryable);
    }

    [Fact]
    public async Task RejectionAfterTheBodyIsReportedAsARecordedOutcomeNotAnException()
    {
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour
        {
            FinalDataCode = 552,
            FinalDataText = "5.3.4 Message size exceeds fixed limit",
        });
        await using var session = await SmtpTestRig.OpenAsync(server);

        var result = await session.SendAsync(Sender, Recipient, SmtpTestRig.CanonicalMessage(), CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.PermanentRejection, result.Outcome);
        Assert.Equal("5.3.4", result.Reply!.EnhancedStatusCode);

        // Exactly one transaction. The spec forbids sending a bespoke warning or bounce to a
        // possibly-spoofed From, so a rejection must not turn into a second message back.
        Assert.Single(server.Commands, c => c.StartsWith("MAIL FROM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LostAcknowledgementAfterTheTerminator_IsInDoubtRatherThanAFailure()
    {
        // SMTP delivery is not exactly-once. The upstream consumed the body and its terminator and
        // then died before the verdict; the message may well have been accepted. Reporting this as a
        // plain failure invites a silent drop, so it is surfaced as ambiguous.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { DropAfterDataTerminator = true });
        await using var session = await SmtpTestRig.OpenAsync(server);

        var result = await session.SendAsync(Sender, Recipient, SmtpTestRig.CanonicalMessage(), CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.InDoubt, result.Outcome);
        Assert.True(result.IsRetryable);
        Assert.Contains("may have accepted", result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    // The two sides of the terminator boundary, and they are separated by a socket buffer rather
    // than by which knob the rig sets. Both were found by measurement, not assumed:
    //
    //   payload ≤ 4 KB   → written successfully into the buffer, failure seen awaiting the verdict
    //                      → terminator was written → InDoubt
    //   payload ≥ 64 KB  → the write itself fails, terminator never written → Failed
    //
    // Both are correct. The first is honest because the peer may have received everything; the
    // second is honest because nothing can be accepted without a terminator. A test that pins the
    // pre-terminator path MUST exceed the socket buffer, and say so, otherwise it quietly starts
    // asserting the other case, which is exactly how this was mistaken for a contradiction.

    [Fact]
    public async Task AConnectionLostBeforeTheTerminatorIsReached_IsAFailureNotInDoubt()
    {
        // Nothing was committed, so retrying cannot duplicate. The 4 MB body is load-bearing: it
        // overflows the socket buffer so the write itself fails. Shrinking it to a few kilobytes
        // changes which failure the client observes and this assertion stops testing what it says.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { CloseAfterDataCommand = true });
        await using var session = await SmtpTestRig.OpenAsync(server);

        var payload = new byte[4 * 1024 * 1024];
        Array.Fill(payload, (byte)'x');

        var result = await session.SendAsync(Sender, Recipient, payload, CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.Failed, result.Outcome);
        Assert.Equal(SmtpDeliveryStage.DataBody, result.Stage);
    }

    [Fact]
    public async Task AConnectionLostAfterASmallBodyIsInDoubt_BecauseTheTerminatorDidGetWritten()
    {
        // The counterpart, pinned rather than left implicit. A small message fits the socket buffer,
        // so the client writes the body AND the terminator successfully and only then discovers the
        // peer is gone. The upstream may have the message, so a plain failure would understate it.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { CloseAfterDataCommand = true });
        await using var session = await SmtpTestRig.OpenAsync(server);

        var result = await session.SendAsync(Sender, Recipient, SmtpTestRig.CanonicalMessage(), CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.InDoubt, result.Outcome);
    }

    // ---- TLS ---------------------------------------------------------------------------------

    [Fact]
    public async Task TlsRequiredButNotAdvertised_RefusesBeforeAnyMailIsSent()
    {
        // Also the shape of a STARTTLS-stripping attack. Nothing about this message leaves in clear.
        await using var server = FakeSmtpServer.Start();

        var ex = await Assert.ThrowsAsync<SmtpSessionException>(
            () => SmtpTestRig.OpenAsync(server, SmtpTestRig.TlsUpstream(server)));

        Assert.Equal(SmtpDeliveryStage.StartTls, ex.Stage);

        // Retryable rather than permanent: an attacker stripping STARTTLS produces this exact
        // symptom, and a retry that succeeds is the right outcome. Burying the message would let an
        // attacker silence mail merely by being present.
        Assert.False(ex.IsPermanent);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("MAIL FROM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TlsRequiredAndAdvertised_NegotiatesAndReissuesEhlo()
    {
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { AdvertiseStartTls = true });
        await using var session = await SmtpTestRig.OpenAsync(server, SmtpTestRig.TlsUpstream(server));

        var result = await session.SendAsync(Sender, Recipient, SmtpTestRig.CanonicalMessage(), CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.Accepted, result.Outcome);
        Assert.Contains("STARTTLS", server.Commands);

        // RFC 3207 requires a fresh EHLO after the handshake, because the pre-TLS capability list is
        // attacker-writable. Exactly two EHLOs is the observable proof that it happened.
        Assert.Equal(2, server.Commands.Count(c => c.StartsWith("EHLO", StringComparison.Ordinal)));
        Assert.Single(server.Messages);
    }

    [Fact]
    public async Task StartTlsAdvertisedThenRefused_NeverContinuesInTheClear()
    {
        // The downgrade signature: the capability was offered and then denied. No legitimate server
        // does this, and continuing in plaintext here is exactly what an attacker engineers.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour
        {
            AdvertiseStartTls = true,
            StartTlsCode = 454,
        });

        var ex = await Assert.ThrowsAsync<SmtpSessionException>(
            () => SmtpTestRig.OpenAsync(server, SmtpTestRig.TlsUpstream(server)));

        Assert.Equal(SmtpDeliveryStage.StartTls, ex.Stage);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("MAIL FROM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpportunisticTlsStillRefusesAFailedStartTls()
    {
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour
        {
            AdvertiseStartTls = true,
            StartTlsCode = 454,
        });

        await Assert.ThrowsAsync<SmtpSessionException>(
            () => SmtpTestRig.OpenAsync(server, SmtpTestRig.TlsUpstream(server, tls: SmtpTlsMode.Opportunistic)));
    }

    [Fact]
    public void CredentialsWithTlsDisabled_AreRefusedAtConstruction()
    {
        // Enforced where the object is built, so a misconfiguration is a startup failure rather than
        // a password on the wire discovered later.
        var upstream = new SmtpUpstream
        {
            Host = "127.0.0.1",
            Port = 25,
            Tls = SmtpTlsMode.None,
            Credentials = new SmtpCredentials { Username = "user", Password = "secret" },
        };

        var ex = Assert.Throws<InvalidOperationException>(upstream.Validate);

        Assert.Contains("clear", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CredentialsTravelOnlyOverAnEncryptedConnection()
    {
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour
        {
            AdvertiseStartTls = true,
            AdvertiseAuth = true,
            AuthUsername = "stylomail",
            AuthPassword = "correct-horse",
        });

        await using var session = await SmtpTestRig.OpenAsync(
            server,
            SmtpTestRig.TlsUpstream(server, new SmtpCredentials { Username = "stylomail", Password = "correct-horse" }));

        var result = await session.SendAsync(Sender, Recipient, SmtpTestRig.CanonicalMessage(), CancellationToken.None);
        Assert.Equal(SmtpTransactionOutcome.Accepted, result.Outcome);

        var authCommands = server.RecordedCommands.Where(c => c.Text.StartsWith("AUTH", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(authCommands);
        Assert.All(authCommands, c => Assert.True(c.Encrypted, "An AUTH command arrived unencrypted."));
    }

    [Fact]
    public async Task AuthenticationFailureIsReportedAsASessionFailure()
    {
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour
        {
            AdvertiseStartTls = true,
            AdvertiseAuth = true,
            AuthUsername = "stylomail",
            AuthPassword = "correct-horse",
        });

        var ex = await Assert.ThrowsAsync<SmtpSessionException>(
            () => SmtpTestRig.OpenAsync(
                server,
                SmtpTestRig.TlsUpstream(server, new SmtpCredentials { Username = "stylomail", Password = "wrong" })));

        Assert.Equal(SmtpDeliveryStage.Auth, ex.Stage);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("MAIL FROM", StringComparison.Ordinal));
    }

    [Fact]
    public void CredentialsCannotBePrinted()
    {
        // An interpolated log line or an exception message that captures the record must not leak it.
        var credentials = new SmtpCredentials { Username = "stylomail", Password = "correct-horse" };

        Assert.DoesNotContain("correct-horse", credentials.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("correct-horse", $"{credentials}", StringComparison.Ordinal);
    }

    [Fact]
    public void TheTranscriptNeverContainsACredential()
    {
        // Redaction happens at the point of recording, so there is no path on which the secret is
        // held and merely "not printed".
        var transcript = new SmtpTranscript();
        transcript.RecordClient("AUTH PLAIN AHVzZXIAcGFzc3dvcmQ=", isCredentialBearing: true);
        transcript.RecordClient("cGFzc3dvcmQ=", isCredentialBearing: true);

        var rendered = transcript.ToString();

        Assert.DoesNotContain("AHVzZXIAcGFzc3dvcmQ=", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("cGFzc3dvcmQ=", rendered, StringComparison.Ordinal);
        Assert.Contains(SmtpTranscript.Redacted, rendered, StringComparison.Ordinal);
    }

    // ---- Bounds and injection ------------------------------------------------------------------

    [Fact]
    public async Task EnvelopeAddressContainingCrLf_IsRefusedWithoutTouchingTheUpstream()
    {
        // SMTP command injection. An address is interpolated into MAIL FROM:<...>, so a CR or LF in
        // it would let a crafted envelope append commands of the submitter's choosing.
        await using var server = FakeSmtpServer.Start();
        await using var session = await SmtpTestRig.OpenAsync(server);

        // No angle brackets at all before the CRLF, so this can only be caught by the CR/LF check.
        var injected = "victim@example.test\r\nRCPT TO:<attacker@evil.test>";

        var result = await session.SendAsync(Sender, injected, SmtpTestRig.CanonicalMessage(), CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.PermanentRejection, result.Outcome);
        Assert.Contains("injection", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(server.Commands, c => c.Contains("evil.test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnvelopeAddressWithEmbeddedAngleBrackets_IsRefusedWithoutTouchingTheUpstream()
    {
        // A second injection route: a '<' or '>' closes the address early and leaves the remainder
        // as bare command syntax.
        await using var server = FakeSmtpServer.Start();
        await using var session = await SmtpTestRig.OpenAsync(server);

        var result = await session.SendAsync(
            Sender,
            "victim@example.test> SIZE=99999999",
            SmtpTestRig.CanonicalMessage(),
            CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.PermanentRejection, result.Outcome);
        Assert.Contains("angle brackets", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(server.Commands, c => c.Contains("SIZE=99999999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonAsciiRecipient_IsRefusedRatherThanTranscoded()
    {
        await using var server = FakeSmtpServer.Start();
        await using var session = await SmtpTestRig.OpenAsync(server);

        var result = await session.SendAsync(Sender, "bücher@example.test", SmtpTestRig.CanonicalMessage(), CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.PermanentRejection, result.Outcome);
        Assert.Contains("ASCII", result.Detail!, StringComparison.Ordinal);
        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task OversizeMessage_IsRefusedLocallyWithoutOpeningATransaction()
    {
        await using var server = FakeSmtpServer.Start();
        var bounds = new SmtpBounds { MaxMessageBytes = 1024 };
        await using var session = await SmtpTestRig.OpenAsync(server, bounds: bounds);

        var payload = new byte[4096];

        var result = await session.SendAsync(Sender, Recipient, payload, CancellationToken.None);

        Assert.Equal(SmtpTransactionOutcome.PermanentRejection, result.Outcome);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("MAIL FROM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASilentUpstream_IsBoundedRatherThanWaitedOnForever()
    {
        // The bound is what stops a peer that accepts TCP and then says nothing from pinning a
        // worker indefinitely.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { Silent = true });
        var bounds = new SmtpBounds { GreetingTimeout = TimeSpan.FromMilliseconds(400) };

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => SmtpTestRig.OpenAsync(server, bounds: bounds));

        Assert.True(
            ex is SmtpTimeoutException or SmtpSessionException,
            $"Expected a bounded failure, got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public async Task AGreetingRefusalIsASessionFailureNotADeliveryOutcome()
    {
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour
        {
            GreetingCode = 554,
            GreetingText = "5.7.1 Access denied",
        });

        var ex = await Assert.ThrowsAsync<SmtpSessionException>(() => SmtpTestRig.OpenAsync(server));

        Assert.Equal(SmtpDeliveryStage.Greeting, ex.Stage);
        Assert.True(ex.IsPermanent);
    }

    [Fact]
    public async Task ASessionThatLostItsConnectionRefusesFurtherUse()
    {
        // A stream abandoned mid-reply cannot be resynchronised by guessing where the next reply
        // starts, so the session must be abandoned rather than reused.
        await using var server = FakeSmtpServer.Start(new FakeSmtpBehaviour { DropAfterDataTerminator = true });
        await using var session = await SmtpTestRig.OpenAsync(server);

        await session.SendAsync(Sender, Recipient, SmtpTestRig.CanonicalMessage(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SendAsync(Sender, Recipient, SmtpTestRig.CanonicalMessage(), CancellationToken.None).AsTask());
    }
}
