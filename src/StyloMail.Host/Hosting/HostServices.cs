using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Text;
using StyloMail.Assessment;
using StyloMail.Assessment.Semantic;
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
using StyloMail.Nimble;
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

        // The one place the assessment secrets are read. Registered unconditionally and read lazily
        // when the assessor is built, so nothing here touches the environment while the container is
        // being populated.
        services.AddSingleton<IAssessmentSecretSource, EnvironmentAssessmentSecretSource>();

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
        var profileMasterKey = services.GetRequiredService<IAssessmentSecretSource>().Read().ProfileMasterKey;

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
        // Read through the one source, and deliberately without applying the mail path's decision:
        // the hosted provider's key is not half of anything here, so a deployment that selected it
        // and set a key while leaving the master key unset must not be told it is "half-configured"
        // for a provider the chat path never asks. What the chat path needs is exactly what it asks
        // for below: the master key.
        var profileMasterKey = services.GetRequiredService<IAssessmentSecretSource>().Read().ProfileMasterKey;

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

    /// <summary>
    /// Builds the assessor for the provider this deployment selected, or the refusing one when it
    /// has no usable credentials for that provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which provider, and therefore which secrets are required, is a decision made here.</b> The
    /// provider comes from configuration (<see cref="AssessmentProviderSelection"/>, hosted Jev by
    /// default) and the secrets come from the environment, once, through the one source that reads
    /// them. What a pair of secret values then means is
    /// <see cref="HostCredentials.ResolveForProvider"/>'s decision, because it is a property of the
    /// provider and not of the host.
    /// </para>
    /// <para>
    /// A build failure here is a refusal, deliberately: a deployment that silently ran without
    /// pseudonymisation would look healthy while quietly collapsing tenant isolation, which is the
    /// exact failure mode this codebase spent the day hunting. A <em>missing</em> configuration is
    /// the other answer, and it is not a refusal: the host starts on the refusing assessor, and
    /// <c>/health/ready</c> names it, so nothing about the state is mistaken for health.
    /// </para>
    /// </remarks>
    private static IMailAssessor BuildAssessor(IServiceProvider services, IConfiguration configuration)
    {
        var logger = services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("StyloMail.Host.Assessment");

        var provider = AssessmentProviderSelection.Select(configuration);
        var secrets = services.GetRequiredService<IAssessmentSecretSource>().Read();

        // Throws on a half-configured or too-short secret, where "half-configured" now depends on
        // the provider: the hosted provider needs both secrets, and the local one holds no
        // credential so it needs only the master key. Resolution never logs and never returns the
        // values; the record it reads them from redacts itself if anything stringifies it.
        var state = HostCredentials.ResolveForProvider(
            provider,
            secrets.JevApiKey,
            secrets.ProfileMasterKey);

        if (state == CredentialState.NotConfigured)
        {
            // The absence is announced rather than left to be inferred from refusals later. It names
            // the one secret this deployment is missing, which is the master key under either
            // provider, so the operator has one thing to fix and not a pair to guess between.
            logger.LogWarning(
                "STYLOMAIL ASSESSMENT NOT CONFIGURED: provider {Provider} is selected but {MasterKey} "
                + "is not set, so no assessor was built and every assessment will refuse. "
                + "/health/ready reports {Check}.",
                provider,
                HostCredentials.ProfileKeyEnvironmentVariable,
                ReadinessProbe.AssessorUnavailable);

            return new UnavailableMailAssessor();
        }

        if (provider == AssessmentProvider.Nimble && !string.IsNullOrWhiteSpace(secrets.JevApiKey))
        {
            // Not a refusal: a deployment may carry a provider key it is not using, and refusing to
            // start over a secret nothing reads would hand the deployment's availability to someone
            // else's configuration. Saying it out loud is what stops it passing for a key that is
            // doing work, which would be the worse reading of the same state.
            logger.LogWarning(
                "StyloMail assessment: {Provider} is selected and {JevKey} is set, but the local "
                + "provider holds no credential and does not use it. Message content goes to the "
                + "local endpoint, not to the hosted provider.",
                provider,
                JevOptions.ApiKeyEnvironmentVariable);
        }

        if (provider == AssessmentProvider.NeverAsks && !string.IsNullOrWhiteSpace(secrets.JevApiKey))
        {
            // The sharper version of the case above: this deployment declares that nothing is ever
            // sent to a semantic provider, and it is holding a provider key anyway. Announced rather
            // than refused for the same reason, and announced rather than ignored because an
            // operator who has just turned the semantic tier off is exactly the operator who should
            // be told that a key is still sitting in the environment.
            logger.LogWarning(
                "StyloMail assessment: {Provider} is selected and {JevKey} is set, but this "
                + "deployment never asks a semantic provider, so no key is read and no message "
                + "content leaves it on the semantic path.",
                provider,
                JevOptions.ApiKeyEnvironmentVariable);
        }

        var options = new MailAssessorOptions
        {
            ProfileKeyHasher = new ProfileKeyHasher(Encoding.UTF8.GetBytes(secrets.ProfileMasterKey!)),
        };

        var clock = services.GetRequiredService<TimeProvider>();
        var http = services.GetRequiredService<HttpClient>();

        // One arm per provider, and a refusal for anything else rather than a fallback arm. An
        // operator cannot reach the refusal: Select validates the value before this point, so the
        // only way here is a provider added to the enum without a composition for it, and the
        // entry points resolve the assessor at startup, which is where it would surface. Falling back
        // to one of the arms instead would assess a deployment's mail with a provider nobody chose.
        ISemanticMailClassifier classifier = provider switch
        {
            AssessmentProvider.Nimble => new NimbleSemanticMailClassifier(
                http,
                BuildNimbleOptions(
                    configuration,
                    services.GetRequiredService<ILoggerFactory>().CreateLogger("StyloMail.Host.Nimble")),
                clock),

            // Wrapped so a rejected credential reaches readiness, which is the thing an operator
            // watches. The wrapper changes nothing a caller sees: it rethrows unchanged. See
            // CredentialAwareSemanticClassifier for why the exception stays loud. The local provider
            // needs no wrapper: it holds no credential, so there is no rejection to report, and its
            // outage already reaches a caller as Unavailable evidence.
            AssessmentProvider.Jev => new CredentialAwareSemanticClassifier(
                new JevSemanticMailClassifier(
                    http,
                    BuildJevOptions(
                        configuration,
                        secrets.JevApiKey!,
                        services.GetRequiredService<ILoggerFactory>().CreateLogger("StyloMail.Host.Jev")),
                    clock),
                services.GetRequiredService<ProviderCredentialHealth>(),
                clock),

            // The deployment's own declaration that it has no semantic provider. It is constructed
            // here rather than reached by leaving a provider unreachable, because the two produce
            // different evidence and only one of them may authorise a delivery on local evidence
            // alone: this emits NotApplicable rows, an outage emits Unavailable ones, and policy
            // refuses the second. The clock is passed so the rows carry the assessment's clock and a
            // fixed-clock replay writes the same stamps the run it replays did.
            AssessmentProvider.NeverAsks => new NeverAskingSemanticClassifier(clock),

            _ => throw new InvalidOperationException(
                $"No composition is defined for assessment provider '{provider}', so the host cannot "
                + "assess mail with it. Add its composition to BuildAssessor rather than letting it "
                + "fall back to a provider the operator did not select."),
        };

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
    /// Builds the local decision model's options, binding the endpoint and model from configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An endpoint that leaves this machine is announced, and it is the setting that matters
    /// most for this provider.</b> Staying local is the property the local model was selected for, so
    /// an endpoint elsewhere gives that up silently: the mail would be assessed, the decisions would
    /// look right, and content would be leaving the host. As with the hosted provider's endpoint,
    /// pointing it elsewhere is a decision an operator is entitled to make and the <em>silence</em>
    /// about it is what would be wrong.
    /// </para>
    /// <para>
    /// The defaults are the ones <see cref="NimbleOptions"/> carries, and they are measurements
    /// rather than preferences, including 11435 rather than Ollama's own 11434. The reasoning is on
    /// <see cref="NimbleOptions.Endpoint"/> and is worth reading before changing either.
    /// </para>
    /// <para>
    /// Public for the same reason <see cref="BuildJevOptions"/> is: a composition decision a test
    /// should be able to assert on directly rather than by inferring it from a request that was or
    /// was not made.
    /// </para>
    /// </remarks>
    public static NimbleOptions BuildNimbleOptions(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var defaults = new NimbleOptions();
        var endpoint = configuration["StyloMail:Nimble:Endpoint"];
        var model = configuration["StyloMail:Nimble:Model"];
        // These two are bound because they were NOT, and that cost two live experiments. This method
        // named only Endpoint and Model, so a `StyloMail:Nimble:EffectiveNumCtx` set by an operator,
        // a probe or a deployment reached nothing: the property stayed null, `AppliedContextWindow`
        // stayed at its derived half of `NumCtx`, and two runs came back byte-identical to the
        // baseline with the setting silently ignored and reported as a negative result.
        //
        // A configuration key nothing reads is worse than a missing one, because it looks like a knob.
        // `NumCtx` is bound with it rather than separately: it is the other half of the relation
        // (`AppliedContextWindow => EffectiveNumCtx ?? NumCtx / 2`), so binding the override alone
        // would leave the number it overrides unreachable and the pair impossible to reason about.
        //
        // WHY `appsettings.json` CARRIES A PIN, since this is the first appsettings file in this
        // project and the number is not self-explaining. 65536 is a floor above the largest evaluation
        // measured on a request shape the adapter can produce, which is **43202 tokens**: hex-ish,
        // twelve questions, and a **2500-character body** -- the FULL `MaxBodyCharacters` budget, so
        // the largest body this adapter sends. `nimble-`'s runs bound it from inside, and the arm that
        // sets the bound is the one AT the budget rather than the first one measured:
        //   2000-byte body  `.styloagent/scratch/nimble/expansion-shapes-2000.json`  hexish  37298
        //   2400-byte body  `.../expansion-shapes.json`                              hexish  42026
        //   2500-byte body  `.../expansion-shapes-2500.json`                         hexish  43202  <- binding
        // This paragraph cited the 2400-byte arm until `conversation-` enumerated the directory; the
        // command is `ls .styloagent/scratch/nimble/expansion-shapes*.json` and nobody ran it.
        //
        // And 46556, which briefly looked like a competing candidate, is NOT one: `probe-shift.py`
        // asks its `n_real` real questions PLUS the probe question, so its twelve-question arm is a
        // THIRTEEN-question request, and there are only TWELVE dimensions -- the artifact's own
        // `answers_returned = 13` was the tell that sat unread.
        //
        // AND THE 46556 READING IS STILL WORTH KEEPING, because it errs the safe way: the server read
        // a 46,556-token prompt IN FULL with the codeword at the START returning 0.9987, and that
        // size is ABOVE the reachable maximum. So it is evidence stronger than the range requires,
        // even though the fixture producing it is not a state the adapter sends. Both figures are
        // ENDPOINT-DIRECT -- inside the caps but outside the pipeline -- so NEITHER is a shipping-path
        // measurement, and that arm is `conversation-`'s and remains queued.
        //
        // The derived default
        // was `NumCtx / 2` = 4096, which refuses real mail -- a live run reported "unavailable: server
        // evaluated 14895 prompt tokens at an applied window of 4096". The cheap error is on the LOW
        // side, where a valid message loses its decision and the row looks the same either way; that
        // is why the pin clears the measurement rather than meeting it. It does NOT follow that a high
        // pin is free: above the server's true ceiling the server's behaviour is not established here.
        // `--context-shift` is on (MEASURED, not assumed: read from the endpoint's own command line
        // at 2026-10-01T23:22, recorded in `docs/running.md`), so an over-long prompt may be
        // continued over a shifted window and ANSWERED rather than refused, and that comes back
        // Available -- a silent failure rather than a loud one. The FLAG is a measurement; the step
        // from it to an `Available` row is DERIVED from what the flag does, not measured on this
        // deployment, and that is the same split the paragraph above keeps for the fit.
        //
        // So the claim this pin supports is the narrow one, that 65536 clears every
        // evaluation this repository has measured, and not the broad one that too-high is safe.
        // The densest shape anyone has TRIED, at the full body budget, reaches 43202, so this is a
        // floor and not a law, and an environment variable still overrides it. (46556 is higher but is
        // a THIRTEEN-question arm, which the dimension set cannot produce.)
        //
        // AND THIS PIN'S DELIVERY DEPENDS ON THE LAUNCHER'S WORKING DIRECTORY, which is the precise
        // form of it and NOT "the pin is inert". `WebApplication.CreateBuilder` and
        // `Host.CreateApplicationBuilder` both default the content root to the CWD, so a Host started
        // from a directory holding no `appsettings.json` read none. Measured as an A/B with one
        // variable, same instrument, same machine, same committed file, same output directory:
        //   cwd = the repo root   2026-10-02T00:25:37  `EffectiveNumCtx (unset, derived)`, applied 4096
        //   cwd = the bin dir     2026-10-02T00:29:08  `EffectiveNumCtx 65536`, applied 65536
        // The second is the first boot line that ever read 65536, and it appeared twenty-four seconds
        // after the first was reported as proof that the pin did not work. **The file carried 65536 the
        // whole time, so neither the VALUE nor the FILE was ever the problem, and an interim "the pin
        // is inert" is as wrong as "the pin is in force" would have been without the pair.**
        // `Program.cs` and `Cli/CliApplication.cs` now pin the content root to `AppContext.BaseDirectory`,
        // which removes the cwd dependence rather than correcting one launcher; the launcher's own cwd
        // is the second and now redundant half.
        //
        // Unparseable or absent means the default, which is the same shape the two keys above use.
        var numCtx = configuration["StyloMail:Nimble:NumCtx"];
        var effectiveNumCtx = configuration["StyloMail:Nimble:EffectiveNumCtx"];

        var options = new NimbleOptions
        {
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? defaults.Endpoint : endpoint,
            Model = string.IsNullOrWhiteSpace(model) ? defaults.Model : model,
            NumCtx = int.TryParse(numCtx, out var numCtxValue) ? numCtxValue : defaults.NumCtx,
            EffectiveNumCtx = int.TryParse(effectiveNumCtx, out var effectiveNumCtxValue)
                ? effectiveNumCtxValue
                : null,
        };

        // Both values, on every boot, so which model answered is a fact in the log rather than a
        // property of a deployment someone has to remember. The local reference cannot be pinned to
        // a resolved version (Ollama reports none), so the name in use is the only identity there is.
        logger.LogInformation(
            "StyloMail assessment provider: Nimble at {Endpoint} with model {Model}.",
            options.Endpoint,
            options.Model);

        // THE RESOLVED WINDOW, ON EVERY BOOT, and it is a separate line rather than more fields on
        // the one above because it is the field that has been missing.
        //
        // `AppliedContextWindow` is DERIVED (`EffectiveNumCtx ?? NumCtx / 2`), so the two keys that
        // reach this method do not show the number the guard actually refuses against, and a window
        // that is SET BUT NOT APPLIED is indistinguishable in any log from one that is applied and
        // exceeded. That ambiguity cost this lane two live experiments whose identical results were
        // read as a negative, and it cost the fleet a build freeze when a second lane could not tell
        // which of the two it was running under. A derived number that a guard depends on belongs
        // where every run can read it.
        logger.LogInformation(
            "StyloMail nimble window: NumCtx {NumCtx}, EffectiveNumCtx {EffectiveNumCtx}, "
            + "applied {AppliedContextWindow}.",
            options.NumCtx,
            options.EffectiveNumCtx?.ToString() ?? "(unset, derived)",
            options.AppliedContextWindow);

        // THE RULE AND THE MEASURED RELATION. THE LOG LINE STATES NO CONSEQUENCE; THE COMMENT DOES.
        //
        // This message has been rewritten FOUR times in one hour, and the sequence is the reason it
        // now says so little. Version one asserted a consequence unconditionally. Version two removed
        // that consequence, on the step that the fit's byte bound caps the server's count -- invalid,
        // because the evaluated count EXCEEDS the request's byte count (7246 bytes to 20642 tokens at
        // twelve questions, 2.85x, measured at eight points). Version three restored the consequence
        // and named a SUM, and the sum is not a measured mechanism: a sum of equal per-question blocks
        // is linear and the curve is not, since a line through n=1 and n=12 needs a NEGATIVE constant.
        //
        // Version four dropped the consequence as well, and that is the correction that took longest
        // to see. The fit SHORTENS THE REQUEST until it is at most NumCtx BYTES, so the sentence's own
        // subject is a fit-satisfying request; every arm anyone cited for the consequence -- the
        // 41198-byte refusal, the boundary sweep -- is OVER that cap and therefore not about the
        // subject at all. The one admissible witness is the adapter's own refusal, "server evaluated
        // 20498 prompt tokens at an applied window of 16384", which makes the consequence TRUE AT 16384
        // and said nothing about 32768, where a fit-satisfying request would need roughly four tokens
        // per byte. THAT GAP IS NOW CLOSED BY MEASUREMENT, and the section below under MEASURED is the
        // settlement rather than a bound -- cited by content rather than by position, because this
        // block has been rewritten six times tonight and a positional reference outlives its own
        // paragraph.
        //
        // So what is left is what is measured and witnessed: the fit counts BYTES of the request, the
        // guard compares a TOKEN count against the applied window, and the byte budget does not bound
        // the evaluation. Whether the guard reaches a fit-satisfying request is that comparison; the
        // boot line does not answer it, for a setting it cannot see, and the measurement below does.
        //
        // WHERE THE ANSWER LIVES, and the answer arrived while this comment was being written. What
        // follows is the SETTLEMENT rather than a caveat. The form that stood here for the previous
        // hour -- "UNRESOLVED at 32768, not excluded, not established" -- is WITHDRAWN, because a
        // measurement replaced it, and the paragraph below states both:
        //
        //   WITNESSED TRUE at an applied window of 16384. The guard FIRED there on an admissible
        //   request evaluating 20498 tokens, recorded in the adapter's own refusal reason.
        //   MEASURED REACHABLE at 32768. Five of six admissible BODY CONTENTS evaluate above 32768
        //   at a body size the adapter produces, which is WHY THE PIN BELOW IS 65536. At that landed
        //   value no measured body is refused, and the bound that still binds is the FIT's
        //   request-size rule rather than the window (see the exposure paragraph below).
        //
        // The arithmetic that PREDICTED it, kept because the reasoning and the result belong on the
        // record together: at a FIXED byte cap a SMALLER bytes-per-token ratio means MORE tokens, so
        // content that lowers the ratio raises the cap's evaluation TOWARD 32768 rather than away
        // from it. Reaching 32768 needs 8192 / 32768 = 0.25 bytes per token. The densest arm then
        // measured was 0.317, which sits ABOVE that threshold, so the reachable region was not
        // discounted so much as unvisited -- and the table below crosses it at 0.1620.
        //
        // MEASURED, and it turned on the one variable nobody had moved: the body's CONTENT, with
        // request size, state shape and question count all held fixed and every arm inside the
        // 8192-byte cap. Six arms at twelve questions with bodies at 2400 bytes, so that each arm is
        // a state the adapter actually produces -- `nimble-`, second run, 23:53 (artifact
        // `.styloagent/scratch/nimble/expansion-shapes.json`).
        //
        // ONE RUN OF THREE, and the BINDING one is not this. The same six arms were run at a 2000-byte
        // body (hex-ish 37298) and at the full 2500-character budget (hex-ish 43202); the per-row
        // ratios below are this run's and must not be compared across runs, but the BOUND is set by
        // the arm AT the budget, which is the 2500-byte one named above.
        //
        //     shape        request B   input_tokens   expansion   B/token
        //     prose            6810         19574      2.874x     0.3479
        //     base64ish        6810         33278      4.887x     0.2046
        //     mixed            6810         34262      5.031x     0.1988
        //     randomcase       6810         38570      5.664x     0.1766
        //     punct            6909         40094      5.803x     0.1723
        //     hexish           6810         42026      6.171x     0.1620
        //
        // The guard compares a TOKEN count against the applied window, so the direct test is that
        // one: FIVE of the six evaluate above 32768 and only `prose` is below it. So the guard IS
        // reachable at applied 32768, and the 25800 and 23100 pair were both PROSE figures -- prose
        // being the LEAST dense of the six -- which is why they read as a bound when they were a
        // single sample. The server is not the limit: the first run's densest request, evaluating
        // 56210 tokens, returned HTTP 200 with all twelve answers, which measures the SERVER (a valid
        // request, though not an adapter state), so the refusal is this client's.
        //
        // THE FIRST RUN HAD TO BE CORRECTED, AND THE REASON IS THE FIXTURE RATHER THAN THE
        // ARITHMETIC. Its bodies were 3600 bytes, which `NimbleOptions.MaxBodyCharacters` (default
        // 2500) would have shortened before the wire, so those arms were reachable as HTTP requests
        // but NOT as adapter states -- a stated fixture that the mechanism does not produce. The
        // corrected run is the table above, and the conclusion is STRONGER at the reachable size
        // rather than weaker.
        //
        // WHY FIVE LANES MISSED IT, which is the transferable part: the size series everyone quoted
        // (0.345 at 6410 bytes up to 0.383 at 18410) varied SIZE with the shape held fixed, and was
        // read as a fact about the ratio in general. It was a fact about size. Content moves the same
        // ratio from 0.3479 to 0.1620, a factor of 2.1, and no amount of care about polarity would
        // have surfaced that, because the variable the claim was about had never been moved. The
        // correction is not "the ratio is unstable"; it is "the ratio is stable in the variable we
        // varied".
        //
        // LIMITS, STATED RATHER THAN LEFT FOR A READER, and two of them are corrections to stronger
        // sentences that briefly stood here. The six shapes were CHOSEN and not sampled, so a denser
        // shape than hexish is unmeasured and the ceiling is open above 43202; non-ASCII is untested,
        // because the adapter escapes it as \uXXXX and that would change the wire bytes. AND THE ARMS
        // RAN AGAINST THE ENDPOINT DIRECTLY, not through the shipping adapter and assessor, so a
        // dense body on the REAL path is UNMEASURED and this table must not stand in for it. The
        // refutable form of the claim is that a 2500-character hexish body -- the FULL budget -- through
        // the real pipeline is **SHORTENED FIRST**, since `corpus-` measured 6 of 6 at that size with
        // prose cut exactly like hex-ish, so it arrives at roughly `L - 512` characters and evaluates
        // **BELOW** 43,202 rather than at it, with the cut reported on the row's reason attribute.
        // **AND `L - 512` IS NOT THE ONLY OUTCOME: `:407` `budget = budget > step ? budget - step : 0;`
        // ZEROES the budget in one step when `excess + 512 >= budget`, so with `excess >= 1988` at the
        // 2500 default BOTH fields are emptied and the reading is `kept = 0` (`queue-` derived this and
        // simulated four cases, parameter-free).** A capture that sees 0 has not found an error; it has
        // found the emptied-body outcome.
        //
        // **AND THERE IS A FOURTH, WHERE THE FIT GIVES UP ENTIRELY AND NOTHING IS ASKED.** `:398-401`
        // returns null when the budget is already zero, and the caller turns that into
        // `Unavailable(..., "question set and message state exceed the configured context window")` at
        // `:165-174`. So a request whose QUESTIONS AND OVERHEAD alone exceed `NumCtx` is never sent, and
        // every askable row carries that reason. **IT IS REACHABLE, WHICH IS WHY IT IS WORTH STATING:
        // the fit's only lever is `bodyCharacterBudget`, and it reaches exactly `body_text` and
        // `quoted_text`. The conversation context is capped at a FIXED 2,000 characters PER ENTRY with
        // ten entries taken (`NimbleMessageState.cs:153-155`), and the tagged context at `:161` is
        // untruncated, so up to 20,000 characters of it are IMMUNE to the reduction the loop applies.**
        // A reply thread can therefore reach the fourth outcome with a modest body, and the largest term
        // the loop is fighting is the one its lever cannot touch.
        // **43202 answers a DIFFERENT question: what the server reads when the fit does not run**, which
        // is why this arm's output is a PAIR -- the kept length and the resulting evaluation -- and not
        // a token count on its own. That run is QUEUED and has not been taken, so the table describes
        // arms, not the deployment.
        //
        // On the POPULATION, AND BOTH OF THE TEMPTING SENTENCES ARE FALSE. Each is refuted at source,
        // read at HEAD `3c6b08a`, and the second is the one that looked safe:
        //
        //   NOT "messages carrying attachments". An attachment enters the state as FIVE METADATA
        //   FIELDS and nothing else: `NimbleMessageState.cs:82-88` writes `file_name`,
        //   `declared_content_type`, `extension_implied_content_type`, `size_bytes` and
        //   `content_available`, fed by `BoundedMimeMessageAnalyzer.cs:388`
        //   (`Attachments = [.. collected.Attachments.Select(a => a.Metadata)]`). A ten-megabyte
        //   base64 attachment contributes five fields and not its bytes, so it CANNOT raise the
        //   evaluation at any budget.
        //
        //   NOT "quoted-printable bodies" either, which is the half that looked safe because the
        //   2500-character budget DOES cover `BodyText`. It does, and the transfer encoding is gone
        //   before the classifier sees anything: `BoundedMimeMessageAnalyzer.cs:379` reads
        //   `quoted.NewText.Length > 0 ? quoted.NewText : effectivePlain`, and `:146-151` shows
        //   `effectivePlain` is DECODED plain text (`collected.PlainBodies`, falling back to the
        //   visible HTML) with the quoted history split out. A quoted-printable body is PROSE by the
        //   time it arrives. The budget was the right object and the wrong question: it says how much
        //   `BodyText` survives, not what `BodyText` contains.
        //
        // SO THE CLASS IS `nimble-`'s, and it is density that SURVIVES DECODING -- the AUTHOR's
        // density rather than the encoding's. Long hexadecimal (hashes, message-ids, certificate
        // fingerprints), base64 or JWT blobs pasted into the body, PGP armour, tracking URLs with
        // long query strings, code, logs, JSON, diff output. Those are ordinary in security alerts,
        // newsletters and CI notifications, which are exactly the messages worth assessing.
        //
        // AND THE PIN MOVES THIS EXPOSURE RATHER THAN REMOVING IT, which is the sentence to carry out
        // of tonight. At the landed `EffectiveNumCtx 65536` the largest evaluation any MEASURED
        // admissible body reaches is 43202, so dense content is NO LONGER REFUSED and the refusal is
        // not the live case. What still binds is the FIT, which the pin does not touch: the fit loops
        // on `total <= NumCtx` where `total` is the WHOLE SERIALIZED REQUEST in BYTES
        // (`NimbleSemanticMailClassifier.cs:388`), so it shortens the body until the request fits, and
        // the pin moved only the BACKSTOP at `:228`. **AND THE STEP IS THE EXCESS PLUS A DELIBERATE
        // MARGIN, whose cost is body and whose benefit is CONVERGENCE** -- not an overshoot, which is
        // the word I used first and which `queue-` corrected. The loop's own comment gives the purpose:
        // "Remove the measured excess plus the margin, and never less than a step, so a request that is
        // barely over the line still converges." `:86` `PromptByteMargin = 512`; `:405`
        // `var excess = total - _options.NumCtx;`; `:406` `var step = Math.Max(excess + 512, 64);`;
        // `:407` `budget = budget > step ? budget - step : 0;`. So a body a hundred bytes over the line
        // loses 612 characters, the floor is 64, and the loop can run more than once, **which means a
        // body that barely oversteps lands well below the largest one that would have fitted** -- a
        // property to know about the CUT rather than a defect in the guard.
        //
        // **AND THE COMMENT ABOVE THAT CONSTANT JUSTIFIES 512 WITH A REASON THAT IS NOT TRUE**: it cites
        // "the state's own punctuation" and "any small field added later", and BOTH are already handled
        // -- `:385` measures the WHOLE serialized request (`GetByteCount(JsonSerializer.Serialize(
        // request, ...))`), so the punctuation is counted, and `:186` `SendWithRetryAsync(request, ...)`
        // sends the same instance, so no field is added after the measurement. **A real cost with a
        // wrong explanation attached**: the margin is doing its job and the sentence defending it does
        // not describe why. **AND THE SIZE IS NOT READABLE FROM A SERVED DECISION, which I measured rather than
        // assumed.** `body_text_characters_kept` is written into the state that goes to the MODEL
        // (`NimbleMessageState.cs:96-97`) and `Built` carries only the BOOLEAN across the method
        // boundary (`:38`, `internal sealed record Built(Dictionary<string, object?> State, bool
        // BodyShortened)`), so a caller cannot report it. Measured on a real served document
        // (`run-benign-full-20261001T232851Z/decisions/004.eml.json`): `availabilityReasons` appears
        // 46 times and `body_text_characters_kept` and `body_text_shortened_for_prompt` appear ZERO
        // times, so the REQUEST PAYLOAD does not travel with the SERVED DECISION. **A reader of a
        // decision can see the reason and not the size**, which is why `nimble-` proposes putting the
        // kept and original lengths into the reason string itself. **And a lane sitting on the wire
        // cannot read it off an existing artifact either**: `tools/conversation/measure`'s
        // `RecordingHandler` DOES keep the outgoing payload, but only in a LOCAL for the duration of
        // the call, and `Runner.cs:81` derives one boolean from it and persists THAT, so the sibling
        // key costs one parse plus one new field plus a NEW RUN rather than being a reading of what a
        // run already wrote. **The quantity was never lost, it was only UNSENT: unsent to the decision,
        // sent to the model, and in hand only while the call is in flight.**
        //
        // AND THE NOUNS MATTER HERE, which is why this paragraph says REQUEST PAYLOAD and SERVED
        // DECISION rather than "the state": that phrase meant the outgoing payload to one lane and the
        // decision to another, and both were right about their own object.
        //
        // **THE CUT IS A SIZE RULE, NOT A DENSITY RULE:
        // measured at a CONSTANT 2500 characters, prose is shortened exactly like hex-ish.** SIZE
        // drives the fit's cut; SHAPE drives the token expansion in the paragraph above. Two
        // mechanisms, two variables, and they are easy to tangle. (`NimbleOptions.MaxBodyCharacters`,
        // 2500, is a third cap and a smaller one; note that the OTHER `MaxBodyCharacters` on
        // `MailAssessorOptions` defaults to 1_000_000, so the project has to be named.) Because the
        // row stays `Available`, that cut reaches a reader ONLY through the reason attribute. So the
        // live failure is the SHORTENED READ rather than the refusal, which is `policy-`'s ruling.
        //
        // AND IT IS THE NORMAL CASE RATHER THAN AN EDGE ONE, on `nimble-`'s body-room arithmetic: the
        // twelve-question object is 3,825 bytes and a 2,500-byte-body request is 6,910, so the non-body
        // overhead is 4,410 and **the body room in a MINIMAL state is 8192 - 4410 = 3782 bytes. Their
        // arm used 2,500 of that 3,782 and had 1,282 bytes to spare that a real message does not have**,
        // because the real builder also adds `BuildBehaviourProfile` (`:120`), `conversation_context`
        // (`:151`), `context` (`:159`), links up to `MaxLinks` 40 and attachments up to
        // `MaxAttachments` 20, every byte of it out of the same `NumCtx` 8192. **So `MaxBodyCharacters`
        // 2500 is a CEILING SET ABOVE WHAT A REAL REQUEST CAN CARRY, the fit reduces the body on
        // essentially every real message, and the reason attribute is not an exception marker but a
        // report on nearly every row** -- which is why it appears eleven times in one served document.
        // **AND IT MAKES 43202 NOT A SHIPPING-PATH FIGURE FOR A SECOND, INDEPENDENT REASON: it is
        // endpoint-direct (the fit never ran) AND it was measured under a state the adapter does not
        // build.** The second survives a future arm that runs through the pipeline with the same
        // probe-shaped state, which is why it is the stronger of the two.
        //
        // What the table supports without either over-reach: FIVE OF SIX body contents evaluate above
        // 32768 and PLAIN PROSE IS THE EXCEPTION rather than the rule, so a reader must not take a
        // naturally dense body for a corner. That finding is what the PIN acts on; at the landed
        // 65536 none of the six is refused, and the exposure that remains is the one below.
        //
        // Split from the line above as well: one line carrying its own explanation is exactly the line
        // a reader quotes in half, and three lanes quoted only the first sentence of it within the
        // hour. It carries the two placeholders rather than being a bare string because a no-argument
        // log call exists nowhere else in `src/` and this file builds under `-warnaserror`.
        logger.LogInformation(
            "StyloMail nimble bounds: the truncation guard compares the server's evaluated TOKEN "
            + "count against the applied window ({AppliedWindow}); the fit bounds the REQUEST by its "
            + "UTF-8 BYTE count against NumCtx ({NumCtx}). The byte budget does not bound the "
            + "evaluation of a fit-satisfying request, so whether the guard reaches one is that "
            + "comparison, and this line does not settle it for a setting it cannot see.",
            options.AppliedContextWindow,
            options.NumCtx);

        if (!IsLoopback(options.Endpoint))
        {
            logger.LogWarning(
                "STYLOMAIL NIMBLE ENDPOINT IS NOT LOOPBACK: message content is being sent to {Endpoint} "
                + "rather than to this machine. This provider is selected for the property that "
                + "message content does not leave the host, and this endpoint gives that up; confirm "
                + "it is intended.",
                options.Endpoint);
        }

        return options;
    }

    /// <summary>Whether an endpoint stays on this machine.</summary>
    /// <remarks>
    /// Answered from the parsed URI rather than from a string prefix, so <c>localhost</c>, a loopback
    /// address and the IPv6 loopback are all recognised as local, and an unparseable endpoint is
    /// treated as not local: the warning it earns is the right one to be wrong about.
    /// </remarks>
    private static bool IsLoopback(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;

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
