using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StyloMail.Host.Hosting;
using StyloMail.Host.Storage;
using StyloMail.Queue;
using StyloMail.Transport.Ingress;

namespace StyloMail.Host.Tests;

/// <summary>
/// The two couplings between the ingress and components it does not own.
/// </summary>
/// <remarks>
/// <para>
/// Both describe a property of a <em>pair</em>, and both fail in a way whose symptom points
/// somewhere else: an over-large ingress bound surfaces as a capacity deferral that looks like spool
/// pressure, and two spool roots surface as mail that was accepted and cannot be read back. Neither
/// is visible to a reader of one component, which is the whole argument for asserting them.
/// </para>
/// <para>
/// Every test here therefore does two things: it checks the guard passes when the pair is
/// consistent, and that it fails loudly when it is not. A guard nobody has seen fail is a comment.
/// </para>
/// </remarks>
public sealed class IngressCompositionTests
{
    [Fact]
    public void An_ingress_bound_that_matches_the_queue_is_accepted()
    {
        var queue = new QueueOptions { MaxPayloadBytes = 1024 };

        // Equal is the deployment's actual state today, and the boundary case on purpose: the rule
        // is "no larger than", so equality must pass rather than be treated as suspicious.
        IngressComposition.RequireIngressFitsQueue("StyloMail:Transport:SmtpIngress:MaxMessageBytes", 1024, queue);
    }

    [Fact]
    public void An_ingress_bound_larger_than_the_queue_names_both_values()
    {
        // The failure has to name both numbers. The whole difficulty of this coupling is that the
        // reader sees one of them and concludes the component is fine.
        var queue = new QueueOptions { MaxPayloadBytes = 4096 };

        var failure = Assert.Throws<InvalidOperationException>(() =>
            IngressComposition.RequireIngressFitsQueue(
                "StyloMail:Transport:SmtpIngress:MaxMessageBytes",
                8192,
                queue));

        Assert.Contains("8192", failure.Message, StringComparison.Ordinal);
        Assert.Contains("4096", failure.Message, StringComparison.Ordinal);
        Assert.Contains("StyloMail:Transport:SmtpIngress:MaxMessageBytes", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_spool_used_by_both_halves_is_accepted()
    {
        var root = TempSpool();
        try
        {
            var spool = new SpoolStore(root);

            IngressComposition.RequireSharedSpool(spool, spool, "the assessment pipeline");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Two_spool_instances_are_refused_even_over_the_same_directory()
    {
        // Deliberately the *same path* on both sides. A path comparison would pass this and the
        // harm would survive: the rule is that there is one instance, because a later
        // delete-after-accept or orphan sweep reasons about the object it holds, not about where
        // that object happens to point today.
        var root = TempSpool();
        try
        {
            var pipelineSpool = new SpoolStore(root);
            var ingressSpool = new SpoolStore(root);

            var failure = Assert.Throws<InvalidOperationException>(() =>
                IngressComposition.RequireSharedSpool(ingressSpool, pipelineSpool, "the assessment pipeline"));

            Assert.Contains(root, failure.Message, StringComparison.Ordinal);
            Assert.Contains("spool", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_spool_rooted_away_from_the_configured_root_is_refused()
    {
        var configured = TempSpool();
        var elsewhere = TempSpool();

        try
        {
            var failure = Assert.Throws<InvalidOperationException>(() =>
                IngressComposition.RequireSpoolRoot(new SpoolStore(elsewhere), configured));

            // Both roots named, because "the spool is wrong" is not actionable and "the spool is at
            // X but configured as Y" is.
            Assert.Contains(Path.GetFullPath(elsewhere), failure.Message, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(configured), failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(configured);
            Cleanup(elsewhere);
        }
    }

    [Fact]
    public void The_host_hands_the_ingress_sink_the_same_spool_instance_it_gives_the_pipeline()
    {
        using var host = new TestHost();

        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();
        var compositionSpool = host.Services.GetRequiredService<SpoolStore>();

        Assert.IsType<HostIngressSink>(sink);
        Assert.Same(compositionSpool, ((HostIngressSink)sink).Spool);
        Assert.Equal(
            Path.GetFullPath(host.Services.GetRequiredService<IOptions<HostStorageOptions>>().Value.SpoolRoot),
            Path.GetFullPath(compositionSpool.Root));
    }

    [Fact]
    public void The_ingress_sink_is_a_single_instance_shared_by_both_ingresses()
    {
        // Two sinks that could disagree about what acceptance means is the situation the queue's
        // "acceptance is a queue id" rule exists to prevent, so there is one, and both ingresses
        // are built over it.
        using var host = new TestHost();

        Assert.Same(
            host.Services.GetRequiredService<ISmtpIngressSink>(),
            host.Services.GetRequiredService<ISmtpIngressSink>());
    }

    [Fact]
    public void A_deployment_whose_ingress_accepts_more_than_the_queue_allows_does_not_start()
    {
        // The end-to-end form of the first assertion: not "the guard throws", but "this deployment
        // cannot come up". A listener that reads a 40 MB message it can never store is worse than a
        // host that refuses to start, because the first one looks like it is working.
        using var host = new TestHost()
            .WithSmtpIngress("example.test")
            .WithQueueOptions(new QueueOptions { MaxPayloadBytes = 4096 });

        var failure = Assert.ThrowsAny<Exception>(() => host.Services.GetRequiredService<ISmtpIngressSink>());

        Assert.Contains(
            "4096",
            Describe(failure),
            StringComparison.Ordinal);
    }

    /// <summary>The whole exception chain, flattened, so a wrapped composition failure is still read.</summary>
    private static string Describe(Exception exception)
    {
        var text = new System.Text.StringBuilder();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }

    private static string TempSpool() =>
        Path.Combine(Path.GetTempPath(), "stylomail-spool-tests", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A test that leaves a handle open should not fail teardown for it.
        }
    }
}
