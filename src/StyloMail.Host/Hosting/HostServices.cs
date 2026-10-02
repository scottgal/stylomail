using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Text;
using StyloMail.Assessment;
using StyloMail.Assessment.Semantic;
using StyloMail.Cascade;
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

        // THE CASCADE'S VERSION STRING, BUILT BEFORE THE OPTIONS AND USED IN TWO PLACES, which is
        // why it is computed here rather than inside the arm below.
        //
        // The semantic cache serves a stored entry only when the entry's resolved model version
        // EQUALS the configured one (`InMemorySemanticCacheStore.IsVersionCompatible`), and
        // `SemanticCacheKey.Digest` covers the configured value and no inner endpoint. So this one
        // string is both the compatibility gate and the cache key's model term, and it must name
        // each arm's HOST as well as its model: two hosts running this model at the same tag and
        // digest answered differently on every one of 352 dimension-instances measured on
        // 2026-10-02, so a string naming models alone would serve an entry taken under a replaced
        // host. Two readers and one writer is deliberate; a deployment cannot set one and forget
        // the other.
        var nimbleArmOptions = provider == AssessmentProvider.Cascade
            ? BuildNimbleOptions(
                configuration,
                services.GetRequiredService<ILoggerFactory>().CreateLogger("StyloMail.Host.Nimble"))
            : null;

        var secondArmOptions = provider == AssessmentProvider.Cascade
            ? BuildJevOptions(
                configuration,
                secrets.JevApiKey!,
                services.GetRequiredService<ILoggerFactory>().CreateLogger("StyloMail.Host.Jev"))
            : null;

        var cascadeVersion = nimbleArmOptions is not null && secondArmOptions is not null
            ? CascadeClassifierVersion(nimbleArmOptions, secondArmOptions)
            : null;

        if (cascadeVersion is not null)
        {
            options = options with
            {
                SemanticCache = options.SemanticCache with { ClassifierModelVersion = cascadeVersion },
            };

            // Announced, because which model answered is a fact the log should carry on every boot
            // and not something an operator has to reconstruct from two other lines. The version
            // string is printed because it is the value the cache compares against, and a reader
            // comparing two runs' decisions needs it to tell a moved arm from an unchanged one.
            logger.LogInformation(
                "StyloMail assessment provider: Cascade, local {LocalModel} at {LocalEndpoint}, "
                + "second opinion {SecondModel} at {SecondEndpoint}, classifier version {Version}.",
                nimbleArmOptions!.Model,
                nimbleArmOptions.Endpoint,
                secondArmOptions!.Model,
                secondArmOptions.Endpoint,
                cascadeVersion);
        }

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

            // The two arms above, composed. The local arm holds no credential and needs no wrapper;
            // the second arm is wrapped exactly as the hosted provider wraps it, so a rejected
            // credential reaches `/health/ready` through the same path rather than surfacing only as
            // rows that say the second opinion was unavailable.
            //
            // The weights are the POLICY ENGINE'S OWN TABLE, passed by reference from the options
            // this assessor will be built with. A copy here would keep escalating on a dimension the
            // engine had stopped weighing, and the escalation threshold would then be measuring an
            // older policy than the one acting.
            AssessmentProvider.Cascade => new CascadeSemanticClassifier(
                new NimbleSemanticMailClassifier(http, nimbleArmOptions!, clock),
                new CredentialAwareSemanticClassifier(
                    new JevSemanticMailClassifier(http, secondArmOptions!, clock),
                    services.GetRequiredService<ProviderCredentialHealth>(),
                    clock),
                new CascadeOptions
                {
                    ClassifierVersion = cascadeVersion!,
                    DimensionWeights = options.Policy.DimensionWeights,
                }),

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
    /// The version string a cascade deployment reports, naming each arm's MODEL and each arm's HOST.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Built from the built options, not from the configuration keys, so there is one reader of
    /// each key.</b> Reading the keys again here would be a second reader that could disagree with
    /// <see cref="BuildNimbleOptions"/> or <see cref="BuildJevOptions"/> about a default or an unset
    /// value, and the two would drift apart silently: a version string that named a default the
    /// adapter is not using would key the cache on a configuration the deployment does not have.
    /// </para>
    /// <para>
    /// <b>The host belongs in the string.</b> Measured 2026-10-02 by the cascade lane: the same model
    /// at the same tag and digest on two hosts, given byte-identical requests, produced different
    /// answers on every measured instance and no instance agreed exactly. The figures and their
    /// populations, because a maximum is only readable with its denominator: over the mailbox and
    /// mixed batches together, <b>352</b> asked instances, maximum absolute difference
    /// <b>2.966e-02</b>; over those plus the shaped batch, <b>440</b> instances and 40 distinct
    /// messages, maximum <b>3.977e-02</b> (semantic.link_lure). The second population contains the
    /// first, so 3.977e-02 is the larger and the later figure and the one to quote; it does NOT
    /// supersede the other as a measurement of anything different, because a maximum grows with the
    /// number of instances compared. The semantic cache serves an entry only when the reported
    /// version equals this configured one, and the cache key covers this value rather than any inner
    /// endpoint, so a string naming models alone would serve an entry taken under a replaced host.
    /// </para>
    /// <para>
    /// Public for the same reason the two option builders are: it is a composition decision a test
    /// should assert on directly rather than infer from a cache miss.
    /// </para>
    /// </remarks>
    public static string CascadeClassifierVersion(NimbleOptions localArm, JevOptions secondArm)
    {
        ArgumentNullException.ThrowIfNull(localArm);
        ArgumentNullException.ThrowIfNull(secondArm);

        return "cascade/1"
            + $"+nimble:{localArm.Model}@{Authority(localArm.Endpoint)}"
            + $"+jev:{secondArm.Model}@{Authority(secondArm.Endpoint)}";
    }

    /// <summary>
    /// The authority (host and port) of an endpoint, or the string unchanged when it does not parse.
    /// </summary>
    /// <remarks>
    /// Keyed rather than the whole URL, matching the local adapter's own cache digest, so a scheme or
    /// path change does not fragment the entry while a different host or port does not share one. An
    /// unparseable endpoint keys to itself rather than to every other unparseable one.
    /// </remarks>
    private static string Authority(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Authority : endpoint;

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

        // THE DEADLINE IS BOUND BECAUSE A CASCADE CAN POINT THIS ADAPTER AT A LOCAL MODEL, and the
        // one-second default is a hosted-service assumption rather than a property of the protocol.
        // Measured on 2026-10-02 while wiring the cascade: with the second arm pointed at a model
        // server on this machine, the adapter's own deadline elapsed on the first escalated
        // assessment, it raised its contract exception, and the request answered HTTP 500. That
        // default is right for a hosted endpoint and wrong for a local one, and the cascade's whole
        // purpose is to let a deployment choose where the second opinion comes from.
        //
        // Unparseable or absent means the default, the same shape the keys above use. Nothing about
        // an existing hosted deployment moves: the default is still one second.
        var timeoutSeconds = configuration["StyloMail:Jev:TimeoutSeconds"];

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
            Timeout = int.TryParse(timeoutSeconds, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : defaults.Timeout,
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
// WHY `appsettings.json` CARRIES A PIN: 65536 is a floor above the largest evaluation an
// adapter-producible state has been measured to reach, and the derived default of 4096 refuses
// real mail. **The measurements, the arms that set the bound, the launcher's working directory
// and why the value is a floor rather than a law are in `docs/running.md` under the window
// section.** They sat here until 2026-10-02 and four of them went stale in one night, each caught
// by a peer and none by a reader of this file -- which is the argument for moving them, and the
// reason this comment now carries only what a reader of THIS method needs.
//
// AND WHAT THAT IS: the pin is a FILE, and a file the binary does not read is a configuration
// rather than a setting. `Program.cs` and `Cli/CliApplication.cs` pin the content root to
// `AppContext.BaseDirectory`, so it is read wherever the binary is launched from.
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

// THE RULE THE LINE BELOW STATES, AND THAT LINE'S OWN SCOPE.
//
// The fit bounds the REQUEST by its UTF-8 BYTE count against `NumCtx`; the guard compares the
// server's evaluated TOKEN count against the APPLIED window. Two quantities, two bounds, and the
// byte budget does not bound the evaluation.
//
// AND THIS LINE'S SCOPE, which is the only claim in this method about THIS FILE: it states the two
// quantities and NAMES NO CONSEQUENCE, because the consequence depends on the setting and this line
// cannot see it. It was rewritten four times in an hour -- asserting a consequence, removing it,
// restoring it with a mechanism, dropping that mechanism -- and the version that survived says only
// what is measured. It carries its two placeholders rather than being a bare string because a
// no-argument log call exists nowhere else in `src/` and this file builds under `-warnaserror`, and
// it is split from the window line above because a line carrying its own explanation is the line a
// reader quotes in half.
//
// THE MEASUREMENTS ARE IN `docs/running.md`: the expansion table and its arms, the evaluated bound
// and its three scopes, the boundary and the window with its width, the two regimes and the shadow
// collision between them, and the four outcomes of the shortening PATH. One sentence there is load
// bearing for the rest: **a bare wire total is not a reading of a body length** -- it needs the flag
// for THAT field, or that field's kept length, or a known regime, because the state names the body
// and the quoted tail separately and the total alone cannot say which of them moved.
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
