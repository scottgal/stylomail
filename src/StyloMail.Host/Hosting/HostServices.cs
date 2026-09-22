using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using System.Text;
using StyloMail.Assessment;
using StyloMail.Core;
using StyloMail.Host.Assessors;
using StyloMail.Host.Auth;
using StyloMail.Host.Controls;
using StyloMail.Host.Decisions;
using StyloMail.Host.Feedback;
using StyloMail.Host.Observability;
using StyloMail.Host.Storage;
using StyloMail.Host.Submissions;
using StyloMail.Adaptive.Profiles;
using StyloMail.Jev;
using StyloMail.Mime;
using StyloMail.Persistence;
using StyloMail.Queue;
using StyloMail.Transport.Cloudflare;
using StyloMail.Transport.Ingress;

namespace StyloMail.Host.Hosting;

/// <summary>
/// The host's composition root, shared by the web server and the CLI.
/// </summary>
/// <remarks>
/// Shared deliberately. A CLI that assembled its own dependencies would be a second, quietly
/// divergent definition of what the system is, and the first thing to drift would be a safety
/// default like the storage path or the refusing assessor.
/// </remarks>
public static class HostServices
{
    public static IServiceCollection AddStyloMailHost(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHostAuthentication(configuration);

        // Enums travel as names on the wire. "Quarantine" is stable and readable in a ledger entry
        // in a way that "2" is not, and a reordered enum must never silently change the meaning of
        // a stored or in-flight decision.
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IMimeMessageAnalyzer, BoundedMimeMessageAnalyzer>();

        services.Configure<HostStorageOptions>(configuration.GetSection(HostStorageOptions.SectionName));
        services.AddSingleton<HostDatabase>();
        services.AddSingleton<IDecisionLedger, SqliteDecisionLedger>();
        services.AddSingleton<IFeedbackStore, SqliteFeedbackStore>();
        services.AddSingleton<ISenderControlStore, SqliteSenderControlStore>();

        // The durable queue, wired from the same storage options so the spool and the database
        // cannot be pointed at different places by two independently-edited configuration keys.
        services.AddSingleton(sp =>
        {
            var storage = sp.GetRequiredService<IOptions<HostStorageOptions>>().Value;
            return new SqliteConnectionFactory(storage.DatabasePath);
        });
        services.AddSingleton(sp =>
        {
            var storage = sp.GetRequiredService<IOptions<HostStorageOptions>>().Value;
            return new SpoolStore(storage.SpoolRoot);
        });
        services.AddSingleton(sp => new QueueOptions { TimeProvider = sp.GetRequiredService<TimeProvider>() });
        services.AddSingleton<QueueStore>();
        services.AddSingleton<ISubmissionIntake, QueueSubmissionIntake>();

        services.AddSingleton<HostMetrics>();
        services.AddSingleton<ReadinessProbe>();

        // Registered unconditionally so the host always has something to resolve. The default
        // refuses every request rather than permitting one: see UnavailableMailAssessor for why a
        // permissive default is the one option that is genuinely unsafe.
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IMailAssessor>(sp => BuildAssessor(sp, configuration));

        AddTransport(services, configuration);

        return services;
    }

    /// <summary>
    /// Wires the mail boundary: what this deployment ingests from, and where it delivers to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here is driven by configuration and everything defaults to off. An unconfigured
    /// deployment is the HTTP host and the CLI with no SMTP port open, no connector, and nothing
    /// dialling an upstream, which is the right default for a component whose whole purpose is to
    /// sit on a mail boundary.
    /// </para>
    /// <para>
    /// <b>The two composition assertions run here, when the components are built</b>, not per
    /// request. Both describe a property of a <em>pair</em> of components that no reader of either
    /// one can see, and both fail with the two values named, see <see cref="IngressComposition"/>.
    /// </para>
    /// </remarks>
    private static void AddTransport(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HostTransportOptions>(configuration.GetSection(HostTransportOptions.SectionName));

        // One sink for both ingresses, resolved from the container. Two sinks that could disagree
        // about what acceptance means is exactly the situation the queue's "acceptance is a queue
        // id" rule exists to prevent, so there is one sink, one decision type, and one rule about
        // what a 250 may be built from.
        services.AddSingleton<ISmtpIngressSink>(sp =>
        {
            var transport = sp.GetRequiredService<IOptions<HostTransportOptions>>().Value;
            var queueOptions = sp.GetRequiredService<QueueOptions>();

            RequireIngressBounds(transport, queueOptions);

            var spool = sp.GetRequiredService<SpoolStore>();

            // The assessor arrives through the container rather than being built here, so that the
            // sink is exercised against whatever assessor this host actually has.
            var sink = new HostIngressSink(
                sp.GetRequiredService<IMailAssessor>(),
                spool,
                sp.GetRequiredService<TimeProvider>());

            // Passes today by construction, and exists so that the day someone gives the sink its
            // own spool it fails here, loudly, with both roots named, rather than as mail the
            // assessor cannot read back.
            IngressComposition.RequireSharedSpool(sink.Spool, spool, "the assessment pipeline");
            IngressComposition.RequireSpoolRoot(
                spool,
                sp.GetRequiredService<IOptions<HostStorageOptions>>().Value.SpoolRoot);

            return sink;
        });

        services.AddSingleton<ISubmissionAuthenticator, PrincipalSubmissionAuthenticator>();

        // The Cloudflare connector is a request-shaped intake, not a listener: nothing here opens a
        // port for it. Registered so the composition root owns exactly one of it, built over the
        // same sink as the SMTP path, two intake paths that could disagree about acceptance is the
        // situation the queue's "acceptance is a queue id" rule exists to prevent.
        //
        // Its secret comes from the environment and never from configuration: see
        // HostCredentials.CloudflareIngressSecretEnvironmentVariable for why the name is defined
        // once. Enabled without one refuses to start here rather than serving 401s, because a route
        // that always refuses sends the operator to inspect the Worker while the fault is this end.
        services.AddSingleton(sp =>
        {
            var cloudflare = sp.GetRequiredService<IOptions<HostTransportOptions>>().Value.CloudflareIngress;
            var secret = HostCredentials.CloudflareIngressSecretFromEnvironment();

            HostCredentials.RequireCloudflareIngressSecretIfEnabled(secret, cloudflare.Enabled);

            return new CloudflareEmailRoutingConnector(
                cloudflare.Build(secret),
                sp.GetRequiredService<ISmtpIngressSink>(),
                sp.GetRequiredService<TimeProvider>());
        });

        // Registered as a singleton and then as a hosted service, rather than with
        // AddHostedService<T>() directly, so a test can resolve the instance the host is running and
        // read the port it actually bound.
        services.AddSingleton<SmtpIngressHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SmtpIngressHostedService>());

        // Same shape as the listener above, and for the same reason: registering it as a singleton
        // first means a test can resolve the instance the host is running rather than only the
        // interface the host starts it through.
        services.AddSingleton<QueueDeliveryWorkerOptions>();
        services.AddSingleton<QueueDeliveryHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<QueueDeliveryHostedService>());
    }

    /// <summary>
    /// Every configured ingress bound must fit inside the queue's payload bound.
    /// </summary>
    /// <remarks>
    /// Checked for both ingresses whether or not they are enabled, because the values are
    /// configuration and a drift between them is a configuration mistake. It is the pair that is
    /// wrong, and neither component can see the other, which is why this is asserted rather than
    /// left to the symptom, a capacity deferral that looks like spool pressure.
    /// </remarks>
    private static void RequireIngressBounds(HostTransportOptions transport, QueueOptions queueOptions)
    {
        IngressComposition.RequireIngressFitsQueue(
            "StyloMail:Transport:SmtpIngress:MaxMessageBytes",
            transport.SmtpIngress.MaxMessageBytes,
            queueOptions);

        IngressComposition.RequireIngressFitsQueue(
            "StyloMail:Transport:CloudflareIngress:MaxMessageBytes",
            transport.CloudflareIngress.MaxMessageBytes,
            queueOptions);
    }

    /// <summary>
    /// Whether an ingress is switched on, and whether this deployment has somewhere to deliver to.
    /// </summary>
    /// <remarks>
    /// Read from configuration rather than from the built components, because the answer is needed to
    /// decide <em>which</em> components to build. A caller that wants to be told the consequences
    /// should ask for <see cref="DescribeTransport"/> instead.
    /// </remarks>
    public static bool IsIngressEnabled(IConfiguration configuration)
        => configuration.GetSection($"{HostTransportOptions.SectionName}:SmtpIngress:Enabled").Get<bool>();

    /// <summary>Whether a delivery target is configured.</summary>
    public static bool IsDeliveryConfigured(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(
            configuration.GetSection($"{HostTransportOptions.SectionName}:Upstream:Host").Value);

    /// <summary>
    /// The transport decisions this deployment made, for the startup log.
    /// </summary>
    /// <remarks>
    /// <b>A deployment that accepts mail and cannot deliver it must say so out loud.</b> Nothing
    /// below is an error the host refuses to start over, an inbound-only deployment and a
    /// submission-only one are both legitimate shapes, but a listener that writes mail into a queue
    /// nothing drains is a configuration mistake that is otherwise discovered as messages ageing
    /// toward their expiry. Stated at startup, it is a line in a log instead.
    /// </remarks>
    public static string DescribeTransport(IConfiguration configuration)
    {
        var ingress = IsIngressEnabled(configuration);
        var delivery = IsDeliveryConfigured(configuration);
        var cloudflare = configuration
            .GetSection($"{HostTransportOptions.SectionName}:CloudflareIngress:Enabled").Get<bool>();

        var listener = ingress
            ? "SMTP submission listener enabled"
            : "SMTP submission listener disabled";

        var sink = cloudflare
            ? "; Cloudflare Email Routing connector configured (no HTTP intake route is mapped for it)"
            : string.Empty;

        var egress = delivery
            ? "delivery worker running"
            : "delivery worker NOT running (no StyloMail:Transport:Upstream:Host configured)";

        var warning = ingress && !delivery
            ? " WARNING: mail accepted by this deployment has nowhere to be delivered and will be "
              + "held until it expires."
            : string.Empty;

        return $"{listener}; {egress}{sink}.{warning}";
    }

    /// <summary>
    /// Builds the assessor, or the refusing sentinel when this deployment has not configured
    /// Assessment at all.
    /// </summary>
    /// <remarks>
    /// <b>Building the pipeline is `assess-`'s; registering it is the host's</b> (overview-'s
    /// ruling). The host is the only component that knows what a deployment has configured, which
    /// is exactly why the choice lives here.
    ///
    /// <para>
    /// The sentinel is not a placeholder to delete. It is the correct behaviour for a deployment
    /// with no Assessment configuration, and its 503-with-a-reason is the whole point: an
    /// unexamined message reported as "Allow" would be worse than a visible outage, because
    /// everything downstream treats an assessment as having happened.
    /// </para>
    /// </remarks>
    private static IMailAssessor BuildAssessor(IServiceProvider services, IConfiguration configuration)
    {
        // Throws on a half-configured or too-short secret. That is deliberate: a deployment that
        // silently ran without pseudonymisation would look healthy while quietly collapsing tenant
        // isolation, which is the exact failure mode this codebase spent the day hunting.
        var state = HostCredentials.ResolveFromEnvironment(out var jevApiKey, out var profileMasterKey);

        if (state == CredentialState.NotConfigured)
        {
            return new UnavailableMailAssessor();
        }

        var options = new MailAssessorOptions
        {
            ProfileKeyHasher = new ProfileKeyHasher(Encoding.UTF8.GetBytes(profileMasterKey!)),
        };

        var classifier = new JevSemanticMailClassifier(
            services.GetRequiredService<HttpClient>(),
            new JevOptions { ApiKey = jevApiKey! },
            services.GetRequiredService<TimeProvider>());

        return AssessmentPipeline.Create(
            services.GetRequiredService<IMimeMessageAnalyzer>(),
            classifier,
            services.GetRequiredService<SqliteConnectionFactory>(),
            services.GetRequiredService<SpoolStore>(),
            options,
            services.GetRequiredService<QueueOptions>());
    }

    /// <summary>
    /// Creates every schema the host relies on, in the one database file they share.
    /// </summary>
    /// <remarks>
    /// All three schemas are created here because in this deployment they are one SQLite file:
    /// the queue's, the adaptive persistence's, and the host's own. A host that started serving
    /// before its tables existed would answer requests with a storage error that looks like an
    /// outage rather than the startup fault it is.
    /// </remarks>
    public static async Task InitialiseStorageAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        services.GetRequiredService<HostDatabase>().EnsureCreated();

        using (var connection = services.GetRequiredService<SqliteConnectionFactory>().Open())
        {
            // Profiles, trusted baselines and the campaign store live in the persistence schema.
            SqliteSchema.EnsureCreated(connection);
        }

        await services.GetRequiredService<QueueStore>().InitializeAsync(cancellationToken).ConfigureAwait(false);
    }
}
