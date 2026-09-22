using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Hosting;
using StyloMail.Host.Traffic;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// The boundaries at which a change is announced, driven through the surface that produces it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every one of these drives the real thing and then asks a real console whether it heard.</b>
/// The routes, the ledger, the queue, the delivery worker and the readiness probe are all the
/// host's own instances, so a test here fails if an emission is dropped, if it is addressed
/// wrongly, or if the component it belongs to stopped producing the change at all.
/// </para>
/// <para>
/// <b>The emission sites are the four the seam is allowed to touch and no more.</b> Two of them are
/// in this file's ledger and delivery paths, which is where "the write is the completion boundary"
/// is decided: emitting from the pipeline instead would let the console be told about a decision
/// that was never recorded. Nothing here asks another component to call back.
/// </para>
/// </remarks>
public sealed class TrafficEmissionTests
{
    [Fact]
    public async Task An_assessment_is_announced_as_a_hint_that_names_the_decision()
    {
        using var host = TrafficSeamTests.WithTraffic();
        await using var console = await host.SubscribeTrafficAsync(TestPrincipals.AcmeReviewerKey);

        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);
        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var assessmentId = body.RootElement.GetProperty("assessmentId").GetString();

        var notice = await console.NextAsync();

        Assert.Equal("DecisionRecorded", notice.GetProperty("kind").GetString());

        // The identifier is the whole content of the event: it is what the console re-reads, and
        // what it re-reads contains the decision itself. A notice carrying the action would be the
        // state this seam is built not to push.
        Assert.Equal(assessmentId, notice.GetProperty("subjectId").GetString());
    }

    [Fact]
    public async Task A_released_quarantine_is_announced_as_a_change_to_that_message()
    {
        using var host = TrafficSeamTests.WithTraffic();

        // The submission records a decision, which is its own announcement. The console connects
        // afterwards so that what it hears here is the release and nothing else: a test that
        // subscribed first would be reading its way past the assessment to reach the event it
        // means to assert on, and would pass just as well if the release announced nothing.
        var queueId = await SubmitQuarantinedAsync(host, "msg-quarantine-announced");

        await using var console = await host.SubscribeTrafficAsync(TestPrincipals.AcmeReviewerKey);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var released = await reviewer.PostAsync($"/v1/quarantine/{queueId}/release", content: null);

        Assert.Equal(HttpStatusCode.OK, released.StatusCode);

        var notice = await console.NextAsync();

        Assert.Equal("MessageStateChanged", notice.GetProperty("kind").GetString());
        Assert.Equal(queueId, notice.GetProperty("subjectId").GetString());
    }

    [Fact]
    public async Task A_pause_and_a_resume_are_announced_as_the_same_kind_of_change()
    {
        // One kind for both directions, on purpose: which way it moved is state, the console
        // re-reads the sender, and a direction on this wire would be a second source of truth for
        // something already durable.
        using var host = TrafficSeamTests.WithTraffic();
        await using var console = await host.SubscribeTrafficAsync(TestPrincipals.AcmeReviewerKey);

        using var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var paused = await operatorClient.PostAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/pause", content: null);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);

        var resumed = await operatorClient.PostAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/resume", content: null);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);

        var first = await console.NextAsync();
        var second = await console.NextAsync();

        foreach (var notice in new[] { first, second })
        {
            Assert.Equal("SenderControlChanged", notice.GetProperty("kind").GetString());
            Assert.Equal(
                TestPrincipals.AcmeSenderPrincipal,
                notice.GetProperty("subjectId").GetString());
        }

        // The whole payload, named: a field for the direction would be state on the wire, which is
        // the one thing a notice may not carry.
        Assert.Equal(["kind", "occurredAt", "subjectId"], PropertyNames(first));
    }

    [Fact]
    public async Task Readiness_is_announced_when_the_answer_changes_and_not_on_every_poll()
    {
        // A poll is not a change. Readiness is polled continuously by whatever routes mail to this
        // host, so announcing every poll would put a notice per liveness request on the wire and
        // teach the console to ignore the one channel that carries a real transition.
        using var host = TrafficSeamTests.WithTraffic();
        await using var console = await host.SubscribeTrafficAsync(TestPrincipals.AcmeReviewerKey);

        using var prober = host.Anonymous();

        // The first answer is a baseline, not a transition: nothing has changed yet, only started
        // being observed. A console reading the feed learns the host's readiness from the route,
        // which is where state belongs.
        Assert.Equal(HttpStatusCode.OK, (await prober.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await prober.GetAsync("/health/ready")).StatusCode);
        Assert.Empty(await console.ReceiveAndSettleAsync());

        host.Services.GetRequiredService<ProviderCredentialHealth>()
            .RecordRejected(HttpStatusCode.Unauthorized, DateTimeOffset.UnixEpoch);

        var notReady = await prober.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);

        var notice = await console.NextAsync();
        Assert.Equal("ReadinessChanged", notice.GetProperty("kind").GetString());

        // Still not ready, so nothing has changed since. This is the half that makes the emission a
        // transition rather than a sample.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await prober.GetAsync("/health/ready")).StatusCode);

        Assert.Single(await console.ReceiveAndSettleAsync());
    }

    [Fact]
    public async Task A_delivery_that_settles_is_announced_as_a_change_to_that_message()
    {
        // The delivery worker's port is the host's, and the state transaction inside the worker is
        // the queue's. Wrapping the port is what lets this host announce that a delivery happened
        // without either lane learning about the other.
        using var host = TrafficSeamTests.WithTraffic();
        await using var console = await host.SubscribeTrafficAsync(TestPrincipals.AcmeReviewerKey);

        var queueId = await AcceptAsync(host, "msg-delivered-announced");

        var worker = new QueueDeliveryWorker(
            host.Services.GetRequiredService<QueueStore>(),
            new TrafficEmittingDeliveryPort(
                new RecordingDeliveryPort(),
                host.Services.GetRequiredService<ITrafficEvents>(),
                host.Services.GetRequiredService<TimeProvider>()),
            host.Services.GetRequiredService<QueueOptions>());

        var result = await worker.RunOnceAsync();

        Assert.Equal(DeliveryCycleOutcome.Dispatched, result.Outcome);

        var notice = await console.NextAsync();

        Assert.Equal("MessageStateChanged", notice.GetProperty("kind").GetString());
        Assert.Equal(queueId, notice.GetProperty("subjectId").GetString());
    }

    private static string[] PropertyNames(JsonElement notice)
        => [.. notice.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)];

    private static async Task<string> SubmitQuarantinedAsync(TestHost host, string idempotencyKey)
    {
        host.Assessor.Action = MailAction.Quarantine;

        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/submissions")
        {
            Content = JsonContent.Create(TestMessages.Request(rawMime: TestMessages.SampleMimeBase64)),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("queueId").GetString()!;
    }

    /// <summary>Puts one deliverable message into the host's own queue.</summary>
    private static async Task<string> AcceptAsync(TestHost host, string idempotencyKey)
    {
        var accepted = await host.Services.GetRequiredService<QueueStore>().AcceptAsync(
            new QueueSubmission
            {
                TenantId = TestPrincipals.AcmeTenant,
                InternalMessageId = $"msg_{Guid.NewGuid():N}",
                Direction = MailDirection.Outbound,
                TrustedPrincipalId = TestPrincipals.AcmeSenderPrincipal,
                MailFrom = "sender@acme.test",
                MimeDigest = "digest",
                Payload = "Subject: delivered\r\n\r\nbody"u8.ToArray(),
                Recipients = [new RecipientAdmission { Recipient = "rcpt@example.test" }],
                IdempotencyKey = idempotencyKey,
            },
            CancellationToken.None);

        Assert.True(accepted.IsAccepted, accepted.Detail);
        return accepted.QueueId!;
    }
}

/// <summary>A delivery port that reports every recipient delivered.</summary>
/// <remarks>
/// The transport is not what is under test: it has its own suite and its own lane. What is under
/// test is the host's port wrapper, so the port reports the outcome that lets the delivery settle
/// and the assertion is about what the host announced, not about what SMTP did.
/// </remarks>
internal sealed class RecordingDeliveryPort : IDeliveryPort
{
    public int Calls { get; private set; }

    public ValueTask<DeliveryPortResult> DeliverAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        Calls++;

        return ValueTask.FromResult(new DeliveryPortResult
        {
            Recipients =
            [
                .. request.Recipients.Select(recipient => new RecipientDeliveryResult
                {
                    Recipient = recipient,
                    Outcome = DeliveryAttemptOutcome.Delivered,
                }),
            ],
        });
    }
}
