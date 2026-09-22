using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// Durable submission.
/// </summary>
/// <remarks>
/// The property under test throughout is acceptance: a success answer here means delivery
/// responsibility has transferred. Every path that cannot persist the message must therefore
/// answer with a temporary failure, and most of the tests below are ways of trying to make it
/// answer success without having durably stored anything.
/// </remarks>
public sealed class SubmissionTests
{
    [Fact]
    public async Task Submission_is_accepted_with_a_durable_queue_id()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.SendAsync(Submit(TestMessages.Request(), "key-1"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var queueId = QueueIdOf(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(queueId));

        // The id is only meaningful if it addresses a row that actually exists.
        var store = host.Services.GetRequiredService<QueueStore>();
        var item = await store.GetItemAsync(queueId!, TestPrincipals.AcmeTenant);

        Assert.NotNull(item);
        Assert.Equal(TestPrincipals.AcmeTenant, item!.TenantId);
    }

    [Fact]
    public async Task A_retry_with_the_same_key_and_payload_returns_the_same_queue_id()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var first = await client.SendAsync(Submit(TestMessages.Request(), "key-retry"));
        var second = await client.SendAsync(Submit(TestMessages.Request(), "key-retry"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.True(second.IsSuccessStatusCode);

        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(QueueIdOf(firstBody), QueueIdOf(secondBody));
        Assert.Equal("Accepted", StatusOf(firstBody));
        Assert.Equal("Duplicate", StatusOf(secondBody));

        // The assertion that gives this test teeth, and the reason it previously had none.
        //
        // Two mechanisms can return the same queue id: the host short-circuiting a replay from its
        // own record, and the pipeline re-running and letting the queue dedupe. A mutation deleting
        // the host fast-path left this test GREEN, because both produce the same id *and* the same
        // "Duplicate" status — asserting the outcome more loudly distinguished nothing.
        //
        // Only the host path can answer without invoking the assessor, so that is what this asserts.
        // It restates what A_retry_does_not_spend_a_second_assessment covers; that is deliberate —
        // this test claims a specific mechanism in its name and must be able to fail for it.
        Assert.Equal(1, host.Assessor.CallCount);
    }

    [Fact]
    public async Task A_retry_does_not_spend_a_second_assessment()
    {
        // A client retrying because a response was lost should cost nothing on the second attempt.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        await client.SendAsync(Submit(TestMessages.Request(), "key-once-only"));
        await client.SendAsync(Submit(TestMessages.Request(), "key-once-only"));

        Assert.Equal(1, host.Assessor.CallCount);
    }

    [Fact]
    public async Task The_same_key_with_different_content_is_a_conflict_not_an_overwrite()
    {
        // The dangerous alternative is answering with the queue id of the message already held,
        // which would tell the client their *new* message was accepted when it was never stored.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        await client.SendAsync(Submit(TestMessages.Request(), "key-conflict"));

        var different = TestMessages.Request(
            rawMime: TestMessages.Base64(
                TestMessages.SampleMime.Replace("Quarterly figures", "Urgent payment request")));
        var response = await client.SendAsync(Submit(different, "key-conflict"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("idempotency_conflict", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Idempotency_keys_are_scoped_to_the_tenant_that_supplied_them()
    {
        // Two tenants choosing the same key is a coincidence, not a collision. If keys were global,
        // one tenant could discover another's queue ids by guessing a common key.
        using var host = new TestHost();

        using var acme = host.ClientAs(TestPrincipals.AcmeSenderKey);
        using var globex = host.ClientAs(TestPrincipals.GlobexSenderKey);

        var acmeResponse = await acme.SendAsync(Submit(TestMessages.Request(), "shared-key"));
        var globexResponse = await globex.SendAsync(Submit(TestMessages.Request(), "shared-key"));

        Assert.Equal(HttpStatusCode.Accepted, acmeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, globexResponse.StatusCode);
        Assert.NotEqual(
            QueueIdOf(await acmeResponse.Content.ReadAsStringAsync()),
            QueueIdOf(await globexResponse.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task A_submission_without_an_idempotency_key_is_refused()
    {
        // Tightened by overview-'s ruling. Accepting it would have meant a client that retries
        // after a lost response — the ordinary case — silently delivering a second copy, and the
        // route cannot keep §12's replay promise for a caller that supplies no key to replay with.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.SendAsync(Submit(TestMessages.Request(), idempotencyKey: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("idempotency_key_required", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Submission_fails_closed_when_durable_storage_is_unavailable()
    {
        // The single most important test in this file. Disk full must produce a temporary failure,
        // never a success: an accepted message we could not persist is a message destroyed.
        using var host = new TestHost().FailSubmissions();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.SendAsync(Submit(TestMessages.Request(), "key-storage"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("storage_unavailable", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task When_the_pipeline_cannot_accept_durably_the_route_reports_a_temporary_failure()
    {
        // Acceptance now happens inside the pipeline, so a durable-storage failure surfaces as a
        // Defer on the assessment rather than as an exception from a call this route makes. The
        // route's obligation is unchanged: no responsibility transferred, so no 2xx.
        using var host = new TestHost();
        host.Assessor.AcceptanceRefused = true;
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.SendAsync(Submit(TestMessages.Request(), "key-defer"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Accepted, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("queueId", out _));
    }

    [Fact]
    public async Task A_submission_refused_by_admission_control_is_not_reported_as_accepted()
    {
        // Quota refusal is a decision to decline, not a storage failure, but it is still not an
        // acceptance — and the boundary between "declined" and "accepted" is the one that matters.
        using var host = new TestHost().WithQueueOptions(new QueueOptions { MaxQueuedItemsPerTenant = 1 });
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var first = await client.SendAsync(Submit(TestMessages.Request(), "key-quota-1"));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.SendAsync(Submit(
            TestMessages.Request(rawMime: TestMessages.Base64(
                TestMessages.SampleMime.Replace("Quarterly", "Annual"))),
            "key-quota-2"));

        Assert.NotEqual(HttpStatusCode.Accepted, second.StatusCode);
        Assert.True(
            (int)second.StatusCode >= 400,
            $"A refusal must not read as success; got {(int)second.StatusCode}.");
    }

    [Fact]
    public async Task A_quarantined_submission_is_recorded_as_quarantined_not_queued_for_delivery()
    {
        using var host = new TestHost();
        host.Assessor.Action = StyloMail.Core.MailAction.Quarantine;
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.SendAsync(Submit(TestMessages.Request(), "key-quarantine"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var store = host.Services.GetRequiredService<QueueStore>();
        var item = await store.GetItemAsync(
            QueueIdOf(await response.Content.ReadAsStringAsync())!, TestPrincipals.AcmeTenant);

        Assert.NotNull(item);
        Assert.All(item!.Recipients, r => Assert.Equal(StyloMail.Core.DeliveryState.Quarantined, r.State));
    }

    [Fact]
    public async Task Submission_is_marked_as_participating_in_live_traffic_accounting()
    {
        // The counterpart to the assessment endpoint's AssessmentOnly assertion: this route is the
        // one that owns observation and delivery state.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        await client.SendAsync(Submit(TestMessages.Request(), "key-accounting"));

        var call = Assert.Single(host.Assessor.Calls);
        Assert.False(call.Context.AssessmentOnly);
    }

    [Fact]
    public async Task Assessment_creates_no_queue_state()
    {
        // Spec §12: an assessment has no delivery implication. If this ever fails, the assessment
        // route has quietly started accepting mail.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var counts = await host.Services.GetRequiredService<QueueStore>()
            .CountByStateAsync(TestPrincipals.AcmeTenant);

        Assert.Empty(counts);
    }

    private static HttpRequestMessage Submit(object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/submissions")
        {
            Content = JsonContent.Create(body),
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static string? QueueIdOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("queueId").GetString();
    }

    private static string? StatusOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("status").GetString();
    }
}
