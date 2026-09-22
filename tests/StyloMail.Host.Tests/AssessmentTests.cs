using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace StyloMail.Host.Tests;

/// <summary>
/// The assessment path. Its defining property is what it does <em>not</em> do: an assessment
/// carries no delivery implication and trains nothing. Most of these tests exist to keep that
/// boundary from eroding as the endpoint grows.
/// </summary>
public sealed class AssessmentTests
{
    [Fact]
    public async Task Assessment_returns_the_assessors_decision()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);
        host.Assessor.Action = StyloMail.Core.MailAction.Allow;

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("assessmentId").GetString()));
        Assert.Equal("Allow", root.GetProperty("action").GetString());
        Assert.NotEmpty(root.GetProperty("reasons").EnumerateArray());
        Assert.Equal("policy/test", root.GetProperty("versions").GetProperty("policyVersion").GetString());
    }

    [Fact]
    public async Task Assessment_takes_its_tenant_from_the_principal_not_the_message()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());

        var call = Assert.Single(host.Assessor.Calls);
        Assert.Equal(TestPrincipals.AcmeTenant, call.Context.TenantId);
        Assert.Equal(TestPrincipals.AcmeTenant, call.Input.Envelope.TenantId);
    }

    [Fact]
    public async Task Assessment_is_marked_as_carrying_no_delivery_implication()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());

        // This flag is the contract Core defines for "must not send mail, advance delivery state,
        // or feed live traffic accounting". If it is ever false on this route, the assessment
        // endpoint has quietly become a submission endpoint.
        var call = Assert.Single(host.Assessor.Calls);
        Assert.True(call.Context.AssessmentOnly);
    }

    [Fact]
    public async Task Assessment_records_the_authenticated_principal_on_the_envelope()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());

        var call = Assert.Single(host.Assessor.Calls);
        Assert.Equal(TestPrincipals.AcmeSenderPrincipal, call.Input.Envelope.TrustedPrincipalId);
    }

    [Fact]
    public async Task A_body_naming_another_tenant_is_refused_rather_than_honoured()
    {
        // The whole point: a JSON field must never become an authority grant. The caller here is
        // legitimately authenticated as acme and simply asks to be treated as globex.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync(
            "/v1/assessments", TestMessages.Request(tenantId: TestPrincipals.GlobexTenant));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, host.Assessor.CallCount);
    }

    [Fact]
    public async Task A_body_naming_the_callers_own_tenant_is_accepted()
    {
        // Restating your own tenant is harmless and some clients will do it; it must not be an
        // error, or the check above would be indistinguishable from "reject any tenantId field".
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync(
            "/v1/assessments", TestMessages.Request(tenantId: TestPrincipals.AcmeTenant));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_sender_cannot_place_itself_in_shadow_mode()
    {
        // Shadow means "record the proposed action and forward anyway". If the sending principal
        // can choose it, shadow stops being an operator observation mode and becomes a
        // self-service bypass of every intervention the system might make.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request(shadowMode: true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, host.Assessor.CallCount);
    }

    [Fact]
    public async Task Malformed_mime_is_refused_without_reaching_the_assessor()
    {
        // Spec §11: an unparseable message gets an explicit unsupported disposition. It is never
        // analysed as though a fragment were the whole message.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var request = new
        {
            direction = "Outbound",
            mailFrom = "sender@example.com",
            rcptTo = new[] { "recipient@example.com" },
            rawMime = Convert.ToBase64String(Encoding.UTF8.GetBytes("this is not a message")),
        };

        var response = await client.PostAsJsonAsync("/v1/assessments", request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, host.Assessor.CallCount);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("malformed", body.RootElement.GetProperty("disposition").GetString());
    }

    [Fact]
    public async Task An_error_body_never_echoes_the_message_it_rejected()
    {
        // Error responses are a classic leak site: a handler that explains itself by quoting the
        // input it could not handle turns a rejection into a content-disclosure channel, and the
        // rejection is exactly the path nobody exercises. This pins the claim on EndpointResults.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        const string canary = "ZQXJ9-ORBITAL-7734";
        var request = new
        {
            direction = "Outbound",
            mailFrom = "sender@example.com",
            rcptTo = new[] { "recipient@example.com" },
            rawMime = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{canary} secret-body-text")),
        };

        var response = await client.PostAsJsonAsync("/v1/assessments", request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(canary, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-body-text", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unconfigured_assessor_is_reported_rather_than_faked()
    {
        // Nothing in the repo implements IMailAssessor yet. The host must say so plainly instead
        // of inventing a verdict: a fabricated "Allow" is the most dangerous possible answer.
        using var host = new TestHost().WithoutAssessor();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("assessor_unavailable", body.RootElement.GetProperty("error").GetString());
    }
}
