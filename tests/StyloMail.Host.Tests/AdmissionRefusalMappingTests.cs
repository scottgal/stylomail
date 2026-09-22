using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Hosting;
using StyloMail.Queue;
using StyloMail.Transport.Ingress;

namespace StyloMail.Host.Tests;

/// <summary>
/// A refusal from the queue must reach the client as a refusal, on every edge the Host owns.
/// </summary>
/// <remarks>
/// <para>
/// Raised by `overview-`, who expected the Host to have a <c>QueueAdmission</c> switch that needed the
/// new <c>RefusedNullSender = 7</c> member added to it, and warned that "an unmodelled admission value
/// falling through a default is how a refusal becomes a 202".
/// </para>
/// <para>
/// <b>There is no such switch, and that is the better answer.</b> The Host never reads
/// <c>QueueAdmission</c> — it reads <c>MailAssessment.SubmissionId</c> and <c>MailAction</c>, and its
/// refresh of the queue's vocabulary stops at the port. So a new enum member cannot change an outcome
/// here, and the fragility the warning was aimed at does not exist to be fixed.
/// </para>
/// <para>
/// That is a claim about the code's shape, which is exactly the kind that turns out to be about a
/// different version of the code. These tests therefore drive a real <c>RefusedNullSender</c> from the
/// real <c>QueueStore</c> out through both edges the Host exposes, and assert the outcome is a refusal
/// — so if anyone ever does introduce an admission switch, a bad default fails here rather than in
/// someone's mail.
/// </para>
/// </remarks>
public sealed class AdmissionRefusalMappingTests
{
    [Fact]
    public async Task An_outbound_null_sender_is_refused_at_the_http_edge_and_never_a_2xx()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/submissions")
        {
            Content = JsonContent.Create(TestMessages.Request(mailFrom: string.Empty)),
        };

        request.Headers.Add("Idempotency-Key", "key-null-sender");

        var response = await client.SendAsync(request);

        // The queue refused it — `QueueAdmission.RefusedNullSender` — so no durable row exists and
        // nothing may answer 2xx. The exact code is the route's business; that it is a refusal is not.
        Assert.False(
            response.IsSuccessStatusCode,
            $"A refused submission answered {(int)response.StatusCode}. A 2xx here tells the caller "
            + "their mail was accepted when nothing was stored.");

        var counts = await host.Services.GetRequiredService<QueueStore>()
            .CountByStateAsync(TestPrincipals.AcmeTenant);

        Assert.Empty(counts);
    }

    [Fact]
    public async Task An_outbound_null_sender_is_refused_at_the_ingress_edge_and_never_acknowledged()
    {
        // The same refusal down the other edge the Host owns. A 250 here is worse than a wrong HTTP
        // status: the client deletes its copy.
        using var host = new TestHost().CountingSubmissions();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        var decision = await sink.SubmitAsync(
            new IngressSubmission
            {
                InternalMessageId = "msg_null_sender",
                TenantId = TestPrincipals.AcmeTenant,
                Direction = MailDirection.Outbound,
                TrustedPrincipalId = TestPrincipals.AcmeSenderPrincipal,
                MailFrom = string.Empty,
                Recipients = ["recipient@example.com"],
                RawMessage = System.Text.Encoding.UTF8.GetBytes(
                    TestMessages.SampleMime.Replace("\r\n", "\n").Replace("\n", "\r\n")),
                Authentication = new AuthenticationContext
                {
                    ConnectingIp = null,
                    AuthenticatedAccount = TestPrincipals.AcmeSenderPrincipal,
                    Results = [],
                    ApprovedSenderIdentities = [],
                    ProvenanceIncomplete = true,
                },
                HopCount = 0,
                UntrustedMessageIdHeader = null,
            },
            CancellationToken.None);

        Assert.NotEqual(IngressOutcome.Accepted, decision.Outcome);
        Assert.NotEqual(250, decision.ReplyCode);
        Assert.Null(decision.QueueId);
        Assert.True(decision.IsAcceptanceValid);

        // The queue *was* consulted — once — and refused. The count is an attempt count, not an
        // acceptance count, which is why the claim that nothing was stored is made against the queue
        // rather than against this number.
        Assert.Equal(1, host.Submissions.AcceptAttempts);

        var counts = await host.Services.GetRequiredService<QueueStore>()
            .CountByStateAsync(TestPrincipals.AcmeTenant);

        Assert.Empty(counts);
    }
}
