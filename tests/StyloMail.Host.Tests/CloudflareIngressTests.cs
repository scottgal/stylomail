using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Endpoints;
using StyloMail.Host.Hosting;
using StyloMail.Queue;
using StyloMail.Transport.Cloudflare;

namespace StyloMail.Host.Tests;

/// <summary>
/// <c>POST /v1/ingress/cloudflare</c> — the inbound handoff from a Cloudflare Email Routing Worker.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the host's one route with no authenticated principal, so the tests here are mostly
/// about what it refuses.</b> If it accepted an unauthenticated message it would be a public mail
/// injection endpoint; if it accepted one for a domain this deployment does not serve, the secret
/// would be the only gate left, and the recipient-domain check exists precisely so that it is not.
/// </para>
/// <para>
/// The acceptance tests drive it the way the Worker will: raw message bytes as the body, the envelope
/// in headers, the secret in a bearer token. That shape is the contract, so it is what is asserted
/// rather than a host-local request record.
/// </para>
/// </remarks>
public sealed class CloudflareIngressTests
{
    private const string Secret = "test-cf-ingress-secret-not-a-real-credential";
    private const string Served = "example.test";
    private const string Unserved = "elsewhere.test";

    [Fact]
    public async Task A_worker_message_with_the_right_secret_is_accepted_with_a_durable_queue_row()
    {
        using var host = new TestHost().WithCloudflareIngress(Secret, Served).CountingSubmissions();
        using var client = host.Anonymous();

        var response = await PostAsync(client, Secret, $"recipient@{Served}");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Accepted", body.RootElement.GetProperty("status").GetString());

        // The queue id is only meaningful if it addresses a row that actually exists.
        var queueId = Assert.IsType<string>(body.RootElement.GetProperty("queueId").GetString());
        var item = await host.Services.GetRequiredService<QueueStore>().GetItemAsync(queueId, "inbound");

        Assert.NotNull(item);
        Assert.Equal(MailDirection.Inbound, item!.Envelope.Direction);

        // And exactly one acceptance, so the route is not quietly a second one.
        Assert.Equal(1, host.Submissions.AcceptAttempts);
    }

    [Fact]
    public async Task The_message_the_sink_receives_is_the_worker_s_bytes_plus_one_hop_marker()
    {
        // Preservation again, and for the same reason: rewriting signed content breaks the signature
        // the message arrived under.
        using var host = new TestHost().WithCloudflareIngress(Secret, Served);
        using var client = host.Anonymous();

        await PostAsync(client, Secret, $"recipient@{Served}");

        var spool = host.Services.GetRequiredService<SpoolStore>();
        var payloads = Directory.EnumerateFiles(spool.Root, "*.eml", SearchOption.AllDirectories).ToList();

        // Two copies, and that is expected rather than a leak to chase here: the ingress copy the
        // pipeline reads the message back from, and the queue's own copy written under the queue id,
        // which *is* the acceptance. Deleting the first once the second exists is the outstanding
        // delete-after-accept work, deliberately not started.
        var ingressCopy = Assert.Single(payloads.Where(
            path => Path.GetFileName(path).StartsWith("ingress-", StringComparison.Ordinal)));

        var stored = await File.ReadAllBytesAsync(ingressCopy);
        var marker = stored.AsSpan().Slice(0, stored.Length - Mime().Length);

        Assert.StartsWith("Received: ", Encoding.UTF8.GetString(marker), StringComparison.Ordinal);
        Assert.Equal(Mime(), stored[marker.Length..]);

        // And the queue's copy is the same bytes, not a re-serialisation of them.
        foreach (var other in payloads.Where(path => path != ingressCopy))
        {
            Assert.Equal(stored, await File.ReadAllBytesAsync(other));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer wrong-secret")]
    [InlineData("not-even-a-bearer-token")]
    public async Task A_worker_that_cannot_authenticate_is_refused_and_nothing_is_queued(string? authorization)
    {
        using var host = new TestHost().WithCloudflareIngress(Secret, Served).CountingSubmissions();
        using var client = host.Anonymous();

        var response = await PostAsync(client, authorization, $"recipient@{Served}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var counts = await host.Services.GetRequiredService<QueueStore>().CountByStateAsync("inbound");
        Assert.Empty(counts);
        Assert.Equal(0, host.Submissions.AcceptAttempts);
    }

    [Fact]
    public async Task A_recipient_in_a_domain_this_deployment_does_not_serve_is_refused()
    {
        // The second gate, and the one that still holds if the secret leaks. An authenticated intake
        // that would accept mail for any domain is a relay that happens to require a password.
        using var host = new TestHost().WithCloudflareIngress(Secret, Served).CountingSubmissions();
        using var client = host.Anonymous();

        var response = await PostAsync(client, Secret, $"victim@{Unserved}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var counts = await host.Services.GetRequiredService<QueueStore>().CountByStateAsync("inbound");
        Assert.Empty(counts);
        Assert.Equal(0, host.Submissions.AcceptAttempts);
    }

    [Fact]
    public async Task A_message_with_no_envelope_recipient_is_refused_rather_than_routed_by_its_headers()
    {
        // Without an envelope recipient there is no routing decision to make, and falling back to the
        // To header would let message content choose the destination — which is the whole reason the
        // envelope is the only thing consulted.
        using var host = new TestHost().WithCloudflareIngress(Secret, Served);
        using var client = host.Anonymous();

        var response = await PostAsync(client, Secret, envelopeTo: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_message_over_the_configured_maximum_is_refused_by_the_connectors_own_check()
    {
        // The connector's bound, not the server's. Both exist, and they are deliberately separate
        // mechanisms — but the message has to reach the connector for its answer to be the one that
        // names a maximum an operator can act on.
        using var host = new TestHost().WithCloudflareIngress(Secret, Served);
        host.Configure("StyloMail:Transport:CloudflareIngress:MaxMessageBytes", "2048");

        using var client = host.Anonymous();

        var oversized = new byte[4096];
        Array.Copy(Mime(), oversized, Mime().Length);

        var response = await PostAsync(client, Secret, $"recipient@{Served}", oversized);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        var counts = await host.Services.GetRequiredService<QueueStore>().CountByStateAsync("inbound");
        Assert.Empty(counts);
    }

    [Fact]
    public async Task The_route_does_not_exist_when_this_deployment_has_not_enabled_it()
    {
        // Not mapped rather than mapped-and-refusing, the same rule POST /v1/session follows: a route
        // that exists but always fails invites a configuration change to "fix" it, whereas a 404 says
        // plainly that this deployment has no such intake.
        using var host = new TestHost();
        using var client = host.Anonymous();

        var response = await PostAsync(client, Secret, $"recipient@{Served}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Enabling_the_intake_without_a_secret_refuses_to_start()
    {
        // Deterministic because nothing in this suite sets the variable, and nothing should: the
        // guard is what stops an enabled route from being an unauthenticated mail injection endpoint.
        // Refusing to start names the variable; serving would name nothing, and would send the
        // operator to inspect the Worker while the fault is this end.
        var host = new TestHost();
        host.Configure("StyloMail:Transport:CloudflareIngress:Enabled", "true");
        host.Configure("StyloMail:Transport:CloudflareIngress:RecipientDomains:0", Served);

        try
        {
            var failure = Assert.ThrowsAny<Exception>(
                () => host.Services.GetRequiredService<CloudflareEmailRoutingConnector>());

            Assert.Contains(
                HostCredentials.CloudflareIngressSecretEnvironmentVariable,
                Describe(failure),
                StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                host.Dispose();
            }
            catch (Exception)
            {
                // The host failed to start; disposing a half-built host is best effort.
            }
        }
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "", true)]
    [InlineData(true, "a-secret", false)]
    [InlineData(false, null, false)]
    public void The_secret_is_only_required_when_the_intake_is_enabled(
        bool enabled,
        string? secret,
        bool shouldThrow)
    {
        // Kept as a pure function so this needs no process environment, which would race against
        // every other test in the suite. A disabled connector requiring a secret would force
        // deployments that do not want this intake to hold a credential for it.
        void Act() =>
            HostCredentials.RequireCloudflareIngressSecretIfEnabled(secret, enabled);

        if (shouldThrow)
        {
            var failure = Assert.Throws<InvalidOperationException>(Act);
            Assert.Contains(
                HostCredentials.CloudflareIngressSecretEnvironmentVariable,
                failure.Message,
                StringComparison.Ordinal);
        }
        else
        {
            Act();
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string? authorization,
        string? envelopeTo,
        byte[]? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, CloudflareIngressEndpoints.Route)
        {
            Content = new ByteArrayContent(body ?? Mime()),
        };

        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("message/rfc822");

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (envelopeTo is not null)
        {
            request.Headers.Add(CloudflareIngressEndpoints.EnvelopeToHeader, envelopeTo);
        }

        request.Headers.Add(CloudflareIngressEndpoints.EnvelopeFromHeader, "sender@example.com");

        return await client.SendAsync(request);
    }

    /// <summary>The raw message, with the CRLF line endings an RFC 5322 body actually uses.</summary>
    private static byte[] Mime() =>
        Encoding.UTF8.GetBytes(TestMessages.SampleMime.Replace("\r\n", "\n").Replace("\n", "\r\n"));

    private static string Describe(Exception exception)
    {
        var text = new StringBuilder();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }
}
