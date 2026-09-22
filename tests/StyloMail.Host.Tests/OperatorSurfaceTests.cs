using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Feedback;
using StyloMail.Host.Controls;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// Quarantine release, feedback and sender controls.
/// </summary>
/// <remarks>
/// These are the routes that change state on someone else's behalf, which makes privilege
/// separation and tenancy the whole of their security story. The tests are correspondingly
/// obsessed with who is asking rather than with what the handler computes.
/// </remarks>
public sealed class OperatorSurfaceTests
{
    // ---- Quarantine release -------------------------------------------------------------

    [Fact]
    public async Task A_reviewer_can_release_a_quarantined_submission()
    {
        using var host = new TestHost();
        var queueId = await QuarantinedSubmissionAsync(host);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.PostAsync($"/v1/quarantine/{queueId}/release", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var item = await host.Services.GetRequiredService<QueueStore>()
            .GetItemAsync(queueId, TestPrincipals.AcmeTenant);

        Assert.NotNull(item);
        Assert.All(item!.Recipients, r => Assert.Equal(DeliveryState.Queued, r.State));
    }

    [Fact]
    public async Task A_sender_cannot_release_its_own_quarantine()
    {
        // The case the privilege split exists for. The sender is fully authenticated, owns the
        // message, and is refused purely because sending and reviewing are different grants.
        using var host = new TestHost();
        var queueId = await QuarantinedSubmissionAsync(host);

        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        var response = await sender.PostAsync($"/v1/quarantine/{queueId}/release", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var item = await host.Services.GetRequiredService<QueueStore>()
            .GetItemAsync(queueId, TestPrincipals.AcmeTenant);
        Assert.All(item!.Recipients, r => Assert.Equal(DeliveryState.Quarantined, r.State));
    }

    [Fact]
    public async Task Releasing_a_quarantine_twice_is_not_an_error()
    {
        // A release is a decision, and a client that did not receive the response must be able to
        // ask again without being told it did something wrong.
        using var host = new TestHost();
        var queueId = await QuarantinedSubmissionAsync(host);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var first = await reviewer.PostAsync($"/v1/quarantine/{queueId}/release", null);
        var second = await reviewer.PostAsync($"/v1/quarantine/{queueId}/release", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task A_release_records_who_made_it()
    {
        using var host = new TestHost();
        var queueId = await QuarantinedSubmissionAsync(host);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        await reviewer.PostAsync($"/v1/quarantine/{queueId}/release", null);

        var attempts = await host.Services.GetRequiredService<QueueStore>()
            .GetAttemptsAsync(queueId, TestPrincipals.AcmeTenant);

        Assert.Contains(attempts, a => a.WorkerId == TestPrincipals.AcmeReviewerPrincipal);
    }

    [Fact]
    public async Task Another_tenant_cannot_release_a_quarantine()
    {
        using var host = new TestHost();
        var queueId = await QuarantinedSubmissionAsync(host);

        using var other = host.ClientAs(TestPrincipals.GlobexReviewerKey);
        var response = await other.PostAsync($"/v1/quarantine/{queueId}/release", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Feedback -----------------------------------------------------------------------

    [Fact]
    public async Task A_principal_without_the_feedback_privilege_cannot_label_a_decision()
    {
        // A reviewer reads; a different grant is required to assert a label that training will
        // consume. Review and feedback are separate on purpose.
        using var host = new TestHost();
        var decisionId = await DecisionAsync(host);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.PostAsJsonAsync("/v1/feedback", new
        {
            decisionId,
            label = "Legitimate",
            scope = "Recipient",
            recipient = "recipient@example.com",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Feedback_must_state_the_scope_it_applies_to()
    {
        // Scope is the field that stops "this one was fine" becoming "this sender is fine".
        // Defaulting it would silently pick a blast radius, so it is required.
        using var host = new TestHost();
        var decisionId = await DecisionAsync(host);

        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        var response = await client.PostAsJsonAsync("/v1/feedback", new { decisionId, label = "Legitimate" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Feedback_is_recorded_against_the_decision_and_the_scope()
    {
        using var host = new TestHost();
        var decisionId = await DecisionAsync(host);

        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        var response = await client.PostAsJsonAsync("/v1/feedback", new
        {
            decisionId,
            label = "WantedPromotion",
            scope = "Recipient",
            recipient = "recipient@example.com",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var store = host.Services.GetRequiredService<IFeedbackStore>();
        var recorded = await store.ListAsync(TestPrincipals.AcmeTenant, decisionId, CancellationToken.None);

        var entry = Assert.Single(recorded);
        Assert.Equal(FeedbackScope.Recipient, entry.Scope);
        Assert.Equal("recipient@example.com", entry.Recipient);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, entry.RecordedBy);
    }

    [Fact]
    public async Task Feedback_cannot_be_left_against_another_tenants_decision()
    {
        using var host = new TestHost();
        var decisionId = await DecisionAsync(host);

        // Globex's operator holds the feedback privilege, so this is refused on tenancy rather
        // than merely on privilege.
        using var other = host.ClientAs(TestPrincipals.GlobexOperatorKey);
        var response = await other.PostAsJsonAsync("/v1/feedback", new
        {
            decisionId,
            label = "Legitimate",
            scope = "Recipient",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Sender controls ----------------------------------------------------------------

    [Fact]
    public async Task Pausing_a_sender_requires_the_administer_privilege()
    {
        using var host = new TestHost();

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var refused = await reviewer.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/pause",
            new { reason = "suspected compromise" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Pausing_is_idempotent()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var body = new { reason = "suspected compromise" };
        var first = await client.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/pause", body);
        var second = await client.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/pause", body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task A_pause_is_scoped_to_the_tenant_that_issued_it()
    {
        // A pause names a principal id. Two tenants can legitimately use the same id, and one
        // tenant's control-plane action must not reach into the other's senders.
        using var host = new TestHost();

        using var globex = host.ClientAs(TestPrincipals.GlobexOperatorKey);
        var response = await globex.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/pause",
            new { reason = "not my sender" });

        // Globex holds Administer, so this is accepted — for its own tenant's namespace. Pausing a
        // principal id is a tenant-local act; it must never reach the other tenant's sender.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var store = host.Services.GetRequiredService<ISenderControlStore>();
        var acmeState = await store.GetAsync(
            TestPrincipals.AcmeTenant, TestPrincipals.AcmeSenderPrincipal, CancellationToken.None);

        Assert.True(acmeState is null || !acmeState.Paused);
    }

    [Fact]
    public async Task A_paused_sender_is_recorded_as_paused_for_its_own_tenant()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await client.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/pause",
            new { reason = "suspected compromise" });

        var store = host.Services.GetRequiredService<ISenderControlStore>();
        var state = await store.GetAsync(
            TestPrincipals.AcmeTenant, TestPrincipals.AcmeSenderPrincipal, CancellationToken.None);

        Assert.NotNull(state);
        Assert.True(state!.Paused);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, state.UpdatedBy);
    }

    // ---- Sender resume ------------------------------------------------------------------

    [Fact]
    public async Task Resuming_a_sender_requires_the_administer_privilege()
    {
        using var host = new TestHost();
        await PauseAsync(host);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/resume",
            new { reason = "false alarm" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Refused on privilege, so the sender must still be paused. Asserting only the status
        // would pass even if the handler had resumed them and then reported 403.
        Assert.True(await IsPausedAsync(host));
    }

    [Fact]
    public async Task A_paused_sender_can_be_resumed()
    {
        using var host = new TestHost();
        await PauseAsync(host);
        Assert.True(await IsPausedAsync(host));

        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        var response = await client.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/resume",
            new { reason = "false alarm, confirmed with the account owner" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The claim in the name is that the pause was lifted, so that is what is asserted. A 200
        // alone would be satisfied by a handler that did nothing.
        Assert.False(await IsPausedAsync(host));
    }

    [Fact]
    public async Task Resuming_records_who_lifted_the_pause()
    {
        using var host = new TestHost();
        await PauseAsync(host);

        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await client.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/resume",
            new { reason = "confirmed legitimate" });

        var state = await ControlStateAsync(host);

        Assert.NotNull(state);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, state!.ResumedBy);
        Assert.Equal("confirmed legitimate", state.ResumeReason);
        Assert.NotNull(state.ResumedAt);
    }

    [Fact]
    public async Task Resuming_does_not_erase_the_record_that_the_pause_happened()
    {
        // A control plane that forgets an intervention once it is lifted cannot be audited, and
        // "why was this account stopped for six hours?" becomes unanswerable. The pause fields are
        // kept, not overwritten.
        using var host = new TestHost();
        await PauseAsync(host, "suspected compromise");

        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await client.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/resume",
            new { reason = "false positive" });

        var state = await ControlStateAsync(host);

        Assert.NotNull(state);

        // Two-sided on purpose. Asserting only that the pause record survived is satisfied by a
        // resume that does nothing at all — which the mutation run confirmed, this test passed
        // against a no-op. It has to assert that the pause was lifted *and* that the history
        // survived; a no-op fails the first, an erasing implementation fails the second.
        Assert.False(state!.Paused);
        Assert.Equal("suspected compromise", state.Reason);
        Assert.NotNull(state.PausedAt);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, state.UpdatedBy);
    }

    [Fact]
    public async Task Resuming_is_idempotent()
    {
        using var host = new TestHost();
        await PauseAsync(host);

        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        var first = await client.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/resume", new { reason = "a" });
        var second = await client.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/resume", new { reason = "a" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(await IsPausedAsync(host));
    }

    [Fact]
    public async Task Resuming_a_sender_who_was_never_paused_leaves_them_unpaused()
    {
        using var host = new TestHost();

        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        var response = await client.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/resume", new { reason = "n/a" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await IsPausedAsync(host));
    }

    [Fact]
    public async Task A_resume_cannot_reach_another_tenants_sender()
    {
        using var host = new TestHost();
        await PauseAsync(host);

        // Globex holds Administer, so this is refused on tenancy, not on privilege.
        using var globex = host.ClientAs(TestPrincipals.GlobexOperatorKey);
        await globex.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/resume",
            new { reason = "not mine" });

        // Acme's sender is untouched.
        Assert.True(await IsPausedAsync(host));

        // And the positive control that makes the assertion above mean something: the same
        // principal id, in the tenant that did issue the resume, *was* affected. Without this the
        // test passes against a resume that does nothing at all, which is how it survived the
        // mutation run. A principal id is only unique within its tenant, so globex acting on it is
        // a legitimate action in globex's own namespace — and must land there.
        var globexState = await host.Services.GetRequiredService<ISenderControlStore>()
            .GetAsync(TestPrincipals.GlobexTenant, TestPrincipals.AcmeSenderPrincipal, CancellationToken.None);

        Assert.NotNull(globexState);
        Assert.False(globexState!.Paused);
        Assert.Equal(TestPrincipals.GlobexOperatorPrincipal, globexState.ResumedBy);
    }

    // ---- helpers ------------------------------------------------------------------------

    private static async Task PauseAsync(TestHost host, string reason = "suspected compromise")
    {
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        var response = await client.PostAsJsonAsync(
            $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/pause", new { reason });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<bool> IsPausedAsync(TestHost host)
    {
        var state = await ControlStateAsync(host);
        return state?.Paused ?? false;
    }

    private static Task<SenderControlState?> ControlStateAsync(TestHost host)
        => host.Services.GetRequiredService<ISenderControlStore>()
            .GetAsync(TestPrincipals.AcmeTenant, TestPrincipals.AcmeSenderPrincipal, CancellationToken.None);

    private static async Task<string> QuarantinedSubmissionAsync(TestHost host)
    {
        host.Assessor.Action = MailAction.Quarantine;

        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/submissions")
        {
            Content = JsonContent.Create(TestMessages.Request()),
        };
        request.Headers.Add("Idempotency-Key", $"q-{Guid.NewGuid():N}");

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("queueId").GetString()!;
    }

    private static async Task<string> DecisionAsync(TestHost host)
    {
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);
        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("assessmentId").GetString()!;
    }
}
