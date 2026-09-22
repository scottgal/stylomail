using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace StyloMail.Host.Tests;

/// <summary>
/// Cross-site request forgery on the browser operator channel.
/// </summary>
/// <remarks>
/// The API-key header is not forgeable cross-site: a browser does not attach a custom header to a
/// request a hostile page caused. A cookie is the opposite, the browser attaches it automatically,
/// which is exactly what makes it convenient for a browser UI and exactly what makes it dangerous.
///
/// <para>
/// So the cookie channel exists only when explicitly enabled, and any request that changed state
/// while authenticated by cookie must also carry an anti-forgery token.
/// </para>
/// </remarks>
public sealed class CsrfTests
{
    [Fact]
    public async Task The_browser_cookie_channel_is_off_by_default()
    {
        // An API-only deployment has no CSRF surface. The safest way to keep it that way is to not
        // accept a browser credential at all unless an operator asks for one.
        using var host = new TestHost();
        using var client = host.Anonymous();

        var response = await client.PostAsJsonAsync("/v1/session", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_cookie_authenticated_write_without_an_antiforgery_token_is_refused()
    {
        using var host = new TestHost().WithBrowserChannel();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await SignInAsync(client);

        // The cookie is now attached automatically by this client, which is precisely what a
        // hostile page would rely on. No token is supplied, so the write must not happen.
        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, host.Assessor.CallCount);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("csrf_token_invalid", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_cookie_authenticated_write_with_a_valid_token_is_allowed()
    {
        // The protection must not be so blunt that the browser surface cannot be used at all.
        using var host = new TestHost().WithBrowserChannel();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var csrfToken = await SignInAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/assessments")
        {
            Content = JsonContent.Create(TestMessages.Request()),
        };
        request.Headers.Add("X-StyloMail-CSRF", csrfToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_cookie_authenticated_read_needs_no_antiforgery_token()
    {
        // Safe methods do not change state, so requiring a token for them would break ordinary
        // navigation without adding any protection.
        using var host = new TestHost().WithBrowserChannel();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await SignInAsync(client);

        var response = await client.GetAsync("/v1/decisions/asm_does_not_exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_header_authenticated_write_needs_no_antiforgery_token()
    {
        // The default, non-browser path. A custom header cannot be set by a cross-site form post,
        // so requiring a token here would be ceremony rather than protection.
        using var host = new TestHost().WithBrowserChannel();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Signing_in_with_an_unrecognised_key_issues_no_cookie()
    {
        using var host = new TestHost().WithBrowserChannel();
        using var client = host.ClientAs("test-key-not-registered");

        var response = await client.PostAsJsonAsync("/v1/session", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signing_in_without_any_key_issues_no_cookie()
    {
        using var host = new TestHost().WithBrowserChannel();
        using var client = host.Anonymous();

        var response = await client.PostAsJsonAsync("/v1/session", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Establishes a browser session and returns the anti-forgery token to send with writes.</summary>
    private static async Task<string> SignInAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/v1/session", new { });
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("csrfToken").GetString()!;
    }
}
