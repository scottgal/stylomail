using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Hosting;
using StyloMail.Queue;
using StyloMail.Transport.Ingress;

namespace StyloMail.Host.Tests;

/// <summary>
/// The ingress sink: translating a message an ingress has read into an assessment, and the
/// assessment into an SMTP reply.
/// </summary>
/// <remarks>
/// <para>
/// The property under test throughout is the same one the queue imposes on itself, <b>a <c>250</c>
/// means a durable queue row exists</b>, and most of these tests are ways of trying to make the
/// sink answer <c>250</c> without one. A client that reads a <c>250</c> deletes its copy, so getting
/// this wrong destroys mail; a deferral costs a retry.
/// </para>
/// <para>
/// The other property, and the one that cost this project a day of duplicate deliveries, is that
/// <b>the sink does not accept</b>. The assessor does. These tests count what reaches durable
/// acceptance, because two accepts under two different idempotency keys are invisible in the
/// outcome: both produce a queue row, and the queue cannot see the two as one submission.
/// </para>
/// </remarks>
public sealed class IngressSinkTests
{
    [Fact]
    public async Task An_ingress_message_is_accepted_only_alongside_a_durable_queue_row()
    {
        using var host = new TestHost().CountingSubmissions();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        var decision = await sink.SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(IngressOutcome.Accepted, decision.Outcome);
        Assert.Equal(250, decision.ReplyCode);

        // Acceptance is a queue id, not a boolean, and the id is only meaningful if it addresses a
        // row that actually exists.
        var queueId = Assert.IsType<string>(decision.QueueId);
        var store = host.Services.GetRequiredService<QueueStore>();
        var item = await store.GetItemAsync(queueId, TestPrincipals.AcmeTenant);

        Assert.NotNull(item);
        Assert.Equal(TestPrincipals.AcmeTenant, item!.TenantId);

        // Exactly once. This is the assertion that would have caught the seam defect: the sink
        // running the pipeline *and* accepting produces two rows under two keys, each of which looks
        // correct from the call site that made it.
        Assert.Equal(1, host.Submissions.AcceptAttempts);
    }

    [Fact]
    public async Task The_bytes_the_queue_holds_are_the_bytes_the_ingress_handed_over()
    {
        // Preservation is the whole reason the original bytes are spooled at all: rewriting them
        // would break the DKIM signature the message arrived under, and a proxy that quietly
        // rewrites mail is a proxy that breaks a guarantee it was deployed to uphold.
        using var host = new TestHost();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();
        var raw = RawMime();

        var decision = await sink.SubmitAsync(Submission(), CancellationToken.None);

        var store = host.Services.GetRequiredService<QueueStore>();
        var item = await store.GetItemAsync(decision.QueueId!, TestPrincipals.AcmeTenant);
        Assert.NotNull(item);

        await using var payload = store.OpenPayload(item!);
        using var buffer = new MemoryStream();
        await payload.CopyToAsync(buffer);

        Assert.Equal(raw, buffer.ToArray());
    }

    [Fact]
    public async Task An_ingress_message_is_never_assessment_only_and_carries_no_invented_key()
    {
        // The two facts the pipeline's step seven depends on. AssessmentOnly would mean the
        // acceptance step never ran; a minted idempotency key would look like replay protection
        // while providing none, because a genuine retry would carry a different one.
        using var host = new TestHost();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        await sink.SubmitAsync(Submission(), CancellationToken.None);

        var call = Assert.Single(host.Assessor.Calls);

        Assert.False(call.Context.AssessmentOnly);
        Assert.False(call.Context.ShadowMode);
        Assert.Null(call.Context.ClientIdempotencyKey);
        Assert.Equal(TestPrincipals.AcmeTenant, call.Context.TenantId);

        // And the reference the pipeline will resolve and later insist is durable for acceptance.
        Assert.True(PayloadReferences.IsDurable(call.Input.Envelope.PayloadReference));
        Assert.True(
            host.Services.GetRequiredService<SpoolStore>().Exists(call.Input.Envelope.PayloadReference),
            "The payload reference handed to the assessor names no stored payload, so every "
            + "submission would defer for a reason that looks like a storage fault.");
    }

    [Fact]
    public async Task Identity_comes_from_the_boundary_and_not_from_the_message()
    {
        // The From header in the sample message says sender@example.com. Nothing downstream may
        // learn anything from it: for inbound mail the identity is the connector's, and the
        // direction is what the boundary observed rather than what the message claims.
        using var host = new TestHost();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        await sink.SubmitAsync(
            Submission(direction: MailDirection.Inbound, trustedPrincipalId: "cloudflare-email-routing"),
            CancellationToken.None);

        var call = Assert.Single(host.Assessor.Calls);

        Assert.Equal("cloudflare-email-routing", call.Input.Envelope.TrustedPrincipalId);
        Assert.Equal(MailDirection.Inbound, call.Input.Envelope.Direction);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(19)]
    public async Task The_hop_count_an_ingress_observed_reaches_the_envelope_the_pipeline_sees(int hopCount)
    {
        // The last link in a chain that has already been inert once, and whose fix is a single
        // assignment with no visible consequence, which is exactly why its absence would go
        // unnoticed a second time. Without this, deleting `HopCount = submission.HopCount` from the
        // sink would leave every other test green while the mail-loop backstop silently stopped
        // firing again.
        //
        // Zero is a value and not an absence, which is why it is a case here rather than the
        // default: `MailEnvelope.HopCount` is nullable precisely so a sink that did not look can say
        // so. Reporting null from this sink would be a false claim about its own behaviour, not a
        // safe default.
        using var host = new TestHost();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        await sink.SubmitAsync(Submission(hopCount: hopCount), CancellationToken.None);

        var call = Assert.Single(host.Assessor.Calls);

        Assert.Equal(hopCount, call.Input.Envelope.HopCount);
    }

    [Fact]
    public async Task Two_messages_without_a_client_key_are_two_deliveries()
    {
        // The honest counterpart of the HTTP replay tests. An SMTP session supplies no key, and the
        // sink must not synthesise one to make this look protected: a duplicate after a lost
        // acknowledgement is the ambiguity the spec accepts, not something to paper over.
        using var host = new TestHost().CountingSubmissions();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        var first = await sink.SubmitAsync(Submission(internalMessageId: "msg_one"), CancellationToken.None);
        var second = await sink.SubmitAsync(Submission(internalMessageId: "msg_two"), CancellationToken.None);

        Assert.Equal(IngressOutcome.Accepted, first.Outcome);
        Assert.Equal(IngressOutcome.Accepted, second.Outcome);
        Assert.NotEqual(first.QueueId, second.QueueId);
        Assert.Equal(2, host.Submissions.AcceptAttempts);
    }

    [Fact]
    public async Task A_repeated_message_identifier_is_deferred_rather_than_overwriting_the_first()
    {
        // The ingress spool name is derived from the message id, and the spool refuses to overwrite.
        // Two different messages under one id cannot happen from either ingress, both mint a fresh
        // identifier per transaction, so if it does, the safe answer is to decline rather than to
        // let the second message take the first one's place on disk.
        using var host = new TestHost().CountingSubmissions();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        var first = await sink.SubmitAsync(Submission(), CancellationToken.None);
        var second = await sink.SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(IngressOutcome.Accepted, first.Outcome);
        Assert.Equal(IngressOutcome.Deferred, second.Outcome);
        Assert.Equal(1, host.Submissions.AcceptAttempts);
    }

    [Theory]
    [InlineData(MailAction.Defer, IngressOutcome.Deferred, 451)]
    [InlineData(MailAction.Reject, IngressOutcome.Rejected, 550)]
    public async Task A_declined_responsibility_is_never_reported_as_accepted(
        MailAction action,
        IngressOutcome expected,
        int expectedCode)
    {
        using var host = new TestHost();
        host.Assessor.Action = action;
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        var decision = await sink.SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(expected, decision.Outcome);
        Assert.Equal(expectedCode, decision.ReplyCode);
        Assert.Null(decision.QueueId);
        Assert.True(decision.IsAcceptanceValid);

        var counts = await host.Services.GetRequiredService<QueueStore>()
            .CountByStateAsync(TestPrincipals.AcmeTenant);
        Assert.Empty(counts);
    }

    [Fact]
    public async Task An_allowed_message_the_pipeline_could_not_accept_is_deferred_not_acknowledged()
    {
        // The case that matters most, because it is the one where policy's answer and the durable
        // outcome disagree. Policy said Allow; acceptance did not happen. Reporting the action would
        // claim a delivery state that does not exist, and a 250 would tell the client to delete mail
        // we never stored.
        using var host = new TestHost();
        host.Assessor.AcceptanceRefused = true;
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        var decision = await sink.SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(IngressOutcome.Deferred, decision.Outcome);
        Assert.NotEqual(250, decision.ReplyCode);
        Assert.Null(decision.QueueId);
    }

    [Fact]
    public async Task An_unconfigured_assessor_defers_rather_than_throwing()
    {
        // A deployment with no Assessment configuration is a legitimate shape, and the correct
        // answer to a message is a temporary failure, the client keeps its copy and tries later.
        // An exception reaching the listener would work too, but only by accident of the listener
        // catching everything; being explicit means the intent is on the record.
        using var host = new TestHost().WithoutAssessor();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        var decision = await sink.SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(IngressOutcome.Deferred, decision.Outcome);
        Assert.Null(decision.QueueId);
    }

    [Fact]
    public async Task A_message_that_cannot_be_spooled_is_declined_before_anything_is_assessed()
    {
        // Disk full, an unmounted volume, a permissions change. Accepting here would destroy mail we
        // cannot produce, so the deferral happens before the assessment is even attempted, which
        // the assessor-invocation count is what proves.
        var root = Path.Combine(Path.GetTempPath(), "stylomail-sink-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var spool = new SpoolStore(root);

            // A regular file where the per-tenant directory has to go. Deterministic, unlike making
            // a directory unwritable, which depends on the user the suite runs as.
            File.WriteAllText(Path.Combine(root, TestPrincipals.AcmeTenant), "not a directory");

            var assessor = new RecordingAssessor();
            var sink = new HostIngressSink(assessor, spool);

            var decision = await sink.SubmitAsync(Submission(), CancellationToken.None);

            Assert.Equal(IngressOutcome.Deferred, decision.Outcome);
            Assert.Null(decision.QueueId);
            Assert.Equal(0, assessor.CallCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_decline_reaches_the_client_as_a_reason_code_and_not_as_internal_prose()
    {
        // Reason messages are written for an operator reading the decision ledger and are free to
        // name a spool path, a tenant or a component. This one goes out over a socket to whoever is
        // on the other end of it, so it carries the stable identifier and nothing else.
        using var host = new TestHost();
        host.Assessor.Action = MailAction.Defer;
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        var decision = await sink.SubmitAsync(Submission(), CancellationToken.None);

        var reason = Assert.IsType<string>(decision.Reason);
        Assert.Contains("assessment.acceptance_refused", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic decision", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_accepted_message_leaves_two_payloads_on_the_spool()
    {
        // Not a bug, and worth seeing. The ingress copy is what the pipeline reads the message back
        // from; the queue then writes its own copy under the queue id it mints, and that second
        // write *is* the acceptance. The ingress copy is referenced by no queue metadata, so the
        // orphan sweep collects it after its minimum age.
        //
        // Deleting it as soon as acceptance succeeds is the remaining work, and deliberately not
        // done here, two questions are still open with queue- about whether the roots are shared
        // and what the measured peak is. This test states the current cost plainly so that the
        // change which removes it has something to fail against.
        using var host = new TestHost();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();
        var raw = RawMime();

        await sink.SubmitAsync(Submission(), CancellationToken.None);

        var spool = host.Services.GetRequiredService<SpoolStore>();
        var payloads = Directory.EnumerateFiles(spool.Root, "*.eml", SearchOption.AllDirectories).ToList();

        Assert.Equal(2, payloads.Count);
        Assert.Contains(payloads, path => Path.GetFileName(path).StartsWith("ingress-", StringComparison.Ordinal));
        Assert.All(payloads, path => Assert.Equal(raw.Length, new FileInfo(path).Length));
    }

    private static IngressSubmission Submission(
        string internalMessageId = "msg_ingress_1",
        MailDirection direction = MailDirection.Outbound,
        string trustedPrincipalId = TestPrincipals.AcmeSenderPrincipal,
        int hopCount = 0)
    {
        var raw = RawMime();

        return new IngressSubmission
        {
            InternalMessageId = internalMessageId,
            TenantId = TestPrincipals.AcmeTenant,
            Direction = direction,
            TrustedPrincipalId = trustedPrincipalId,
            MailFrom = "sender@example.com",
            Recipients = ["recipient@example.com"],
            RawMessage = raw,
            Authentication = new AuthenticationContext
            {
                ConnectingIp = null,
                AuthenticatedAccount = direction == MailDirection.Outbound ? trustedPrincipalId : null,
                Results = [],
                ApprovedSenderIdentities = [],
                ProvenanceIncomplete = true,
            },

            // The ingress's own observation, passed through rather than invented. Zero means "scanned
            // and found no prior hops", which is a different claim from "did not look", the
            // ingresses own that scan and refuse an over-limit message before ever calling here.
            HopCount = hopCount,
            UntrustedMessageIdHeader = "<abc123@example.com>",
        };
    }

    private static byte[] RawMime() =>
        Encoding.UTF8.GetBytes(TestMessages.SampleMime.Replace("\r\n", "\n").Replace("\n", "\r\n"));
}
