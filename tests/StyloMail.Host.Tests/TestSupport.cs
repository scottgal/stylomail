using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StyloMail.Transport.Cloudflare;
using StyloMail.Transport.Ingress;
using StyloMail.Core;
using StyloMail.Host;
using StyloMail.Host.Auth;
using StyloMail.Host.Hosting;
using StyloMail.Host.Storage;
using StyloMail.Host.Submissions;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// Credentials the test suite authenticates with. These are test fixtures, not secrets: they
/// exist only inside this process and are never valid anywhere else.
/// </summary>
internal static class TestPrincipals
{
    public const string AcmeTenant = "acme";
    public const string GlobexTenant = "globex";

    /// <summary>Assessment-only service principal for acme.</summary>
    public const string AcmeAssessKey = "test-key-acme-assess";
    public const string AcmeAssessPrincipal = "svc-acme-assess";

    /// <summary>Outbound sending principal for acme. Deliberately holds no review privilege.</summary>
    public const string AcmeSenderKey = "test-key-acme-send";
    public const string AcmeSenderPrincipal = "user-acme-sender";

    /// <summary>Reviewer for acme, separately privileged from the sender.</summary>
    public const string AcmeReviewerKey = "test-key-acme-review";
    public const string AcmeReviewerPrincipal = "user-acme-reviewer";

    /// <summary>A second tenant, used to prove cross-tenant access is denied.</summary>
    public const string GlobexSenderKey = "test-key-globex-send";
    public const string GlobexSenderPrincipal = "user-globex-sender";

    /// <summary>Holds every privilege, for tests about what an administrator may do.</summary>
    public const string AcmeOperatorKey = "test-key-acme-operator";
    public const string AcmeOperatorPrincipal = "user-acme-operator";

    /// <summary>Globex's administrator, so a cross-tenant control action is refused on tenancy
    /// rather than merely on privilege.</summary>
    public const string GlobexOperatorKey = "test-key-globex-operator";
    public const string GlobexOperatorPrincipal = "user-globex-operator";

    /// <summary>Globex likewise gets a fully privileged reviewer, so a cross-tenant read is
    /// refused on tenancy rather than merely on privilege.</summary>
    public const string GlobexReviewerKey = "test-key-globex-review";
    public const string GlobexReviewerPrincipal = "user-globex-reviewer";

    /// <summary>Header the host reads API keys from. A custom header is deliberate: browsers do
    /// not attach it automatically, so it cannot be ridden by a cross-site request.</summary>
    public const string ApiKeyHeader = "X-StyloMail-Key";

    /// <summary>
    /// The tenant a test principal's key resolves to.
    /// </summary>
    /// <remarks>
    /// Needed by anything that has to publish *to* a connected test principal, which means anything
    /// that has to wait for one to be subscribed before telling it something. Kept beside the keys
    /// themselves so the two cannot drift apart.
    /// </remarks>
    public static string TenantFor(string apiKey) => apiKey switch
    {
        AcmeAssessKey or AcmeSenderKey or AcmeReviewerKey or AcmeOperatorKey => AcmeTenant,
        GlobexSenderKey or GlobexReviewerKey or GlobexOperatorKey => GlobexTenant,
        _ => throw new ArgumentOutOfRangeException(
            nameof(apiKey),
            "That key is not one of the suite's fixtures, so there is no tenant to publish to."),
    };
}

/// <summary>
/// Web host under test, with the two external dependencies replaced: no live Jev provider and
/// no network access. The assessor is a fake whose every call is recorded, so tests can assert
/// on the boundary rather than on a vendor.
/// </summary>
internal sealed class TestHost : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings = new(StringComparer.Ordinal);
    private readonly List<Action<IServiceCollection>> _overrides = [];

    public TestHost()
    {
        Root = Path.Combine(Path.GetTempPath(), "stylomail-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Configure("StyloMail:Storage:SpoolRoot", Path.Combine(Root, "spool"));
        Configure("StyloMail:Storage:DatabasePath", Path.Combine(Root, "host.db"));

        AddPrincipal(TestPrincipals.AcmeAssessPrincipal, TestPrincipals.AcmeAssessKey,
            TestPrincipals.AcmeTenant, "Assess");
        AddPrincipal(TestPrincipals.AcmeSenderPrincipal, TestPrincipals.AcmeSenderKey,
            TestPrincipals.AcmeTenant, "Assess", "Send");
        AddPrincipal(TestPrincipals.AcmeReviewerPrincipal, TestPrincipals.AcmeReviewerKey,
            TestPrincipals.AcmeTenant, "Assess", "Review");
        AddPrincipal(TestPrincipals.AcmeOperatorPrincipal, TestPrincipals.AcmeOperatorKey,
            TestPrincipals.AcmeTenant, "Assess", "Send", "Review", "Feedback", "Administer");
        AddPrincipal(TestPrincipals.GlobexSenderPrincipal, TestPrincipals.GlobexSenderKey,
            TestPrincipals.GlobexTenant, "Assess", "Send");
        AddPrincipal(TestPrincipals.GlobexReviewerPrincipal, TestPrincipals.GlobexReviewerKey,
            TestPrincipals.GlobexTenant, "Assess", "Review");
        AddPrincipal(TestPrincipals.GlobexOperatorPrincipal, TestPrincipals.GlobexOperatorKey,
            TestPrincipals.GlobexTenant, "Assess", "Send", "Review", "Feedback", "Administer");
    }

    public string Root { get; }

    public RecordingAssessor Assessor { get; } = new();

    /// <summary>
    /// The configuration this host was given, in the same shape the running host sees it.
    /// </summary>
    /// <remarks>
    /// Read back from the settings rather than from the built host, so a test can ask what the host
    /// would decide without depending on the decision having been wired to a component it can reach.
    /// </remarks>
    public IConfiguration ConfigurationSnapshot =>
        new ConfigurationBuilder().AddInMemoryCollection(_settings).Build();

    private bool _installFakeAssessor = true;

    /// <summary>
    /// Leaves the host's own default <c>IMailAssessor</c> in place instead of installing the fake,
    /// so the "no assessor configured" behaviour can be exercised.
    /// </summary>
    public TestHost WithoutAssessor()
    {
        _installFakeAssessor = false;
        return this;
    }

    /// <summary>
    /// Makes durable acceptance fail, so the "storage unavailable" path can be exercised without
    /// depending on the suite's ability to make a directory genuinely unwritable, which varies
    /// with the user the tests run as.
    /// </summary>
    public TestHost FailSubmissions()
        => Override(services =>
        {
            RemoveAll<ISubmissionIntake>(services);
            services.AddSingleton<ISubmissionIntake, UnavailableSubmissionIntake>();
        });

    /// <summary>
    /// Puts a hub in the host whose every audience throws, which is what a dead hub looks like from
    /// the code that publishes into it.
    /// </summary>
    /// <remarks>
    /// <b>The failure is injected as far from the pipeline as it can be.</b> Nothing here replaces
    /// the port, the adapter or an emission site: the real adapter and the real wrappers run, and
    /// only the transport underneath them is broken, so a test can assert that a hub outage reaches
    /// no further than the hub.
    /// </remarks>
    public TestHost WithFailingHub()
        => Override(services =>
        {
            RemoveAll<IHubContext<StyloMail.Host.Traffic.TrafficHub>>(services);

            // Registered after SignalR's own open-generic registration, which is what makes this
            // one win the resolution. SignalR registers the interface as an open generic, so the
            // removal above finds nothing to remove.
            services.AddSingleton<IHubContext<StyloMail.Host.Traffic.TrafficHub>>(new ThrowingHubContext());
        });

    /// <summary>Enables the browser cookie channel, which is off by default.</summary>
    public TestHost WithBrowserChannel()
    {
        Configure("StyloMail:Auth:EnableBrowserCookieChannel", "true");
        return this;
    }

    /// <summary>Overrides the queue's bounds for a test.</summary>
    public TestHost WithQueueOptions(QueueOptions options)
        => Override(services =>
        {
            RemoveAll<QueueOptions>(services);
            services.AddSingleton(options);
        });

    /// <summary>
    /// Switches the SMTP submission listener on, bound to loopback on an operating-system-chosen
    /// port.
    /// </summary>
    /// <remarks>
    /// <b>Encryption is switched off here, and that is the point rather than a shortcut.</b> With
    /// <c>RequireEncryption</c> left on and no certificate there is no <c>STARTTLS</c> and therefore
    /// no <c>AUTH</c>, so the listener refuses to be constructed at all, a real deployment wanting
    /// authenticated submission supplies a certificate. What these tests exercise is the inbound
    /// handoff, which is the path that works without one.
    /// </remarks>
    public TestHost WithSmtpIngress(params string[] recipientDomains)
    {
        Configure("StyloMail:Transport:SmtpIngress:Enabled", "true");
        Configure("StyloMail:Transport:SmtpIngress:BindAddress", "127.0.0.1");
        Configure("StyloMail:Transport:SmtpIngress:Port", "0");
        Configure("StyloMail:Transport:SmtpIngress:ServerName", "stylomail");
        Configure("StyloMail:Transport:SmtpIngress:RequireEncryption", "false");
        Configure("StyloMail:Transport:SmtpIngress:AllowUnauthenticatedInbound", "true");
        Configure("StyloMail:Transport:SmtpIngress:LocalHostIdentities:0", "stylomail");

        for (var i = 0; i < recipientDomains.Length; i++)
        {
            Configure($"StyloMail:Transport:SmtpIngress:RecipientDomains:{i}", recipientDomains[i]);
        }

        return this;
    }

    /// <summary>The port the listener actually bound, once the host has started.</summary>
    public int BoundIngressPort => Services.GetRequiredService<SmtpIngressHostedService>().BoundPort
        ?? throw new InvalidOperationException("The SMTP ingress listener is not running.");

    /// <summary>
    /// Switches the Cloudflare Email Routing intake on, with a known secret.
    /// </summary>
    /// <remarks>
    /// <b>The connector is replaced rather than the environment being set.</b> The real secret is read
    /// from <c>STYLOMAIL_CF_INGRESS_SECRET</c>, and mutating process environment from a test would
    /// race against every other test in the suite, the same hazard <c>HostCredentials.Resolve</c> is
    /// shaped to avoid. What is substituted is the credential only: the connector is still built over
    /// the container's own sink and options, so everything except the secret is the production wiring.
    /// The environment-absent path is tested separately and does not need to be faked.
    /// </remarks>
    public TestHost WithCloudflareIngress(string sharedSecret, params string[] recipientDomains)
    {
        Configure("StyloMail:Transport:CloudflareIngress:Enabled", "true");

        for (var i = 0; i < recipientDomains.Length; i++)
        {
            Configure($"StyloMail:Transport:CloudflareIngress:RecipientDomains:{i}", recipientDomains[i]);
        }

        return Override(services =>
        {
            RemoveAll<CloudflareEmailRoutingConnector>(services);

            services.AddSingleton(sp => new CloudflareEmailRoutingConnector(
                sp.GetRequiredService<IOptions<HostTransportOptions>>().Value.CloudflareIngress.Build(sharedSecret),
                sp.GetRequiredService<ISmtpIngressSink>(),
                sp.GetRequiredService<TimeProvider>()));
        });
    }

    /// <summary>
    /// Counts what reaches durable acceptance, so a seam that accepts twice can be seen.
    /// </summary>
    /// <remarks>
    /// The double-accept this guards against is invisible in the outcome: the queue dedupes what it
    /// can see as the same submission, and two accepts under two different idempotency keys are two
    /// deliveries that both look correct from either call site. Only the count shows it.
    /// </remarks>
    public TestHost CountingSubmissions()
    {
        var counter = Submissions;

        return Override(services =>
        {
            RemoveAll<ISubmissionIntake>(services);
            services.AddSingleton<ISubmissionIntake>(sp => new CountingSubmissionIntake(
                sp.GetRequiredService<QueueStore>(),
                counter));
        });
    }

    /// <summary>How many messages have reached durable acceptance on this host.</summary>
    public AcceptanceCounter Submissions { get; } = new();

    /// <summary>A clock the test controls, so timestamps are chosen rather than raced for.</summary>
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Runs this host on <see cref="Clock"/> instead of the wall clock.
    /// </summary>
    /// <remarks>
    /// Needed wherever a test depends on *ordering* by time. Two records written in the same
    /// millisecond tie on their timestamp, and a keyset cursor's tiebreak then decides the order — so
    /// a test that means to exercise distinct-timestamp paging can silently become a
    /// same-timestamp one and stop covering the case it was written for. Setting the times removes
    /// the race rather than hoping the machine is slow enough.
    /// </remarks>
    public TestHost WithClock()
        => Override(services =>
        {
            RemoveAll<TimeProvider>(services);
            services.AddSingleton<TimeProvider>(Clock);
        });

    private int _principalIndex;

    public TestHost Configure(string key, string? value)
    {
        _settings[key] = value;
        return this;
    }

    /// <summary>Points this host at another instance's storage, so a restart can be simulated
    /// without the test having to guess where the files went.</summary>
    public TestHost ReusingStorageOf(TestHost other)
    {
        Configure("StyloMail:Storage:SpoolRoot", Path.Combine(other.Root, "spool"));
        Configure("StyloMail:Storage:DatabasePath", Path.Combine(other.Root, "host.db"));
        return this;
    }

    public TestHost Override(Action<IServiceCollection> configure)
    {
        _overrides.Add(configure);
        return this;
    }

    /// <summary>
    /// Mints a key straight into this host's store and returns the value it would have printed.
    /// </summary>
    /// <remarks>
    /// Deliberately the store rather than <c>KeyCommands.CreateAsync</c>: a test about what the
    /// <em>listing</em> does with a minted principal should not depend on the CLI's output format,
    /// and the CLI's own tests drive the CLI. This writes exactly the row <c>key create</c> writes,
    /// because it is the same call.
    /// </remarks>
    public string MintKey(string principalId, string tenantId, params string[] privileges)
    {
        var material = MintedApiKey.Mint();

        var created = Services.GetRequiredService<MintedPrincipalStore>().Create(
            new MintedPrincipal
            {
                KeyId = material.KeyId,
                PrincipalId = principalId,
                TenantId = tenantId,
                Privileges = [.. privileges],
                ApprovedSenderIdentities = [],
                CreatedAt = Clock.GetUtcNow(),
                CreatedBy = "test",
            },
            material);

        Assert.True(created, $"'{principalId}' already had a live minted key.");

        return material.Value;
    }

    public TestHost AddPrincipal(string principalId, string key, string tenantId, params string[] privileges)
    {
        var i = _principalIndex++;
        Configure($"StyloMail:Auth:Principals:{i}:PrincipalId", principalId);
        Configure($"StyloMail:Auth:Principals:{i}:Key", key);
        Configure($"StyloMail:Auth:Principals:{i}:TenantId", tenantId);
        for (var p = 0; p < privileges.Length; p++)
        {
            Configure($"StyloMail:Auth:Principals:{i}:Privileges:{p}", privileges[p]);
        }

        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(_settings);
        });

        builder.ConfigureServices(services =>
        {
            // The host requires an IMailAssessor; nothing in the repo implements one yet, so the
            // suite supplies a deterministic fake. Remove-then-add so a real registration cannot
            // silently win and reach the network.
            if (_installFakeAssessor)
            {
                RemoveAll<IMailAssessor>(services);

                // Resolved through the container so the fake gets the same intake and spool the
                // real pipeline would use, and can therefore model reading the payload back and
                // accepting for real. An assessor constructed with `new` could not do either, and
                // that is exactly how the seam defect stayed invisible.
                services.AddSingleton<IMailAssessor>(sp =>
                {
                    Assessor.Connect(
                        sp.GetRequiredService<ISubmissionIntake>(),
                        sp.GetRequiredService<SpoolStore>());
                    return Assessor;
                });
            }

            foreach (var configure in _overrides)
            {
                configure(services);
            }
        });
    }

    private static void RemoveAll<T>(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(T))
            {
                services.RemoveAt(i);
            }
        }
    }

    public HttpClient ClientAs(string? apiKey)
    {
        var client = CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add(TestPrincipals.ApiKeyHeader, apiKey);
        }

        return client;
    }

    public HttpClient Anonymous() => CreateClient();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A test that leaves the spool locked should not fail teardown for it.
        }
    }
}

/// <summary>
/// How many times the queue was asked to accept a message.
/// </summary>
/// <remarks>
/// <b>Attempts, not acceptances.</b> The counter is incremented on entry to the intake, so it counts
/// every call, including one the queue refuses, and including a second call for a message the first
/// already stored. That is deliberately the thing being counted: the double-accept defect is two
/// <em>calls</em> under two different idempotency keys, and the queue deduplicates only what it can
/// see as one submission, so the count is the only place the second call is visible. Whether a row
/// resulted is a different question, answered by asking the queue.
///
/// <para>
/// Renamed from <c>Accepted</c> after a test asserted zero on a run where the queue was called once
/// and refused: the count was right and the name was a lie, which is the kind of thing a name does
/// quietly until someone reads it as a fact.
/// </para>
/// </remarks>
/// <summary>A clock whose "now" the test sets.</summary>
/// <remarks>
/// Exists so a test can choose the timestamps it is testing with. Ordering-by-time is exactly the
/// thing that cannot be tested against the wall clock: two writes in the same millisecond tie, and
/// which of them a keyset cursor resumes after then depends on the tiebreak rather than on the
/// property under test.
/// </remarks>
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class AcceptanceCounter
{
    private int _attempts;

    public int AcceptAttempts => Volatile.Read(ref _attempts);

    public void RecordAttempt() => Interlocked.Increment(ref _attempts);
}

/// <summary>
/// The real intake, with every acceptance counted.
/// </summary>
/// <remarks>
/// Delegates rather than replacing, so the message still lands in the queue exactly as it would in
/// production, a counting stub that stored nothing would make "accepted exactly once" a claim about
/// the stub. The counter is what the outcome cannot show: the queue deduplicates what it can see as
/// one submission, so two accepts under two different keys produce two deliveries that each look
/// correct from the call site that made them.
/// </remarks>
internal sealed class CountingSubmissionIntake : ISubmissionIntake
{
    private readonly QueueStore _store;
    private readonly AcceptanceCounter _counter;

    public CountingSubmissionIntake(QueueStore store, AcceptanceCounter counter)
    {
        _store = store;
        _counter = counter;
    }

    public Task<QueueAcceptResult> AcceptAsync(QueueSubmission submission, CancellationToken cancellationToken)
    {
        _counter.RecordAttempt();
        return _store.AcceptAsync(submission, cancellationToken);
    }

    public Task<SubmissionLookup?> FindAsync(string tenantId, string idempotencyKey, CancellationToken cancellationToken)
        => _store.FindSubmissionAsync(tenantId, idempotencyKey, cancellationToken);

    public Task<QueueItem?> GetAsync(string queueId, string tenantId, CancellationToken cancellationToken)
        => _store.GetItemAsync(queueId, tenantId, cancellationToken);

    public Task<bool> ResolveQuarantineAsync(
        string queueId,
        string tenantId,
        string decidedBy,
        CancellationToken cancellationToken)
        => _store.ResolveQuarantineAsync(
            queueId,
            QuarantineResolution.Release,
            decidedBy,
            tenantId,
            cancellationToken);
}

/// <summary>
/// An intake whose storage is permanently unavailable. Every acceptance raises the same exception
/// the real adapter raises when the spool or the database cannot be written.
/// </summary>
internal sealed class UnavailableSubmissionIntake : ISubmissionIntake
{
    public Task<QueueAcceptResult> AcceptAsync(QueueSubmission submission, CancellationToken cancellationToken)
        => throw new StorageUnavailableException("Storage is unavailable (injected by the test suite).");

    public Task<SubmissionLookup?> FindAsync(string tenantId, string idempotencyKey, CancellationToken cancellationToken)
        => throw new StorageUnavailableException("Storage is unavailable (injected by the test suite).");

    public Task<QueueItem?> GetAsync(string queueId, string tenantId, CancellationToken cancellationToken)
        => throw new StorageUnavailableException("Storage is unavailable (injected by the test suite).");

    public Task<bool> ResolveQuarantineAsync(
        string queueId,
        string tenantId,
        string decidedBy,
        CancellationToken cancellationToken)
        => throw new StorageUnavailableException("Storage is unavailable (injected by the test suite).");
}

/// <summary>
/// An <see cref="IMailAssessor"/> that records what it was asked and models the real pipeline's
/// contract at the acceptance seam.
/// </summary>
/// <remarks>
/// <b>This fake used to be far less than this, and that is why the suite was green over a broken
/// seam.</b> It returned an assessment and nothing else, so the host's own (wrong) queue call was
/// the only one that ever happened, and no test could see it. It now does the two things the real
/// assessor does that matter to the host:
///
/// <list type="number">
/// <item>reads the original bytes back through the envelope's durable payload reference, so the
/// host must actually have spooled them; and</item>
/// <item>performs acceptance itself, under the client's idempotency key, reporting the resulting
/// queue id on <c>SubmissionId</c>.</item>
/// </list>
/// </remarks>
internal sealed class RecordingAssessor : IMailAssessor
{
    private ISubmissionIntake? _intake;
    private SpoolStore? _spool;

    public List<(MailAnalysisInput Input, AssessmentContext Context)> Calls { get; } = [];

    public MailAction Action { get; set; } = MailAction.Allow;

    /// <summary>Models a pipeline that could not make acceptance durable. Reported as Defer.</summary>
    public bool AcceptanceRefused { get; set; }

    /// <summary>
    /// When set, every assessment throws this instead of answering.
    /// </summary>
    /// <remarks>
    /// The path a rejected provider credential takes: the adapter throws and the exception travels
    /// out of the pipeline. Modelling it here is what lets a test assert what the host does with that
    /// — which is the question, since "throws" is not by itself an answer about acknowledgements.
    /// </remarks>
    public Exception? Failure { get; set; }

    public int CallCount => Calls.Count;

    /// <summary>Wired by the host's service provider, as the real pipeline is.</summary>
    public void Connect(ISubmissionIntake intake, SpoolStore spool)
    {
        _intake = intake;
        _spool = spool;
    }

    public async ValueTask<MailAssessment> AssessAsync(
        MailAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken)
    {
        Calls.Add((input, context));

        if (Failure is not null)
        {
            throw Failure;
        }

        var action = Action;
        string? submissionId = null;
        SubmissionAdmission? admission = null;

        // Assessment-only means assessment only: no delivery state is created and none is implied.
        if (!context.AssessmentOnly && !AcceptanceRefused && action is not (MailAction.Defer or MailAction.Reject))
        {
            var bytes = ReadPayload(input.Envelope.PayloadReference);

            if (bytes is null)
            {
                // The real pipeline refuses to accept what it cannot produce rather than
                // manufacturing mail, so an unresolvable reference becomes a Defer and not an
                // exception. That is precisely why a host sending a reference naming nothing would
                // not fail loudly, it would quietly turn every submission into a Defer.
                action = MailAction.Defer;
            }
            else
            {
                var admissions = input.Envelope.RcptTo
                    .Select(recipient => new RecipientAdmission
                    {
                        Recipient = recipient,
                        State = action switch
                        {
                            MailAction.Hold => DeliveryState.Held,
                            MailAction.Quarantine => DeliveryState.Quarantined,
                            _ => DeliveryState.Queued,
                        },
                        ReEvaluateBy = action == MailAction.Hold
                            ? context.TimeProvider.GetUtcNow() + TimeSpan.FromSeconds(30)
                            : null,
                    })
                    .ToList();

                var accepted = await _intake!
                    .AcceptAsync(
                        new QueueSubmission
                        {
                            TenantId = context.TenantId,
                            InternalMessageId = input.Envelope.InternalMessageId,
                            Direction = input.Envelope.Direction,
                            TrustedPrincipalId = input.Envelope.TrustedPrincipalId,
                            MailFrom = input.Envelope.MailFrom,
                            MimeDigest = input.Envelope.MimeDigest,
                            Payload = bytes,
                            Recipients = admissions,
                            UntrustedMessageIdHeader = input.Envelope.UntrustedMessageIdHeader,
                            // The client's key, so a client retry is recognised as the same
                            // submission. This is the half of the contract the host depends on for
                            // its replay fast-path to be live rather than dead code.
                            //
                            // Deliberately no fallback when it is null, matching MailAssessor.cs
                            // exactly. Inventing one here would make the fake more permissive than
                            // the pipeline: the no-key case would look replay-protected in tests
                            // while in production a retry duplicates. A fake that is kinder than
                            // reality is the same defect as one that is harsher.
                            IdempotencyKey = context.ClientIdempotencyKey,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                submissionId = accepted.QueueId;

                // Model the admission as the real pipeline does, rather than leaving it null and
                // letting the host infer "created" by default. A fake that omits a field the
                // pipeline sets makes the host's handling of it untested, the same fidelity gap
                // that hid the acceptance seam.
                admission = accepted.Admission == QueueAdmission.DuplicateSubmission
                    ? SubmissionAdmission.Duplicate
                    : SubmissionAdmission.Created;
            }
        }

        return Build(input, context, action, submissionId, admission);
    }

    private byte[]? ReadPayload(string payloadReference)
    {
        if (!PayloadReferences.IsDurable(payloadReference))
        {
            return null;
        }

        using var stream = _spool?.OpenRead(payloadReference);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static MailAssessment Build(
        MailAnalysisInput input,
        AssessmentContext context,
        MailAction action,
        string? submissionId = null,
        SubmissionAdmission? admission = null,
        string? assessmentId = null) => new()
    {
        AssessmentId = assessmentId ?? $"asm_{Guid.NewGuid():N}",
        InternalMessageId = input.Envelope.InternalMessageId,
        TenantId = input.Envelope.TenantId,
        Evidence = [],
        RiskDimensions = [],
        RiskIndex = action == MailAction.Allow ? 0.05 : 0.95,
        Action = action,
        ProposedActionInShadow = context.ShadowMode ? action : null,
        DeliveryTiming = DeliveryTiming.PreAcceptance,
        SubmissionId = submissionId,

        // Core pins the invariant that this is null exactly when SubmissionId is; the fake must
        // obey it too, or it would certify behaviour the real pipeline cannot produce.
        Submission = submissionId is null ? null : admission,
        Reasons =
        [
            new ReasonCode
            {
                Code = action == MailAction.Defer ? "assessment.acceptance_refused" : "test.decision",
                Message = "Synthetic decision supplied by the test assessor.",
                EvidenceSignalIds = [],
            },
        ],
        Versions = new AssessmentVersions
        {
            PolicyVersion = "policy/test",
            ClassifierModelVersion = "jev-test",
            QuestionSchemaVersion = "questions/test",
            PreprocessingVersion = "preprocess/test",
        },
        Coverage = new AnalysisCoverage
        {
            BodyParsed = true,
            HtmlPresent = false,
            HasAttachments = false,
            HtmlTextDisagreement = false,
            ParserLimitExceeded = false,
            ContentEncrypted = false,
            Truncated = false,
            ConversationContextMissing = true,
        },
        RecipientDispositions = [.. input.Envelope.RcptTo.Select(r => new RecipientDisposition
        {
            Recipient = r,
            RecipientRisk = 0.05,
            Action = action,
            DeliveryState = action switch
            {
                MailAction.Quarantine => DeliveryState.Quarantined,
                MailAction.Hold => DeliveryState.Held,
                _ => DeliveryState.Queued,
            },
        })],
        AssessedAt = context.TimeProvider.GetUtcNow(),
    };
}

/// <summary>
/// A minimal SMTP client. Deliberately hand-written rather than a library: these tests are about
/// what is on the wire from an ordinary client, and a library's own conventions would be one
/// more thing between the assertion and the bytes.
/// </summary>
internal sealed class SmtpClient : IDisposable
{
    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private SmtpClient(TcpClient client)
    {
        _client = client;
        var stream = client.GetStream();
        _reader = new StreamReader(stream, Encoding.ASCII);
        _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
    }

    public static async Task<SmtpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        return new SmtpClient(client);
    }

    /// <summary>Writes a command and returns the reply it produced.</summary>
    /// <remarks>
    /// Returning rather than asserting, so a test that expects a refusal can look at what came
    /// back instead of having to have predicted it before sending.
    /// </remarks>
    public async Task<string> SendAsync(string command)
    {
        await _writer.WriteLineAsync(command);
        return await ReadReplyAsync();
    }

    /// <summary>Writes a command and asserts the reply's code.</summary>
    public async Task<string> SendExpectingAsync(string command, string code)
    {
        var reply = await SendAsync(command);
        Assert.StartsWith(code, reply, StringComparison.Ordinal);
        return reply;
    }

    /// <summary>Writes a message body and its terminator, returning the reply to the end of data.</summary>
    public async Task<string> SendBodyAsync(string message)
    {
        var normalised = message.Replace("\r\n", "\n").Replace("\n", "\r\n");

        foreach (var line in normalised.Split("\r\n"))
        {
            // Dot-stuffing, as a real client does: a line consisting of a single dot would
            // otherwise be read as the end of the message.
            await _writer.WriteLineAsync(line.StartsWith('.') ? "." + line : line);
        }

        await _writer.WriteLineAsync(".");
        return await ReadReplyAsync();
    }

    public async Task ExpectAsync(string code)
    {
        var reply = await ReadReplyAsync();
        Assert.StartsWith(code, reply, StringComparison.Ordinal);
    }

    /// <summary>Reads one complete reply, following continuation lines to the last one.</summary>
    private async Task<string> ReadReplyAsync()
    {
        for (var i = 0; i < 40; i++)
        {
            var line = await _reader.ReadLineAsync();

            if (line is null)
            {
                throw new InvalidOperationException("The server closed the connection mid-reply.");
            }

            // "250-Line" continues; "250 Line" is the last line of the reply.
            if (line.Length < 4 || line[3] != '-')
            {
                return line;
            }
        }

        throw new InvalidOperationException("Reply never terminated.");
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
        _client.Dispose();
    }
}
