using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace StyloMail.Host.Tests;

/// <summary>
/// Reading the decision ledger.
/// </summary>
/// <remarks>
/// The ledger is the explainability surface, which makes it the most attractive thing on this host
/// to read across a tenant boundary: it holds who has been talking to whom and what we concluded
/// about it. These tests pin tenant scoping on it.
/// </remarks>
public sealed class DecisionLedgerTests
{
    [Fact]
    public async Task A_decision_is_readable_by_a_reviewer_of_its_own_tenant()
    {
        using var host = new TestHost();
        var assessmentId = await AssessAsync(host, TestPrincipals.AcmeSenderKey);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.GetAsync($"/v1/decisions/{assessmentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(assessmentId, body.RootElement.GetProperty("assessmentId").GetString());
    }

    [Fact]
    public async Task Another_tenants_reviewer_cannot_read_it()
    {
        // Globex holds the review privilege and is fully authenticated. It is refused purely on
        // tenancy, so this cannot pass merely because the caller lacked a permission.
        using var host = new TestHost();
        var assessmentId = await AssessAsync(host, TestPrincipals.AcmeSenderKey);

        using var other = host.ClientAs(TestPrincipals.GlobexReviewerKey);
        var response = await other.GetAsync($"/v1/decisions/{assessmentId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_cross_tenant_read_does_not_reveal_that_the_decision_exists()
    {
        // 404 rather than 403 on purpose. A 403 would confirm the id is real, turning the ledger
        // into an oracle a tenant could walk to enumerate another tenant's message ids.
        using var host = new TestHost();
        var realAssessmentId = await AssessAsync(host, TestPrincipals.AcmeSenderKey);

        using var other = host.ClientAs(TestPrincipals.GlobexReviewerKey);
        var real = await other.GetAsync($"/v1/decisions/{realAssessmentId}");
        var imaginary = await other.GetAsync("/v1/decisions/asm_does_not_exist");

        Assert.Equal(imaginary.StatusCode, real.StatusCode);
        Assert.Equal(await imaginary.Content.ReadAsStringAsync(), await real.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_decision_is_not_found()
    {
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var response = await reviewer.GetAsync("/v1/decisions/asm_does_not_exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_sender_without_the_review_privilege_cannot_read_the_ledger()
    {
        // Sending and reviewing are separate grants. The sender may submit mail; reading the
        // ledger, including their own message's entry, is the reviewer's job.
        using var host = new TestHost();
        var assessmentId = await AssessAsync(host, TestPrincipals.AcmeSenderKey);

        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        var response = await sender.GetAsync($"/v1/decisions/{assessmentId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_ledger_survives_a_restart()
    {
        // A ledger that only exists in the process that produced it cannot audit anything.
        using var host = new TestHost();
        var assessmentId = await AssessAsync(host, TestPrincipals.AcmeSenderKey);

        using var reopened = new TestHost().ReusingStorageOf(host);
        using var reviewer = reopened.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.GetAsync($"/v1/decisions/{assessmentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> AssessAsync(TestHost host, string apiKey)
    {
        using var client = host.ClientAs(apiKey);
        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("assessmentId").GetString()!;
    }
}
