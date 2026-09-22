using System.Text;
using StyloMail.Core;
using StyloMail.Transport.Ingress;
using StyloMail.Transport.Tests.Support;

namespace StyloMail.Transport.Tests;

/// <summary>
/// The listener is the boundary, so these tests are mostly about what it refuses.
/// </summary>
public sealed class SmtpSubmissionListenerTests
{
    private const string LocalDomain = "example.test";
    private const string OurName = "stylomail.example.test";

    private static SmtpIngressOptions Options(
        RecipientDomainPolicy? domains = null,
        bool allowUnauthenticatedInbound = true,
        int maxHops = 20,
        IReadOnlyList<string>? localIdentities = null) => new()
        {
            Port = 0,
            BindAddress = System.Net.IPAddress.Loopback,
            ServerName = OurName,
            Certificate = FakeSmtpServer.Certificate,
            RecipientDomains = domains ?? new RecipientDomainPolicy([LocalDomain]),
            LocalHostIdentities = localIdentities ?? [OurName],
            AllowUnauthenticatedInbound = allowUnauthenticatedInbound,
            MaxHops = maxHops,
            InboundTenantId = "t_inbound",
        };

    /// <summary>Connects, greets, and negotiates TLS — the state every accepted message needs to reach.</summary>
    private static async Task<(TestSmtpClient Client, ClientReply Ehlo)> OpenEncryptedAsync(int port)
    {
        var client = await TestSmtpClient.ConnectAsync(port);
        var greeting = await client.ReadReplyAsync();
        Assert.Equal(220, greeting.Code);

        await client.SendAsync("EHLO test.client");
        var startTls = await client.StartTlsAsync();
        Assert.Equal(220, startTls.Code);

        // RFC 3207 requires a fresh EHLO after the handshake, and the listener enforces it.
        var ehlo = await client.SendAsync("EHLO test.client");
        return (client, ehlo);
    }

    private static byte[] Message(int receivedHeaders = 0, string subject = "hello", string body = "a body")
    {
        var builder = new StringBuilder();
        for (var i = 0; i < receivedHeaders; i++)
        {
            builder.Append("Received: from relay").Append(i).Append(".example.net by mta")
                .Append(i).Append(".example.net with ESMTP\r\n");
        }

        builder.Append("From: sender@example.net\r\n");
        builder.Append("To: recipient@").Append(LocalDomain).Append("\r\n");
        builder.Append("Subject: ").Append(subject).Append("\r\n");
        builder.Append("Message-ID: <msg-1@example.net>\r\n");
        builder.Append("\r\n");
        builder.Append(body).Append("\r\n");

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    [Fact]
    public async Task AcceptsInboundForAConfiguredDomainAndAnswersWithTheQueueId()
    {
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, ehlo) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _ = client;

        // No longer offered once the connection is already encrypted — renegotiation is not a thing
        // a client should be asked to do.
        Assert.DoesNotContain(ehlo.Lines, l => l.Contains("STARTTLS", StringComparison.Ordinal));

        Assert.Equal(250, (await client.SendAsync("MAIL FROM:<sender@example.net>")).Code);
        Assert.Equal(250, (await client.SendAsync($"RCPT TO:<user@{LocalDomain}>")).Code);

        var verdict = await client.SendMessageAsync(Message());

        Assert.Equal(250, verdict.Code);
        Assert.Contains("q_test_1", verdict.Text, StringComparison.Ordinal);

        var submission = Assert.Single(sink.Submissions);
        Assert.Equal(MailDirection.Inbound, submission.Direction);
        Assert.Equal("sender@example.net", submission.MailFrom);
        Assert.Equal($"user@{LocalDomain}", Assert.Single(submission.Recipients));
        Assert.Equal("<msg-1@example.net>", submission.UntrustedMessageIdHeader);
        Assert.Equal("t_inbound", submission.TenantId);
    }

    [Fact]
    public async Task RefusesUnauthenticatedRelayToAForeignDomain()
    {
        // The open-relay refusal. This is the single most important thing the listener does: without
        // it, anyone who can reach the port can send mail to anyone in the world through us.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        var rcpt = await client.SendAsync("RCPT TO:<victim@elsewhere.example>");

        Assert.Equal(550, rcpt.Code);
        Assert.Contains("Relay access denied", rcpt.Text, StringComparison.Ordinal);
        Assert.Empty(sink.Submissions);
    }

    [Fact]
    public async Task RefusesInboundWhenNoRecipientDomainsAreConfigured()
    {
        // An empty configuration must not read as permission.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(
            Options(domains: RecipientDomainPolicy.None), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        var rcpt = await client.SendAsync($"RCPT TO:<user@{LocalDomain}>");

        Assert.Equal(550, rcpt.Code);
    }

    [Fact]
    public async Task RefusesMailBeforeTheConnectionIsEncrypted()
    {
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        await using var client = await TestSmtpClient.ConnectAsync(listener.BoundPort);
        await client.ReadReplyAsync();
        await client.SendAsync("EHLO test.client");

        var mail = await client.SendAsync("MAIL FROM:<sender@example.net>");

        Assert.Equal(530, mail.Code);
        Assert.Contains("STARTTLS", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefersRatherThanAcceptingWhenDurableStorageIsUnavailable()
    {
        // The single rule that matters most. A 250 here transfers responsibility for a message we
        // could not store, and the client deletes its copy — the mail is destroyed by our success.
        var sink = new TestIngressSink(_ => IngressDecision.Defer("The spool could not be written."));
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        await client.SendAsync($"RCPT TO:<user@{LocalDomain}>");
        var verdict = await client.SendMessageAsync(Message());

        Assert.Equal(451, verdict.Code);
        Assert.True(verdict.IsTransientFailure);
        Assert.NotEqual(250, verdict.Code);
    }

    [Fact]
    public async Task DefersRatherThanAcceptingWhenTheSinkFailsUnexpectedly()
    {
        // Defence in depth: a sink that throws an unclassified exception must still not produce a
        // 250. Deferring leaves the message with the client, which is always recoverable.
        var sink = new TestIngressSink { ThrowOnSubmit = new IOException("the spool volume vanished") };
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        await client.SendAsync($"RCPT TO:<user@{LocalDomain}>");
        var verdict = await client.SendMessageAsync(Message());

        Assert.Equal(451, verdict.Code);
        Assert.NotEqual(250, verdict.Code);
    }

    [Fact]
    public async Task RefusesAMessageThatHasReachedTheHopLimit()
    {
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(maxHops: 5), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        await client.SendAsync($"RCPT TO:<user@{LocalDomain}>");
        var verdict = await client.SendMessageAsync(Message(receivedHeaders: 5));

        Assert.Equal(554, verdict.Code);
        Assert.Contains("Too many hops", verdict.Text, StringComparison.Ordinal);
        Assert.Empty(sink.Submissions);
    }

    [Fact]
    public async Task RefusesAMessageThatHasAlreadyBeenThroughUs()
    {
        var message = Encoding.UTF8.GetBytes(
            $"Received: from relay.example.net by {OurName} with ESMTP id abc123\r\n"
            + "From: sender@example.net\r\n"
            + "\r\n"
            + "body\r\n");

        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        await client.SendAsync($"RCPT TO:<user@{LocalDomain}>");
        var verdict = await client.SendMessageAsync(message);

        Assert.Equal(554, verdict.Code);
        Assert.Contains("loop", verdict.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(sink.Submissions);
    }

    [Fact]
    public async Task DoesNotMistakeOurNameInAForClauseForALoop()
    {
        // Every message a correspondent's server sends us carries our own domain in a "for" clause.
        // Matching anywhere in the Received value rather than only in the "by" clause would declare
        // a loop on essentially all ordinary inbound mail.
        var message = Encoding.UTF8.GetBytes(
            $"Received: from relay.example.net by mta.example.net with ESMTP for <user@{OurName}>\r\n"
            + "From: sender@example.net\r\n"
            + "\r\n"
            + "body\r\n");

        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        await client.SendAsync($"RCPT TO:<user@{LocalDomain}>");
        var verdict = await client.SendMessageAsync(message);

        Assert.Equal(250, verdict.Code);
        Assert.Single(sink.Submissions);
    }

    [Fact]
    public async Task AuthenticationIsRefusedOnAnUnencryptedConnection()
    {
        // Credentials travel inside the SMTP stream, so a plaintext session hands them to anyone on
        // the path. The listener refuses before reading the payload, whether or not AUTH was offered.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(
            Options(),
            sink,
            new AlwaysAuthenticatingAuthenticator());
        listener.Start();

        await using var client = await TestSmtpClient.ConnectAsync(listener.BoundPort);
        await client.ReadReplyAsync();
        var ehlo = await client.SendAsync("EHLO test.client");

        // Not advertised before STARTTLS either — offering it would invite a credential we would
        // then refuse, and the refusal would still have disclosed the mechanism.
        Assert.DoesNotContain(ehlo.Lines, l => l.Contains("AUTH", StringComparison.Ordinal));

        var auth = await client.AuthenticatePlainAsync("stylomail", "secret");

        Assert.Equal(538, auth.Code);
    }

    [Fact]
    public async Task AuthenticatedSubmissionAcceptsAnAuthorisedSenderIdentity()
    {
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(
            Options(), sink, new AlwaysAuthenticatingAuthenticator(["alice@example.test"]));
        listener.Start();

        var (client, ehlo) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        Assert.Contains(ehlo.Lines, l => l.Contains("AUTH PLAIN LOGIN", StringComparison.Ordinal));

        Assert.Equal(235, (await client.AuthenticatePlainAsync("alice", "secret")).Code);
        Assert.Equal(250, (await client.SendAsync("MAIL FROM:<alice@example.test>")).Code);

        // An authenticated principal is not subject to the inbound recipient-domain check: this is
        // outbound mail, and its destination is by definition elsewhere.
        Assert.Equal(250, (await client.SendAsync("RCPT TO:<someone@far.example>")).Code);

        var verdict = await client.SendMessageAsync(Message());
        Assert.Equal(250, verdict.Code);

        var submission = Assert.Single(sink.Submissions);
        Assert.Equal(MailDirection.Outbound, submission.Direction);
        Assert.Equal("p_alice", submission.TrustedPrincipalId);
    }

    [Fact]
    public async Task AuthenticatedSubmissionRefusesAnUnauthorisedSenderIdentity()
    {
        // Authenticating proves who you are, not that you may claim any sender. Without this every
        // valid account would be a forgery primitive.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(
            Options(), sink, new AlwaysAuthenticatingAuthenticator(["alice@example.test"]));
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.AuthenticatePlainAsync("alice", "secret");
        var mail = await client.SendAsync("MAIL FROM:<ceo@victim.example>");

        Assert.Equal(553, mail.Code);
        Assert.Empty(sink.Submissions);
    }

    [Theory]
    [InlineData("MAIL FROM:<>")]
    [InlineData("MAIL FROM:< >")]
    [InlineData("MAIL FROM: <  > ")]
    [InlineData("MAIL FROM:<> SIZE=1000")]
    public async Task AnAuthenticatedSubmissionCannotUseTheNullSender(string command)
    {
        // `<>` means "this is a DSN", and we do not originate bounces — a permanent failure is
        // recorded and left to the upstream MTA's DSN policy. Permitting it here is exactly the rule
        // that lets a bounce be generated on someone else's behalf.
        //
        // Every spelling is covered rather than the canonical one, because `queue-` found the same
        // class of hole in their own validation: their check rejected empty and whitespace values
        // but let the literal wire form `<>` through, so the guard read as present and did not fire.
        // A guard tested on one spelling is a guard tested on the spelling its author was thinking
        // of.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(
            Options(), sink, new AlwaysAuthenticatingAuthenticator(["alice@example.test"]));
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.AuthenticatePlainAsync("alice", "secret");
        var mail = await client.SendAsync(command);

        Assert.Equal(553, mail.Code);
        Assert.Empty(sink.Submissions);
    }

    [Fact]
    public async Task AnUnauthenticatedInboundDsnWithANullSenderIsStillAccepted()
    {
        // The other half of the ruling, and the reason the refusal is scoped to the submission path:
        // a DSN being *delivered to* a mailbox arrives unauthenticated from outside and must reach
        // us. Refusing it here would break legitimate bounces, which is a guard catching too much.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        Assert.Equal(250, (await client.SendAsync("MAIL FROM:<>")).Code);
        Assert.Equal(250, (await client.SendAsync($"RCPT TO:<user@{LocalDomain}>")).Code);

        var verdict = await client.SendMessageAsync(Message());
        Assert.Equal(250, verdict.Code);

        var submission = Assert.Single(sink.Submissions);
        Assert.Equal(MailDirection.Inbound, submission.Direction);
        Assert.Equal(string.Empty, submission.MailFrom);
    }

    [Fact]
    public void MaySendAsRefusesTheNullSenderAndBlankApprovedEntries()
    {
        var principal = new AuthenticatedPrincipal
        {
            PrincipalId = "p1",
            TenantId = "t1",
            ApprovedSenderIdentities = ["alice@example.test", "   "],
        };

        Assert.True(principal.MaySendAs("alice@example.test"));
        Assert.True(principal.MaySendAs("ALICE@EXAMPLE.TEST"));
        Assert.False(principal.MaySendAs("bob@example.test"));

        // A stray blank in a configured list must not silently re-permit the null sender.
        Assert.False(principal.MaySendAs(string.Empty));
    }

    [Fact]
    public async Task BadCredentialsAreRefusedAndBounded()
    {
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(
            Options() with { MaxAuthAttempts = 2 }, sink, new AlwaysAuthenticatingAuthenticator());
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        // The authenticator here only accepts the password "secret"; anything else is a failure.
        Assert.Equal(535, (await client.AuthenticatePlainAsync("alice", "wrong")).Code);

        // The second failure hits the bound and the connection is closed rather than left open to
        // keep guessing against.
        var final = await client.AuthenticatePlainAsync("alice", "wrong2");

        Assert.Equal(421, final.Code);
    }

    [Fact]
    public async Task InboundIsRefusedEntirelyWhenUnauthenticatedInboundIsDisabled()
    {
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(
            Options(allowUnauthenticatedInbound: false), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        var mail = await client.SendAsync("MAIL FROM:<sender@example.net>");

        Assert.Equal(530, mail.Code);
        Assert.Contains("Authentication required", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandsBeforeHeloAreRefused()
    {
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        // Driven by hand rather than through the helper, which greets again after the handshake.
        // The point is that STARTTLS discards the pre-handshake session, so a client that does not
        // re-greet is not treated as though it had.
        await using var client = await TestSmtpClient.ConnectAsync(listener.BoundPort);
        await client.ReadReplyAsync();
        await client.SendAsync("EHLO test.client");
        await client.StartTlsAsync();

        var afterTls = await client.SendAsync("MAIL FROM:<sender@example.net>");
        Assert.Equal(503, afterTls.Code);
        Assert.Contains("EHLO", afterTls.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommandsAreRefusedRatherThanIgnored()
    {
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        Assert.Equal(500, (await client.SendAsync("FROBNICATE now")).Code);
    }

    [Fact]
    public async Task VerifiedProvenanceIsReportedAsIncompleteRatherThanClean()
    {
        // The listener is downstream of the MTA that saw the client connection, so it has neither
        // the connecting address nor the domain's SPF policy. Recording provenance as complete
        // would let downstream evidence treat an unverified message as though it had been checked.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        await client.SendAsync($"RCPT TO:<user@{LocalDomain}>");
        await client.SendMessageAsync(Message());

        var submission = Assert.Single(sink.Submissions);
        Assert.True(submission.Authentication.ProvenanceIncomplete);
        Assert.Empty(submission.Authentication.Results);
        Assert.Null(submission.Authentication.ConnectingIp);
    }

    [Fact]
    public async Task ExactlyOneHopMarkerIsPrependedAndNothingElseChanges()
    {
        // The precise property, stated so it cannot widen unnoticed: one Received line, then the
        // original bytes untouched — body, headers, order and all.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        var payload = Message(body: "a line starting with . and a dot\r\n.hidden");

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        await client.SendAsync($"RCPT TO:<user@{LocalDomain}>");
        await client.SendMessageAsync(payload);

        var submission = Assert.Single(sink.Submissions);
        var (marker, remainder) = SmtpTestRig.SplitLeadingHopMarker(submission.RawMessage.ToArray());

        Assert.StartsWith("Received: ", marker, StringComparison.Ordinal);
        Assert.Equal(payload, remainder);

        // Exactly one, not two: a message that marks our hop twice would double-count it for every
        // downstream reader and for our own loop guard.
        Assert.DoesNotContain("\r\nReceived:", Encoding.UTF8.GetString(remainder), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHopMarkerNamesUsAndTheConnectingClient()
    {
        // The `by` clause is what the loop guard matches on, so it must carry the name the listener
        // is configured under — not a generic label, or our own hop coming back is unrecognisable.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        await client.SendAsync($"RCPT TO:<user@{LocalDomain}>");
        await client.SendMessageAsync(Message());

        var (marker, _) = SmtpTestRig.SplitLeadingHopMarker(
            Assert.Single(sink.Submissions).RawMessage.ToArray());

        Assert.Contains($"by {OurName}", marker, StringComparison.Ordinal);
        Assert.Contains("with ESMTP", marker, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", marker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostileClientNameCannotForgeAnExtraHeader()
    {
        // The EHLO argument is attacker-supplied and is interpolated into a header placed at the top
        // of the stored message — the highest-value injection point in either ingress.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var payload = Message();

        await using var client = await TestSmtpClient.ConnectAsync(listener.BoundPort);
        await client.ReadReplyAsync();
        await client.SendAsync("EHLO nasty.invalid");

        // A single EHLO argument stuffed with every structural character in the grammar. Sent as one
        // line, because CR/LF cannot survive the line reader — the characters that can do damage are
        // the ones that reframe the value without ending the line.
        await client.StartTlsAsync();
        Assert.Equal(250, (await client.SendAsync("EHLO a(b)c;<injected@evil.test>")).Code);

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        await client.SendAsync($"RCPT TO:<user@{LocalDomain}>");
        await client.SendMessageAsync(payload);

        var submission = Assert.Single(sink.Submissions);
        var (marker, remainder) = SmtpTestRig.SplitLeadingHopMarker(submission.RawMessage.ToArray());

        // Structural characters are replaced rather than escaped: a value drawn from a restricted
        // alphabet cannot close a comment, open a clause, or terminate the header. Angle brackets
        // and the raw hostile token are checked here; the parens and semicolons the grammar itself
        // requires are asserted separately by the token tests.
        Assert.DoesNotContain("<", marker, StringComparison.Ordinal);
        Assert.DoesNotContain(">", marker, StringComparison.Ordinal);
        Assert.DoesNotContain("a(b)c", marker, StringComparison.Ordinal);

        // And the clauses we control still parse, which is what proves nothing was injected.
        Assert.Contains($"by {OurName}", marker, StringComparison.Ordinal);
        Assert.Contains("with ESMTP id", marker, StringComparison.Ordinal);

        // Nothing reached the message itself either.
        Assert.Equal(payload, remainder);
    }

    [Fact]
    public async Task TheHopMarkerNeverCarriesARecipientAddress()
    {
        // A `Received` header is stored, forwarded to every recipient, and often archived by third
        // parties — so naming one recipient in it discloses that recipient to all the others. That
        // is a privacy breach manufactured by a diagnostic convenience, which is why the `for`
        // clause is optional in the grammar and is deliberately not written.
        //
        // Pinned because this is exactly the kind of property that gets "improved" back in later by
        // someone making the header more conventional.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        await client.SendAsync($"RCPT TO:<alice@{LocalDomain}>");
        await client.SendAsync($"RCPT TO:<bob@{LocalDomain}>");
        await client.SendMessageAsync(Message());

        var submission = Assert.Single(sink.Submissions);
        Assert.Equal(2, submission.Recipients.Count);

        var (marker, _) = SmtpTestRig.SplitLeadingHopMarker(submission.RawMessage.ToArray());

        Assert.DoesNotContain($"alice@{LocalDomain}", marker, StringComparison.Ordinal);
        Assert.DoesNotContain($"bob@{LocalDomain}", marker, StringComparison.Ordinal);
        Assert.DoesNotContain("for <", marker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReceivedHeaderTheSinkSeesIsTheStampedOne()
    {
        // HopCount is counted over what arrived, before our marker is added, so a message with two
        // hops still reports two — not three because we counted our own line.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var (client, _) = await OpenEncryptedAsync(listener.BoundPort);
        await using var _1 = client;

        await client.SendAsync("MAIL FROM:<sender@example.net>");
        await client.SendAsync($"RCPT TO:<user@{LocalDomain}>");
        await client.SendMessageAsync(Message(receivedHeaders: 2));

        var submission = Assert.Single(sink.Submissions);

        // What arrived, counted before our marker was added...
        Assert.Equal(2, submission.HopCount);

        // ...and what is now stored, which is that plus ours. This is the visibility the loop guard
        // depends on: before this change our hop was nowhere in the artefact at all.
        var stored = TransportHeaderScanner.Scan(submission.RawMessage.Span, new TransportHeaderLimits(), []);
        Assert.Equal(3, stored.ReceivedCount);
    }

    // ---- Shutdown ------------------------------------------------------------------------------
    //
    // The ordinary host-shutdown path is stop-then-dispose: IHostedService calls StopAsync, then the
    // host disposes. My suite only ever used `await using`, which reaches DisposeAsync as the single
    // entry point — so it never exercised the pairing at all, and the defect below was invisible to
    // a suite that looked thorough. Reported by ingress-, who hosted it and hit it.

    [Fact]
    public async Task StoppingThenDisposingWithAClientAttachedDoesNotThrow()
    {
        // The bug: StopAsync nulled the listener BEFORE awaiting the drain, so a second caller —
        // which DisposeAsync always is on this path — returned immediately, disposed the connection
        // semaphore, and the still-draining session's `finally { Release(); }` threw
        // ObjectDisposedException out of the first caller's Task.WhenAll.
        var sink = new TestIngressSink();
        var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        // A client left parked mid-session, so there is genuinely something in flight to drain.
        await using var client = await TestSmtpClient.ConnectAsync(listener.BoundPort);
        await client.ReadReplyAsync();
        await client.SendAsync("EHLO test.client");

        var stopping = listener.StopAsync();

        // Dispose while the first stop is still draining — the exact overlap that used to race.
        await listener.DisposeAsync();

        await stopping;
    }

    [Fact]
    public async Task ConcurrentStopCallersShareTheSameDrain()
    {
        // The property the fix rests on, asserted directly rather than through a race: a second
        // caller must join the drain, not run a no-op beside it. If StopAsync ever goes back to
        // returning early on a null listener, this fails deterministically instead of flaking.
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        var first = listener.StopAsync();
        var second = listener.StopAsync();

        Assert.Same(first, second);

        await first;
    }

    [Fact]
    public async Task StopIsRepeatableAndDisposeAfterItIsSafe()
    {
        var sink = new TestIngressSink();
        await using var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        await listener.StopAsync();
        await listener.StopAsync();
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task AClientParkedMidTransactionIsDrainedRatherThanCutOff()
    {
        // The property the original comment claimed and DisposeAsync broke: in-flight sessions are
        // waited for, because interrupting one between the queue commit and the 250 would leave a
        // message accepted and never acknowledged.
        var sink = new TestIngressSink();
        var listener = new SmtpSubmissionListener(Options(), sink);
        listener.Start();

        await using var client = await TestSmtpClient.ConnectAsync(listener.BoundPort);
        await client.ReadReplyAsync();
        await client.SendAsync("EHLO test.client");

        await listener.StopAsync();

        // The session ran to completion rather than being abandoned mid-conversation.
        Assert.Empty(sink.Submissions);
        await listener.DisposeAsync();
    }

    private sealed class AlwaysAuthenticatingAuthenticator : ISubmissionAuthenticator
    {
        private readonly IReadOnlyList<string> _identities;

        public AlwaysAuthenticatingAuthenticator(IReadOnlyList<string>? identities = null) =>
            _identities = identities ?? [];

        public ValueTask<AuthenticatedPrincipal?> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            if (password != "secret")
            {
                return ValueTask.FromResult<AuthenticatedPrincipal?>(null);
            }

            return ValueTask.FromResult<AuthenticatedPrincipal?>(new AuthenticatedPrincipal
            {
                PrincipalId = $"p_{username}",
                TenantId = "t_1",
                ApprovedSenderIdentities = _identities,
            });
        }
    }
}
