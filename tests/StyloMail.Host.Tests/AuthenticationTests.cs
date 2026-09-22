using System.Net;
using System.Net.Http.Json;

namespace StyloMail.Host.Tests;

/// <summary>
/// Every route that reaches into a tenant must establish who is asking before it does anything
/// else. These tests pin the default-deny property: a route added without an authorization
/// requirement should fail here rather than quietly become public.
/// </summary>
public sealed class AuthenticationTests
{
    public static TheoryData<string, string> ProtectedRoutes => new()
    {
        { "POST", "/v1/assessments" },
        { "POST", "/v1/submissions" },
        { "GET", "/v1/submissions/sub_123" },
        { "GET", "/v1/decisions/asm_123" },
        { "POST", "/v1/feedback" },
        { "POST", "/v1/quarantine/sub_123/release" },
        { "POST", "/v1/controls/senders/user-1/pause" },
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Route_without_credentials_is_rejected(string method, string path)
    {
        using var host = new TestHost();
        using var client = host.Anonymous();

        var response = await Send(client, method, path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Route_with_an_unrecognised_key_is_rejected(string method, string path)
    {
        using var host = new TestHost();
        using var client = host.ClientAs("test-key-not-registered");

        var response = await Send(client, method, path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_never_assessed()
    {
        // Authentication must gate the work, not merely the response. If an unauthenticated call
        // still reached the assessor we would be doing unpaid work on an attacker's behalf and
        // burning provider budget to do it.
        using var host = new TestHost();
        using var client = host.Anonymous();

        var response = await Send(client, "POST", "/v1/assessments");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Assessor.CallCount);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (method is "POST" or "PUT" or "PATCH")
        {
            // A syntactically valid body, so a 400 for malformed input cannot be mistaken for
            // the authentication result under test.
            request.Content = JsonContent.Create(new { });
        }

        return client.SendAsync(request);
    }
}
