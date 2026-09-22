using System.Text;
using StyloMail.Core;
using StyloMail.Transport.Cloudflare;
using StyloMail.Transport.Ingress;
using StyloMail.Transport.Tests.Support;

namespace StyloMail.Transport.Tests;

/// <summary>
/// The Cloudflare path is an unauthenticated-by-default byte intake unless it is configured
/// properly, so most of these are about what it refuses.
/// </summary>
public sealed class CloudflareConnectorTests
{
    private const string Secret = "worker-shared-secret-value";
    private const string LocalDomain = "example.test";
    private const string OurName = "stylomail.example.test";

    private static CloudflareIngressOptions Options(
        string? secret = Secret,
        RecipientDomainPolicy? domains = null,
        int maxHops = 20) => new()
        {
            SharedSecret = secret,
            RecipientDomains = domains ?? new RecipientDomainPolicy([LocalDomain]),
            LocalHostIdentities = [OurName],
            MaxHops = maxHops,
            InboundTenantId = "t_inbound",
        };

    private static CloudflareIngressRequest Request(
        byte[]? body = null,
        string? to = null,
        string? from = "sender@example.net",
        string? authorization = "Bearer worker-shared-secret-value") => new()
        {
            Authorization = authorization,
            RawMessage = body ?? Message(),
            EnvelopeFrom = from,
            EnvelopeTo = to ?? $"user@{LocalDomain}",
        };

    private static byte[] Message(int receivedHeaders = 0, string extraHeaders = "")
    {
        var builder = new StringBuilder();
        for (var i = 0; i < receivedHeaders; i++)
        {
            builder.Append("Received: from relay").Append(i).Append(".example.net by mta")
                .Append(i).Append(".example.net with ESMTP\r\n");
        }

        builder.Append(extraHeaders);
        builder.Append("From: sender@example.net\r\n");
        builder.Append("To: user@").Append(LocalDomain).Append("\r\n");
        builder.Append("Subject: hello\r\n");
        builder.Append("Message-ID: <cf-1@example.net>\r\n");
        builder.Append("\r\n");
        builder.Append("a body\r\n");

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    [Fact]
    public async Task AcceptsWithTheCorrectCredentialAndAnswersWithTheQueueId()
    {
        var sink = new TestIngressSink(_ => IngressDecision.Accepted("q_cf_1"));
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);

        var result = await connector.IngestAsync(Request(), CancellationToken.None);

        // 202, like the HTTP submission path: an acknowledgement without the durable row it is a
        // claim about would be exactly the bug this whole rule exists to prevent.
        Assert.Equal(202, result.StatusCode);
        Assert.Equal("q_cf_1", result.QueueId);

        var submission = Assert.Single(sink.Submissions);
        Assert.Equal(MailDirection.Inbound, submission.Direction);
        Assert.Equal("cloudflare-email-routing", submission.TrustedPrincipalId);
        Assert.Equal($"user@{LocalDomain}", Assert.Single(submission.Recipients));
        Assert.Equal("t_inbound", submission.TenantId);
    }

    [Fact]
    public async Task TheDirectionIsAlwaysInboundBecauseThereIsNoFieldToSetIt()
    {
        // Recorded as an executable assertion rather than only a comment, because "inbound only" is
        // a security property of this connector: nothing here may become a send.
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);

        await connector.IngestAsync(Request(), CancellationToken.None);

        Assert.All(sink.Submissions, s => Assert.Equal(MailDirection.Inbound, s.Direction));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer wrong-secret")]
    [InlineData("wrong-secret")]
    [InlineData("Bearer worker-shared-secret-valu")]
    [InlineData("Bearer worker-shared-secret-valuex")]
    public async Task RefusesAMissingOrIncorrectCredential(string? authorization)
    {
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);

        var result = await connector.IngestAsync(
            Request(authorization: authorization), CancellationToken.None);

        Assert.Equal(401, result.StatusCode);
        Assert.Null(result.QueueId);
        Assert.Empty(sink.Submissions);
    }

    [Fact]
    public async Task WithNoSecretConfiguredNothingIsAccepted()
    {
        // An unconfigured accept path must not be an unauthenticated one.
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(Options(secret: null), sink);

        var result = await connector.IngestAsync(
            Request(authorization: null), CancellationToken.None);

        Assert.Equal(401, result.StatusCode);
        Assert.Empty(sink.Submissions);
    }

    [Fact]
    public async Task RefusesARecipientOutsideTheConfiguredDomains()
    {
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);

        var result = await connector.IngestAsync(
            Request(to: "victim@elsewhere.example"), CancellationToken.None);

        Assert.Equal(403, result.StatusCode);
        Assert.Empty(sink.Submissions);
    }

    [Fact]
    public async Task RefusesWhenNoRecipientDomainsAreConfigured()
    {
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(
            Options(domains: RecipientDomainPolicy.None), sink);

        var result = await connector.IngestAsync(Request(), CancellationToken.None);

        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task RefusesAMessageWithNoEnvelopeRecipient()
    {
        // Routing must come from the envelope, never from the To header: message content cannot
        // decide whether we are the destination for this address.
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);

        var result = await connector.IngestAsync(Request(to: " "), CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("envelope recipient", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefersRatherThanAcknowledgingWhenStorageIsUnavailable()
    {
        var sink = new TestIngressSink(_ => IngressDecision.Defer("The spool could not be written."));
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);

        var result = await connector.IngestAsync(Request(), CancellationToken.None);

        Assert.Equal(503, result.StatusCode);
        Assert.Null(result.QueueId);
        Assert.NotNull(result.RetryAfter);
    }

    [Fact]
    public async Task DefersWhenTheSinkFailsUnexpectedly()
    {
        var sink = new TestIngressSink { ThrowOnSubmit = new IOException("spool volume vanished") };
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);

        var result = await connector.IngestAsync(Request(), CancellationToken.None);

        Assert.Equal(503, result.StatusCode);
        Assert.Null(result.QueueId);
    }

    [Fact]
    public async Task NeverAcknowledgesAnAcceptanceThatNamesNoDurableRow()
    {
        // Hand-built rather than via the factory: the guard exists precisely for a sink that
        // constructs the record directly and forgets the queue id.
        var sink = new TestIngressSink(_ => new IngressDecision
        {
            Outcome = IngressOutcome.Accepted,
            ReplyCode = 250,
            EnhancedStatusCode = "2.0.0",
        });

        var connector = new CloudflareEmailRoutingConnector(Options(), sink);
        var result = await connector.IngestAsync(Request(), CancellationToken.None);

        Assert.Equal(503, result.StatusCode);
        Assert.Null(result.QueueId);
    }

    [Fact]
    public async Task RefusesAnOversizeMessage()
    {
        var sink = new TestIngressSink();
        // Below the size of the fixture message, so the bound is what refuses it.
        var options = Options() with { MaxMessageBytes = 64 };
        var connector = new CloudflareEmailRoutingConnector(options, sink);

        var result = await connector.IngestAsync(Request(body: Message()), CancellationToken.None);

        Assert.Equal(413, result.StatusCode);
        Assert.Empty(sink.Submissions);
    }

    [Fact]
    public async Task RefusesAMessageThatHasAlreadyBeenThroughUs()
    {
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);

        var result = await connector.IngestAsync(
            Request(body: Message(extraHeaders: $"Received: from a by {OurName} with ESMTP\r\n")),
            CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("loop", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(sink.Submissions);
    }

    [Fact]
    public async Task RefusesAMessageThatHasReachedTheHopLimit()
    {
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(Options(maxHops: 3), sink);

        var result = await connector.IngestAsync(
            Request(body: Message(receivedHeaders: 3)), CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("hops", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProvenanceIsRecordedAsIncomplete()
    {
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);

        await connector.IngestAsync(Request(), CancellationToken.None);

        var submission = Assert.Single(sink.Submissions);
        Assert.True(submission.Authentication.ProvenanceIncomplete);
        Assert.Empty(submission.Authentication.Results);
        Assert.Empty(submission.Authentication.ApprovedSenderIdentities);
    }

    [Fact]
    public async Task ExactlyOneHopMarkerIsPrependedAndNothingElseChanges()
    {
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);
        var payload = Message();

        await connector.IngestAsync(Request(body: payload), CancellationToken.None);

        var (marker, remainder) = SmtpTestRig.SplitLeadingHopMarker(
            Assert.Single(sink.Submissions).RawMessage.ToArray());

        Assert.StartsWith("Received: ", marker, StringComparison.Ordinal);
        Assert.Contains($"by {OurName}", marker, StringComparison.Ordinal);
        Assert.Contains("with HTTPS", marker, StringComparison.Ordinal);
        Assert.Equal(payload, remainder);
    }

    [Fact]
    public async Task TheHopMarkerClaimsNoOriginItCannotKnow()
    {
        // This connector has no connection, so it has no connecting host or address. Emitting a
        // `from` clause anyway would write a fabricated fact into the message's permanent trace,         // and that clause is exactly what a downstream reader would use to judge provenance.
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);

        await connector.IngestAsync(Request(), CancellationToken.None);

        var (marker, _) = SmtpTestRig.SplitLeadingHopMarker(
            Assert.Single(sink.Submissions).RawMessage.ToArray());

        Assert.DoesNotContain("from ", marker, StringComparison.Ordinal);
    }

    [Fact]
    public void AByHostTheLoopGuardCannotMatchIsRefusedAtConstruction()
    {
        // A `by` clause that is not one of the recognised local identities means our own hop coming
        // back is invisible to the loop guard, a silent failure discovered as a mail loop.
        var ex = Assert.Throws<InvalidOperationException>(() => new CloudflareEmailRoutingConnector(
            Options() with { ByHost = "some-other-name.example.test" },
            new TestIngressSink()));

        Assert.Contains("loop guard", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(200)]
    public void ARefusalCannotClaimToBeRetryableOrSuccessful(int statusCode)
    {
        // The host constructs these for requests it answers without consulting the connector, so the
        // factory is a public contract now and not just an internal convenience. A 5xx would tell the
        // Worker to re-offer a permanently refused message forever; a 2xx would claim acceptance for
        // something nothing accepted.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CloudflareIngressResult.Refused(statusCode, "nope"));
    }

    [Fact]
    public void AnAcceptanceCannotBeBuiltWithoutNamingItsQueueRow()
    {
        Assert.Throws<ArgumentException>(() => CloudflareIngressResult.Accepted("  "));
    }

    [Fact]
    public void ADeferralAlwaysCarriesARetryHint()
    {
        // A deferral that does not say when to come back invites either a hot retry loop or a
        // message the Worker never re-offers.
        var result = CloudflareIngressResult.Deferred("try later");

        Assert.Equal(503, result.StatusCode);
        Assert.Null(result.QueueId);
        Assert.NotNull(result.RetryAfter);
    }

    [Fact]
    public async Task TheSecretIsNeverRenderedIntoAResponse()
    {
        // The refusal reason travels back to a Worker and into its logs.
        var sink = new TestIngressSink();
        var connector = new CloudflareEmailRoutingConnector(Options(), sink);

        var result = await connector.IngestAsync(
            Request(authorization: "Bearer not-the-secret"), CancellationToken.None);

        Assert.DoesNotContain(Secret, result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-secret", result.Reason, StringComparison.Ordinal);
    }
}
