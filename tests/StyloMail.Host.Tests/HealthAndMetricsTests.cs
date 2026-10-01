using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Host.Assessors;
using StyloMail.Host.Observability;

namespace StyloMail.Host.Tests;

/// <summary>
/// Health and metrics.
/// </summary>
/// <remarks>
/// These routes exist to be polled by a load balancer and a scraper, which means they are
/// reachable without credentials by design. That makes them the easiest place in the host for
/// message content to escape, and the tests below are mostly attempts to get some out.
/// </remarks>
public sealed class HealthAndMetricsTests
{
    /// <summary>A token that appears only in the message under test, so finding it anywhere in a
    /// health or metrics response is unambiguously a leak.</summary>
    private const string Secret = "ZQXJ9-ORBITAL-7734";

    private static string MarkedMessage => TestMessages.SampleMime
        .Replace("Quarterly figures", Secret)
        .Replace("recipient@example.com", "leak.canary@example.com");

    private static readonly string MarkedMessageBase64 = TestMessages.Base64(MarkedMessage);

    [Fact]
    public async Task Liveness_needs_no_credentials()
    {
        // A probe that has to authenticate cannot tell a load balancer anything useful, and the
        // credential would have to be distributed to the load balancer.
        using var host = new TestHost();
        using var client = host.Anonymous();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_needs_no_credentials_and_reports_ready()
    {
        using var host = new TestHost();
        using var client = host.Anonymous();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_reports_not_ready_when_the_spool_stops_being_writable()
    {
        // Readiness is a claim that this host can durably accept mail. If the spool is unusable it
        // must stop advertising itself, because the alternative is a load balancer continuing to
        // send it messages that will be refused.
        //
        // The spool breaks *after* startup here on purpose. A volume that is unmounted or filled
        // while the process runs is the case this check exists for; a spool that is broken at boot
        // is caught earlier, because the host refuses to start at all.
        using var host = new TestHost();
        using var client = host.Anonymous();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        var spool = Path.Combine(host.Root, "spool");
        Directory.Delete(spool, recursive: true);
        await File.WriteAllTextAsync(spool, "a file where a directory needs to be");

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(
            "spool",
            body.RootElement.GetProperty("failedChecks").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Readiness_reports_not_ready_when_the_host_has_no_assessor()
    {
        // The counterpart to the spool test above, and the distinction from the test before it is
        // this deployment's provider secrets versus the caller's credentials. Reaching
        // /health/ready needs no credential; that says nothing about whether the host behind it can
        // assess anything.
        //
        // WithoutAssessor() leaves the host's own composition in place, so this is the real answer:
        // with no provider secret present, BuildAssessor returns UnavailableMailAssessor, which
        // throws on every message it is handed. A host that can assess nothing must stop advertising
        // itself, or mail is routed to a deployment that refuses all of it while the load balancer
        // reads 200. That was the shape of the rejected-credential defect; this is the same shape
        // from the other direction, and worse, because nothing changes at run time to reveal it.
        using var host = new TestHost().WithoutAssessor();

        // The precondition, asserted rather than assumed: WithoutAssessor() must have left the host
        // composing the stand-in. If it ever returned a host with a working assessor instead, the
        // assertions below would be testing a shape this test does not describe.
        Assert.IsType<UnavailableMailAssessor>(
            host.Services.GetRequiredService<StyloMail.Core.IMailAssessor>());

        using var client = host.Anonymous();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // Exactly this check and no other. The database and spool are present, and no credential has
        // been rejected, so if any other name appeared here the 503 would have some cause other than
        // the one under test, and it would be the assertion below that silently passed anyway.
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            new[] { ReadinessProbe.AssessorUnavailable },
            body.RootElement.GetProperty("failedChecks").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Metrics_needs_no_credentials()
    {
        using var host = new TestHost();
        using var client = host.Anonymous();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_and_metrics_expose_no_message_content()
    {
        using var host = new TestHost();

        using (var sender = host.ClientAs(TestPrincipals.AcmeSenderKey))
        {
            var body = TestMessages.Request(rawMime: MarkedMessageBase64);
            await sender.PostAsJsonAsync("/v1/assessments", body);

            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/submissions")
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add("Idempotency-Key", "key-canary");
            await sender.SendAsync(request);
        }

        using var anonymous = host.Anonymous();
        foreach (var (path, marker) in new[]
                 {
                     ("/health/live", "status"),
                     ("/health/ready", "status"),
                     ("/metrics", "stylomail_"),
                 })
        {
            var text = await anonymous.GetStringAsync(path);

            // THE CONTROL. Every assertion below is a negative, and a blank body satisfies all
            // three at once for every path. The marker is what the route is supposed to carry, so a
            // route that stopped serving its own shape fails here instead of passing as clean.
            Assert.Contains(marker, text, StringComparison.Ordinal);

            Assert.DoesNotContain(Secret, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("leak.canary@example.com", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Quarterly", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Metrics_counts_work_without_naming_the_work()
    {
        // A counter labelled by recipient or sender would be both a content leak and an unbounded
        // cardinality cost. The counts should be there; the identities should not.
        using var host = new TestHost();

        using (var sender = host.ClientAs(TestPrincipals.AcmeSenderKey))
        {
            await sender.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        }

        using var anonymous = host.Anonymous();
        var text = await anonymous.GetStringAsync("/metrics");

        Assert.Contains("stylomail_assessments_total", text, StringComparison.Ordinal);
        Assert.Contains("1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("acme", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recipient@example.com", text, StringComparison.OrdinalIgnoreCase);
    }
}
