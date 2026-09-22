using Microsoft.Extensions.DependencyInjection;
using StyloMail.Host.Hosting;

namespace StyloMail.Host.Tests;

/// <summary>
/// The delivery worker, hosted in-process by the host.
/// </summary>
/// <remarks>
/// <para>
/// <b>The worker existed and was tested but had no caller</b>, which is its own kind of defect: a
/// component nobody runs is not a component, and the thing it was supposed to do, drain the queue, /// simply did not happen in any deployment. These tests are about the hosting rather than about
/// delivery, which the queue's own suite covers.
/// </para>
/// <para>
/// The other half is the shutdown token. <see cref="QueueDeliveryWorker.RunAsync"/> takes one, and
/// its bounded drain only happens if the token it is given is the one cancelled when the host begins
/// shutting down. Passing the wrong one produces either a process that hangs on a wedged upstream or
/// a delivery cut off mid-flight, and the second of those creates the duplicate the drain exists to
/// prevent.
/// </para>
/// </remarks>
public sealed class DeliveryWorkerHostingTests
{
    [Fact]
    public void No_upstream_configured_means_no_worker_is_draining_the_queue()
    {
        using var host = new TestHost();

        var service = host.Services.GetRequiredService<QueueDeliveryHostedService>();

        Assert.Null(service.WorkerId);
    }

    [Fact]
    public void A_configured_upstream_starts_the_worker_with_a_named_lease_identity()
    {
        using var host = WithUpstream();

        var service = host.Services.GetRequiredService<QueueDeliveryHostedService>();

        Assert.NotNull(service.WorkerId);
        Assert.StartsWith("worker-", service.WorkerId!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_worker_stops_promptly_when_the_host_shuts_down()
    {
        // The bounded drain must not become an unbounded one. A worker that ignored its shutdown
        // token would hold the process open until the host's own shutdown timeout expired, which in
        // a real deployment is a rolling restart that takes minutes instead of seconds.
        var host = WithUpstream();
        _ = host.Services.GetRequiredService<QueueDeliveryHostedService>().WorkerId;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        host.Dispose();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"Shutdown took {stopwatch.Elapsed.TotalSeconds:F1}s; the worker is not honouring its "
            + "shutdown token and its bounded drain has become an unbounded one.");
    }

    [Fact]
    public void A_deployment_that_accepts_mail_it_cannot_deliver_says_so_at_startup()
    {
        // Not a startup failure, an inbound-only deployment and a submission-only one are both
        // legitimate shapes, but a silence here is a configuration mistake discovered as messages
        // ageing toward their expiry, which is the worst way to find out.
        using var host = new TestHost().WithSmtpIngress("example.test");

        var described = HostServices.DescribeTransport(host.ConfigurationSnapshot);

        Assert.Contains("listener enabled", described, StringComparison.Ordinal);
        Assert.Contains("WARNING", described, StringComparison.Ordinal);
        Assert.Contains("nowhere to be delivered", described, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deployment_with_both_halves_configured_reports_them_without_a_warning()
    {
        using var host = WithUpstream().WithSmtpIngress("example.test");

        var described = HostServices.DescribeTransport(host.ConfigurationSnapshot);

        Assert.Contains("delivery worker running", described, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", described, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unconfigured_deployment_delivers_nothing_and_is_not_warned_at()
    {
        // Nothing is configured, so nothing is missing. A warning here would be noise on the common
        // deployment shape and would train the reader to ignore the line.
        using var host = new TestHost();

        var described = HostServices.DescribeTransport(host.ConfigurationSnapshot);

        Assert.Contains("listener disabled", described, StringComparison.Ordinal);
        Assert.Contains("delivery worker NOT running", described, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host with a delivery target that will never be dialled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The address is documentation-reserved on purpose, rather than a probed-then-released port.</b>
    /// <c>192.0.2.0/24</c> is TEST-NET-1 (RFC 5737): a block reserved for documentation, never
    /// assigned to a real host, and never routable. That makes it impossible for this target to
    /// reach anything, which is what a test that must not deliver actually needs.
    /// </para>
    /// <para>
    /// This replaced a helper that bound <c>127.0.0.1:0</c>, read the port, and released it before
    /// handing the number to the host. That is a probe-then-release race: between the release and
    /// whatever binds later, here, a parallel test class, the operating system is free to hand the
    /// port to someone else. It was a genuine latent bug, flagged by two other lanes. Nothing ever
    /// dialled it, so it never fired, which is exactly what made it worth removing rather than
    /// documenting: a race that cannot fire today is one that fires the day a test acquires a reason
    /// to dial.
    /// </para>
    /// <para>
    /// Nothing contacts this target in these tests: the queue is empty, so the worker idles without
    /// ever opening a session, and the point here is that it <em>is</em> running.
    /// </para>
    /// </remarks>
    private static TestHost WithUpstream()
    {
        var host = new TestHost();

        host.Configure("StyloMail:Transport:Upstream:Host", "192.0.2.1");
        host.Configure("StyloMail:Transport:Upstream:Port", "25");
        host.Configure("StyloMail:Transport:Upstream:Tls", "None");

        return host;
    }
}
