using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Text;
using StyloMail.Assessment;
using StyloMail.Core;
using StyloMail.Host.Assessors;
using StyloMail.Host.Auth;
using StyloMail.Host.Chat;
using StyloMail.Host.Controls;
using StyloMail.Host.Decisions;
using StyloMail.Host.Feedback;
using StyloMail.Host.Observability;
using StyloMail.Host.Storage;
using StyloMail.Host.Submissions;
using StyloMail.Host.Traffic;
using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Storage;
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
        services.AddSingleton<ISenderProfileStore, SqliteSenderProfileStore>();
        services.AddSingleton<ICompanyStore, SqliteCompanyStore>();

        // The emergency stop, registered unconditionally. Before this existed the only policy
        // context source supplied nothing, so the control the specification lists second in policy
        // precedence was false on every assessment in every deployment and could not be engaged at
        // all. Registered here rather than behind a flag for the same reason: a safety control that
        // has to be configured into existence is one that is absent from every deployment that did
        // not read the documentation.
        // Registered in its own right as well as behind the port, on the same pattern as the traffic
        // seam: the port is what the pipeline reads, and the type is what engages it so a caller
        // never has to downcast an interface to pull the stop.
        services.AddSingleton<SqliteEmergencyKillSwitch>();
        services.AddSingleton<IEmergencyKillSwitch>(
            sp => sp.GetRequiredService<SqliteEmergencyKillSwitch>());

        // Registered unconditionally, unlike the endpoint it serves. The store is inert unless the
        // Slack ingress is enabled, and a route that is mapped or not is a different question from
        // whether the durable hand-off exists.
        services.AddSingleton<IChatIntakeStore, SqliteChatIntakeStore>();

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

        // Whether the semantic provider has rejected this deployment's credential. Registered
        // unconditionally and mutated by the classifier decorator, because the condition can arise at
        // any point in a run rather than only at startup.
        services.AddSingleton<ProviderCredentialHealth>();
        services.AddSingleton<ReadinessProbe>();

        // Registered unconditionally so the host always has something to resolve. The default
        // refuses every request rather than permitting one: see UnavailableMailAssessor for why a
        // permissive default is the one option that is genuinely unsafe.
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IMailAssessor>(sp => BuildAssessor(sp, configuration));

        // The chat assessment path and its drain. Registered unconditionally and decided at
        // resolution, for the reason the traffic port below records: this composition root runs
        // before a test host layers its own configuration in, so gating here reads the wrong values
        // and silently registers nothing. The drain checks whether the intake is enabled itself, and
        // resolves the assessor only after that check, so a deployment with no chat never builds one.
        services.AddSingleton<IAdaptiveProfileStore>(sp =>
        {
            var profiles = new SqliteAdaptiveProfileStore(
                sp.GetRequiredService<SqliteConnectionFactory>());

            // Safe to call on every start, and called here rather than left to whichever component
            // happens to need it first, so a chat-only deployment still has the table its
            // assessments write into.
            profiles.EnsureCreated();

            return new SqliteAdaptiveProfileStoreAdapter(profiles);
        });

        // Held rather than inferred at the point of use, because the reason chat cannot assess is a
        // property of how the process was configured and the readiness surface has to be able to say
        // so without re-deriving it.
        services.AddSingleton<ChatAssessmentHealth>();
        services.AddSingleton<IChatAssessor>(BuildChatAssessor);

        // The write, separable from the assessment. Registered unconditionally and resolved only by
        // the drain's dismissal path, so a deployment with no profile master key still starts: the
        // drain's per-event guard leaves those events waiting rather than losing them.
        services.AddSingleton<ChatObservationRecorder>(sp => new ChatObservationRecorder(
            sp.GetRequiredService<IAdaptiveProfileStore>(),
            new MailAssessorOptions
            {
                ProfileKeyHasher = new ProfileKeyHasher(
                    Encoding.UTF8.GetBytes(ProfileKeyMaterial(sp))),
            }));
        services.AddHostedService<ChatIntakeDrain>();

        AddTransport(services, configuration);
        AddTrafficEvents(services, configuration);

        return services;
    }

    /// <summary>
    /// Wires the live-traffic seam: what announces that something changed, if anything does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, and the off path is the no-op port rather than a null.</b> Every call site
    /// publishes identically whether the feature is on or off, so enabling it cannot change what any
    /// mail path does, and a deployment that never configures it resolves something that answers
    /// every call by doing nothing.
    /// </para>
    /// <para>
    /// <b>The choice is made when the port is resolved, not here.</b> The host's composition root
    /// runs before a test host layers its own configuration in, so a decision taken at registration
    /// reads the wrong values. The same reason <c>SmtpIngress:Enabled</c> is honoured when the
    /// listener starts rather than when it is registered.
    /// </para>
    /// <para>
    /// <b><c>AddSignalR</c> is called unconditionally, and the route is not.</b> Adding the services
    /// opens no port and maps no endpoint; whether this deployment offers a feed is decided where
    /// the configuration is final, beside the Cloudflare intake's route, which is the same decision
    /// shape. A deployment that has not enabled the feature is a host with no such route.
    /// </para>
    /// </remarks>
    private static void AddTrafficEvents(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TrafficOptions>(configuration.GetSection(TrafficOptions.SectionName));
        services.Configure<SlackIngressOptions>(configuration.GetSection(SlackIngressOptions.SectionName));

        // Enums travel as names here for the same reason they do on the HTTP surface, and it has to
        // be said twice because SignalR writes its own serializer: the console switches on the
        // kind, so a numeric payload would silently repoint every live screen if these members were
        // ever reordered.
        services.AddSignalR()
            .AddJsonProtocol(options => options
                .PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddSingleton<ITrafficEvents>(sp =>
            sp.GetRequiredService<IOptions<TrafficOptions>>().Value.Enabled
                ? sp.GetRequiredService<SignalRTrafficEvents>()
                : NullTrafficEvents.Instance);

        // Registered in its own right as well as behind the port above, so that enabling the
        // feature is the only thing that constructs it and a test can hand it a hub that fails.
        services.AddSingleton<SignalRTrafficEvents>(sp =>
            new SignalRTrafficEvents(sp.GetRequiredService<IHubContext<TrafficHub>>()));
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
    /// <summary>
    /// Builds the chat assessor.
    /// </summary>
    /// <remarks>
    /// <b>It needs the profile master key, and refuses to start without it.</b> Every chat profile
    /// key is a pseudonym, exactly as every mail profile key is, and a deployment that ran without
    /// one would be keying profiles on an author's platform identifier: a store that identifies
    /// people directly cannot later honour a deletion request without knowing every derived copy.
    /// That is the same reason the mail path throws here rather than degrading.
    ///
    /// <para>
    /// Note what it does <em>not</em> need: a semantic provider credential. Chat is local-only by
    /// design, so the Jev key being absent is a supported deployment for this path even though it
    /// leaves the mail path with no assessor at all.
    /// </para>
    /// </remarks>
    /// <summary>The profile master key, or a startup refusal naming why it is needed.</summary>
    private static string ProfileKeyMaterial(IServiceProvider services)
    {
        HostCredentials.ResolveFromEnvironment(out _, out var profileMasterKey);

        if (string.IsNullOrWhiteSpace(profileMasterKey))
        {
            throw new InvalidOperationException(
                "Recording chat observations requires the profile master key, because every profile "
                + "key is a pseudonym and a deployment without one would key profiles on an author's "
                + "platform identifier.");
        }

        return profileMasterKey;
    }

    private static IChatAssessor BuildChatAssessor(IServiceProvider services)
    {
        HostCredentials.ResolveFromEnvironment(out _, out var profileMasterKey);

        // Degrades rather than refusing to build, so a deployment that has not configured chat can
        // still start. What stops that degrading into silence is on the other side: the intake drain
        // leaves events waiting when the assessment throws, so nothing is consumed unassessed.
        if (string.IsNullOrWhiteSpace(profileMasterKey))
        {
            // Recorded so the readiness surface can name it. Without this the state is visible only
            // as events accumulating, which reads the same as a drain that is merely busy.
            services.GetRequiredService<ChatAssessmentHealth>().IsUnavailable = true;

            return new UnavailableChatAssessor();
        }

        return new ChatAssessor(
            services.GetRequiredService<IAdaptiveProfileStore>(),
            new MailAssessorOptions
            {
                ProfileKeyHasher = new ProfileKeyHasher(Encoding.UTF8.GetBytes(profileMasterKey)),
            },
            services.GetRequiredService<IEmergencyKillSwitch>());
    }

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

        var jevOptions = BuildJevOptions(
            configuration,
            jevApiKey!,
            services.GetRequiredService<ILoggerFactory>().CreateLogger("StyloMail.Host.Jev"));

        var clock = services.GetRequiredService<TimeProvider>();

        // Wrapped so a rejected credential reaches readiness, which is the thing an operator watches.
        // The wrapper changes nothing a caller sees: it rethrows unchanged. See
        // CredentialAwareSemanticClassifier for why the exception stays loud.
        var classifier = new CredentialAwareSemanticClassifier(
            new JevSemanticMailClassifier(
                services.GetRequiredService<HttpClient>(),
                jevOptions,
                clock),
            services.GetRequiredService<ProviderCredentialHealth>(),
            clock);

        return AssessmentPipeline.Create(
            services.GetRequiredService<IMimeMessageAnalyzer>(),
            classifier,
            services.GetRequiredService<SqliteConnectionFactory>(),
            services.GetRequiredService<SpoolStore>(),
            options,
            services.GetRequiredService<QueueOptions>(),

            // Without this the pipeline falls back to the source that supplies nothing, which is
            // how the emergency stop came to be false on every assessment in every deployment.
            // Named rather than positional: it sits behind two optional parameters, and a later
            // insertion would silently repoint it at the wrong one.
            policyContext: new HostPolicyContextSource(
                services.GetRequiredService<IEmergencyKillSwitch>()));
    }

    /// <summary>
    /// Builds the semantic provider's options, binding the endpoint and model from configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These were hardcoded while <paramref name="configuration"/> was in scope as a parameter, so
    /// <c>StyloMail:Jev:Endpoint</c> and <c>:Model</c> could be set, looked accepted, and were
    /// silently ignored. Configuration that appears to work and does nothing is worse than
    /// configuration that is absent.
    /// </para>
    /// <para>
    /// <b>The endpoint redirects message content, so a non-default one is announced at startup.</b>
    /// Pointing it somewhere else is a legitimate operator decision: a local classifier, a staging
    /// provider, or deliberately unreachable to exercise the semantic-unavailable path, but it is
    /// also the setting that decides who receives the mail this deployment processes. It is not
    /// buried in a config file; it is a line in the log of every boot.
    /// </para>
    /// <para>
    /// The model pin is bound for the opposite reason: it is tuned alongside confidence thresholds,
    /// and an alias there would move without notice and silently invalidate memoised assessments. A
    /// value is only ever *changed* deliberately, and the log says which one is in use.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Public for the same reason <see cref="DescribeTransport"/> and <see cref="IsIngressEnabled"/>
    /// are: this is a composition decision a test should be able to assert on directly rather than by
    /// inferring it from a side effect. The rest of the reasoning is on the internal overload.
    /// </remarks>
    public static JevOptions BuildJevOptions(IConfiguration configuration, string apiKey, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var defaults = new JevOptions();
        var endpoint = configuration["StyloMail:Jev:Endpoint"];
        var model = configuration["StyloMail:Jev:Model"];

        if (!string.IsNullOrWhiteSpace(endpoint)
            && !string.Equals(endpoint, defaults.Endpoint, StringComparison.Ordinal))
        {
            // A warning rather than an error. Redirecting the endpoint is a decision an operator is
            // entitled to make; it is the *silence* about it that would be wrong.
            logger.LogWarning(
                "STYLOMAIL JEV ENDPOINT OVERRIDDEN: message content is being sent to {Endpoint} "
                + "rather than {Default}. This is the setting that decides who receives the mail this "
                + "deployment processes; confirm it is intended.",
                endpoint,
                defaults.Endpoint);
        }

        return new JevOptions
        {
            ApiKey = apiKey,
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? defaults.Endpoint : endpoint,
            Model = string.IsNullOrWhiteSpace(model) ? defaults.Model : model,
        };
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
