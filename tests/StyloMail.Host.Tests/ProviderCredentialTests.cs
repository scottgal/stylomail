using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StyloMail.Core;
using StyloMail.Host.Hosting;
using StyloMail.Jev;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// A rejected semantic-provider credential, and what the host does about it.
/// </summary>
/// <remarks>
/// <para>
/// This is the failure this project exists to eliminate, in its purest form: **a deployment whose
/// provider key has been rotated looks healthy to a load balancer and fails every message.** The Jev
/// adapter throws loudly on a 401 by design, a revoked key must never present as a calm inbox, but
/// nothing connected that loudness to `/health/ready`, so the probe answered <c>200 ready</c> while
/// every assessment 500ed.
/// </para>
/// <para>
/// Two separate questions, and both are tested here: does readiness learn about it, and does a host
/// that cannot assess still avoid acknowledging mail?
/// </para>
/// </remarks>
public sealed class ProviderCredentialTests
{
    // ---------------------------------------------------------------------------------------------
    // Readiness
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_rejected_credential_makes_the_host_report_not_ready()
    {
        using var host = new TestHost();

        using (var client = host.Anonymous())
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        }

        host.Services.GetRequiredService<ProviderCredentialHealth>()
            .RecordRejected(HttpStatusCode.Unauthorized, DateTimeOffset.UnixEpoch);

        using var after = host.Anonymous();
        var response = await after.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_ready", body.RootElement.GetProperty("status").GetString());

        // Named, never described. This route is served without credentials, so the status the
        // provider answered with stays internal: an operator gets it from the log and the metric.
        var failed = body.RootElement.GetProperty("failedChecks")
            .EnumerateArray().Select(c => c.GetString()).ToList();

        Assert.Contains("provider_credential", failed);
        Assert.DoesNotContain("401", string.Join(",", failed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Liveness_is_unaffected_by_a_rejected_credential()
    {
        // Deliberately still live. A dependency outage is not a reason to have a container restarted:
        // restarting changes nothing about a revoked key, and a liveness probe that failed here would
        // turn a configuration problem into a restart loop.
        using var host = new TestHost();

        host.Services.GetRequiredService<ProviderCredentialHealth>()
            .RecordRejected(HttpStatusCode.Unauthorized, DateTimeOffset.UnixEpoch);

        using var client = host.Anonymous();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Readiness_recovers_when_a_classification_succeeds()
    {
        using var host = new TestHost();
        var health = host.Services.GetRequiredService<ProviderCredentialHealth>();

        health.RecordRejected(HttpStatusCode.Unauthorized, DateTimeOffset.UnixEpoch);

        using (var notReady = host.Anonymous())
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await notReady.GetAsync("/health/ready")).StatusCode);
        }

        health.RecordAccepted();

        using var recovered = host.Anonymous();
        Assert.Equal(HttpStatusCode.OK, (await recovered.GetAsync("/health/ready")).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // The decorator that observes the rejection
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_credential_rejection_is_recorded_and_still_thrown(HttpStatusCode status)
    {
        var health = new ProviderCredentialHealth();
        var classifier = new CredentialAwareSemanticClassifier(
            new ThrowingClassifier(new JevContractException("rejected", status)),
            health,
            TimeProvider.System);

        await Assert.ThrowsAsync<JevContractException>(
            async () => await classifier.ClassifyAsync(Input(), CancellationToken.None));

        // Recorded *and* rethrown. The loudness was right; what was missing was a reader.
        Assert.True(health.IsRejected);
        Assert.Equal(status, health.RejectedWith);
    }

    [Fact]
    public async Task A_contract_fault_is_not_treated_as_a_credential_problem()
    {
        // A 422 means our request shape is wrong: a bug, not a deployment condition. Making the host
        // not-ready for it would take a service out of rotation over something a restart cannot fix.
        var health = new ProviderCredentialHealth();
        var classifier = new CredentialAwareSemanticClassifier(
            new ThrowingClassifier(new JevContractException("bad request", HttpStatusCode.UnprocessableEntity)),
            health,
            TimeProvider.System);

        await Assert.ThrowsAsync<JevContractException>(
            async () => await classifier.ClassifyAsync(Input(), CancellationToken.None));

        Assert.False(health.IsRejected);
    }

    [Fact]
    public async Task An_unavailable_result_does_not_clear_a_rejection()
    {
        // The subtle one, and the shape of a mistake that would have made the whole fix useless: an
        // `Unavailable` assessment is what the adapter returns when the provider *failed*, so treating
        // it as a success would put the host back to advertising ready while every message kept losing
        // its semantic evidence. Only a resolved model id proves the provider answered.
        var health = new ProviderCredentialHealth();
        health.RecordRejected(HttpStatusCode.Unauthorized, DateTimeOffset.UnixEpoch);

        var classifier = new CredentialAwareSemanticClassifier(
            new ReturningClassifier(Assessment(resolvedModel: null)),
            health,
            TimeProvider.System);

        await classifier.ClassifyAsync(Input(), CancellationToken.None);

        Assert.True(health.IsRejected);
    }

    [Fact]
    public async Task A_provider_answer_clears_a_rejection()
    {
        var health = new ProviderCredentialHealth();
        health.RecordRejected(HttpStatusCode.Unauthorized, DateTimeOffset.UnixEpoch);

        var classifier = new CredentialAwareSemanticClassifier(
            new ReturningClassifier(Assessment(resolvedModel: "jev-1.13.0")),
            health,
            TimeProvider.System);

        await classifier.ClassifyAsync(Input(), CancellationToken.None);

        Assert.False(health.IsRejected);
    }

    // ---------------------------------------------------------------------------------------------
    // A host that cannot assess must not acknowledge mail
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_message_the_failing_assessor_cannot_assess_is_never_acknowledged()
    {
        // The chain a rejected key actually produces, driven end to end through the real listener:
        // the classifier throws, the pipeline rethrows, the sink does not catch it, and the transport's
        // own defence turns it into a deferral. The assertion that matters is the one the client sees
        //, 451, not 250, because a 250 tells it to delete its copy.
        using var host = new TestHost().WithSmtpIngress("example.test");
        host.Assessor.Failure = new JevContractException("rejected the API key", HttpStatusCode.Unauthorized);

        using var client = await SmtpClient.ConnectAsync(host.BoundIngressPort);

        await client.ExpectAsync("220");
        await client.SendExpectingAsync("EHLO probe.local", "250");
        await client.SendExpectingAsync("MAIL FROM:<sender@example.com>", "250");
        await client.SendExpectingAsync("RCPT TO:<recipient@example.test>", "250");
        await client.SendExpectingAsync("DATA", "354");

        var reply = await client.SendBodyAsync(TestMessages.SampleMime);

        Assert.StartsWith("451", reply, StringComparison.Ordinal);

        var counts = await host.Services.GetRequiredService<QueueStore>().CountByStateAsync("inbound");
        Assert.Empty(counts);
    }

    [Fact]
    public async Task And_the_http_submission_route_does_not_answer_with_a_2xx_either()
    {
        using var host = new TestHost();
        host.Assessor.Failure = new JevContractException("rejected the API key", HttpStatusCode.Unauthorized);

        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/submissions")
        {
            Content = JsonContent.Create(TestMessages.Request()),
        };

        request.Headers.Add("Idempotency-Key", "key-failing-provider");

        // **TestServer rethrows an unhandled exception to the caller where Kestrel would answer 500**,
        // so this request either throws here or comes back a 5xx, and which of the two happens is a
        // property of the server, not of the claim being tested. Asserting a status code alone would
        // therefore be asserting about TestServer.
        //
        // What must hold under either is the thing that would be a defect rather than an ugly status
        // code: no 2xx, because a 2xx here transfers delivery responsibility for mail that was never
        // assessed. The 500 itself is reported separately: it is worth improving, and it is not this.
        HttpResponseMessage? response = null;

        try
        {
            response = await client.SendAsync(request);
        }
        catch (JevContractException)
        {
            // The Kestrel behaviour, surfaced as an exception by the test server.
        }

        if (response is not null)
        {
            Assert.False(
                response.IsSuccessStatusCode,
                $"A submission answered {(int)response.StatusCode} while the assessor could not "
                + "assess. A 2xx here tells the caller their mail was accepted when nothing was stored.");
        }

        var counts = await host.Services.GetRequiredService<QueueStore>()
            .CountByStateAsync(TestPrincipals.AcmeTenant);

        Assert.Empty(counts);
    }

    [Fact]
    public void A_deployment_with_no_provider_configured_is_unaffected()
    {
        // The sentinel refuses everything and never touches the provider, so a deployment with no
        // Assessment configuration must not be reported not-ready for a credential it does not hold.
        using var host = new TestHost().WithoutAssessor();

        Assert.False(host.Services.GetRequiredService<ProviderCredentialHealth>().IsRejected);
    }

    // ---------------------------------------------------------------------------------------------

    private static SemanticMailInput Input() => new()
    {
        Message = new MailAnalysisInput
        {
            Envelope = new MailEnvelope
            {
                InternalMessageId = "msg_credential",
                TenantId = TestPrincipals.AcmeTenant,
                Direction = MailDirection.Outbound,
                TrustedPrincipalId = TestPrincipals.AcmeSenderPrincipal,
                MailFrom = "sender@example.com",
                RcptTo = ["recipient@example.com"],
                ReceivedAt = DateTimeOffset.UnixEpoch,
                MimeDigest = new string('a', 64),
                PayloadReference = PayloadReferences.Ephemeral,
            },
            Authentication = new AuthenticationContext
            {
                ConnectingIp = null,
                AuthenticatedAccount = null,
                Results = [],
                ApprovedSenderIdentities = [],
                ProvenanceIncomplete = true,
            },
            Channel = ChannelContext.Email,
            BodyText = string.Empty,
            Links = [],
            Attachments = [],
            Coverage = new AnalysisCoverage
            {
                BodyParsed = false,
                HtmlPresent = false,
                HasAttachments = false,
                HtmlTextDisagreement = false,
                ParserLimitExceeded = false,
                ContentEncrypted = false,
                Truncated = false,
                ConversationContextMissing = true,
            },
        },
        Dimensions = [],
    };

    private static SemanticAssessment Assessment(string? resolvedModel) => new()
    {
        Evidence = [],
        ResolvedModelVersion = resolvedModel,
        Cache = new CacheProvenance { Hit = false, KeyDigest = "digest", Stale = false },
    };

    private sealed class ThrowingClassifier : ISemanticMailClassifier
    {
        private readonly Exception _failure;

        public ThrowingClassifier(Exception failure) => _failure = failure;

        public ValueTask<SemanticAssessment> ClassifyAsync(SemanticMailInput input, CancellationToken cancellationToken)
            => throw _failure;
    }

    private sealed class ReturningClassifier : ISemanticMailClassifier
    {
        private readonly SemanticAssessment _assessment;

        public ReturningClassifier(SemanticAssessment assessment) => _assessment = assessment;

        public ValueTask<SemanticAssessment> ClassifyAsync(SemanticMailInput input, CancellationToken cancellationToken)
            => ValueTask.FromResult(_assessment);
    }
}

/// <summary>
/// Binding the semantic provider's endpoint and model from configuration.
/// </summary>
/// <remarks>
/// These were hardcoded while <c>IConfiguration</c> sat in scope as a parameter, so
/// <c>StyloMail:Jev:Endpoint</c> could be set, appear accepted, and be silently ignored. Configuration
/// that looks like it works and does nothing is worse than configuration that is absent, because the
/// operator stops looking for the reason their change had no effect.
/// </remarks>
public sealed class JevOptionsBindingTests
{
    [Fact]
    public void The_endpoint_and_model_default_to_the_pinned_values()
    {
        var options = HostServices.BuildJevOptions(Configuration(), "key", NullLogger());

        var defaults = new JevOptions();

        Assert.Equal(defaults.Endpoint, options.Endpoint);
        Assert.Equal(defaults.Model, options.Model);
    }

    [Fact]
    public void A_configured_endpoint_and_model_are_honoured()
    {
        var options = HostServices.BuildJevOptions(
            Configuration(
                ("StyloMail:Jev:Endpoint", "http://127.0.0.1:9/v1/systemone"),
                ("StyloMail:Jev:Model", "jev-0.0.0-local")),
            "key",
            NullLogger());

        Assert.Equal("http://127.0.0.1:9/v1/systemone", options.Endpoint);
        Assert.Equal("jev-0.0.0-local", options.Model);
    }

    [Fact]
    public void A_non_default_endpoint_is_announced_at_startup()
    {
        // The endpoint decides who receives the mail this deployment processes, so redirecting it is a
        // legitimate operator decision and a silent one is not. A warning rather than an error: this
        // is a choice the operator is entitled to make, and it is the *silence* that would be wrong.
        var logger = new CapturingLogger();

        HostServices.BuildJevOptions(
            Configuration(("StyloMail:Jev:Endpoint", "http://127.0.0.1:9/v1/systemone")),
            "key",
            logger);

        var warning = Assert.Single(logger.Warnings);

        Assert.Contains("ENDPOINT OVERRIDDEN", warning, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:9/v1/systemone", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_endpoint_is_not_announced()
    {
        // A warning on every boot is a warning nobody reads, and it would bury the case that matters.
        var logger = new CapturingLogger();

        HostServices.BuildJevOptions(Configuration(), "key", logger);

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void The_api_key_is_never_written_to_the_log()
    {
        // The one thing this method handles that must not escape. Asserted rather than assumed,
        // because a future "log the resolved options" line would be a natural and quiet mistake.
        var logger = new CapturingLogger();

        HostServices.BuildJevOptions(
            Configuration(("StyloMail:Jev:Endpoint", "http://127.0.0.1:9/v1/systemone")),
            "super-secret-api-key-value",
            logger);

        Assert.DoesNotContain("super-secret-api-key-value", string.Join("\n", logger.Warnings), StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static ILogger NullLogger() => new CapturingLogger();

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}

/// <summary>
/// The real adapter against a provider that refuses the credential.
/// </summary>
/// <remarks>
/// The unit tests above use a stub classifier, so they prove the decorator records what it is given.
/// They say nothing about whether the real adapter *gives* it a credential rejection, and that is the
/// half that decides whether the whole fix is connected to anything. This drives the actual
/// <see cref="JevSemanticMailClassifier"/> against a provider answering 401.
/// </remarks>
public sealed class JevCredentialRejectionTests
{
    [Fact]
    public async Task The_real_adapter_reports_a_401_as_a_credential_rejection()
    {
        using var provider = new CannedProvider(401, "{}");

        var health = new ProviderCredentialHealth();
        var classifier = new CredentialAwareSemanticClassifier(
            new JevSemanticMailClassifier(
                new HttpClient(),
                new JevOptions { ApiKey = "a-key-the-provider-refuses", Endpoint = provider.Url },
                TimeProvider.System),
            health,
            TimeProvider.System);

        await Assert.ThrowsAsync<JevContractException>(
            async () => await classifier.ClassifyAsync(Input(), CancellationToken.None));

        Assert.True(health.IsRejected);
        Assert.Equal(HttpStatusCode.Unauthorized, health.RejectedWith);
    }

    [Fact]
    public async Task A_failing_provider_that_is_not_a_credential_problem_does_not_latch_not_ready()
    {
        // A 500 from the provider is an outage: the adapter returns `Unavailable`, the pipeline
        // degrades as the spec requires, and readiness stays out of it. Latching not-ready for every
        // provider hiccup would take a healthy host out of rotation for a fault it cannot fix.
        using var provider = new CannedProvider(500, "{}");

        var health = new ProviderCredentialHealth();
        var classifier = new CredentialAwareSemanticClassifier(
            new JevSemanticMailClassifier(
                new HttpClient(),
                new JevOptions { ApiKey = "a-key", Endpoint = provider.Url },
                TimeProvider.System),
            health,
            TimeProvider.System);

        await classifier.ClassifyAsync(Input(), CancellationToken.None);

        Assert.False(health.IsRejected);
    }

    private static SemanticMailInput Input() => new()
    {
        Message = new MailAnalysisInput
        {
            Envelope = new MailEnvelope
            {
                InternalMessageId = "msg_real",
                TenantId = TestPrincipals.AcmeTenant,
                Direction = MailDirection.Outbound,
                TrustedPrincipalId = TestPrincipals.AcmeSenderPrincipal,
                MailFrom = "sender@example.com",
                RcptTo = ["recipient@example.com"],
                ReceivedAt = DateTimeOffset.UnixEpoch,
                MimeDigest = new string('a', 64),
                PayloadReference = PayloadReferences.Ephemeral,
            },
            Authentication = new AuthenticationContext
            {
                ConnectingIp = null,
                AuthenticatedAccount = null,
                Results = [],
                ApprovedSenderIdentities = [],
                ProvenanceIncomplete = true,
            },
            Channel = ChannelContext.Email,
            BodyText = "Please review the figures.",
            Links = [],
            Attachments = [],
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
        },
        Dimensions = SemanticDimensions.All,
    };

}

/// <summary>A provider that answers every request with one canned response.</summary>
internal sealed class CannedProvider : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _status;
    private readonly string _body;
    private readonly CancellationTokenSource _cts = new();

    public CannedProvider(int status, string body)
    {
        _status = status;
        _body = body;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/systemone";

        _ = Task.Run(ServeAsync);
    }

    public string Url { get; }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            using (client)
            {
                var stream = client.GetStream();

                // Read the request head before answering, so the client is not still writing when the
                // response lands. A server that answers and closes mid-request makes the client see a
                // connection fault rather than the status, which is a different thing entirely, and
                // was the bug in this stub that cost an afternoon of chasing the wrong component.
                //
                // The byte count is used rather than discarded: ignoring it means not knowing whether
                // anything arrived, and this stub's whole job is to reach a definite state before it
                // answers. CA2022 flagged the first version for exactly that, and it was right.
                try
                {
                    await ReadHeadAsync(stream, _cts.Token);
                }
                catch (Exception)
                {
                    // The client gave up; the answer no longer matters.
                }

                var payload = System.Text.Encoding.UTF8.GetBytes(_body);
                var head = System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {_status} X\r\nContent-Type: application/json\r\n"
                    + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");

                await stream.WriteAsync(head, _cts.Token);
                await stream.WriteAsync(payload, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
        }
    }

    /// <summary>Reads up to the end of the request headers, or until the client stops.</summary>
    private static async Task ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var read = 0;

        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);

            if (chunk == 0)
            {
                return;
            }

            read += chunk;

            // The head ends at the first blank line; anything after it is body, which this stub does
            // not need.
            if (buffer.AsSpan(0, read).IndexOf("\r\n\r\n"u8) >= 0)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
    }

/// <summary>
/// The whole real pipeline against a provider that refuses the credential.
/// </summary>
/// <remarks>
/// <para>
/// The strongest available proof of the fix, short of a live provider: the actual
/// <see cref="JevSemanticMailClassifier"/> behind the actual
/// <see cref="CredentialAwareSemanticClassifier"/>, composed by the actual
/// <c>AssessmentPipeline.Create</c> into the actual <c>MailAssessor</c>. Nothing here is a stub except
/// the provider's HTTP answer.
/// </para>
/// <para>
/// It exists because an out-of-process probe could not produce a 401 reliably: a Python
/// <c>http.server</c> answered in a way the client read as a connection fault, which the adapter
/// correctly degrades to <c>Unavailable</c>. That degradation is right; what the probe could not then
/// show is the credential path, and this can.
/// </para>
/// </remarks>
public sealed class PipelineCredentialRejectionTests
{
    [Fact]
    public async Task A_401_from_the_provider_reaches_readiness_through_the_real_pipeline()
    {
        using var provider = new CannedProvider(401, "{}");

        var root = Path.Combine(Path.GetTempPath(), "stylomail-credential-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var health = new ProviderCredentialHealth();
            var clock = TimeProvider.System;

            var connections = new StyloMail.Persistence.SqliteConnectionFactory(Path.Combine(root, "host.db"));

            using (var connection = connections.Open())
            {
                StyloMail.Persistence.SqliteSchema.EnsureCreated(connection);
            }

            var assessor = StyloMail.Assessment.AssessmentPipeline.Create(
                new StyloMail.Mime.BoundedMimeMessageAnalyzer(),
                new CredentialAwareSemanticClassifier(
                    new JevSemanticMailClassifier(
                        new HttpClient(),
                        new JevOptions { ApiKey = "a-key-the-provider-refuses", Endpoint = provider.Url },
                        clock),
                    health,
                    clock),
                connections,
                new SpoolStore(Path.Combine(root, "spool")),
                new StyloMail.Assessment.MailAssessorOptions
                {
                    ProfileKeyHasher = new StyloMail.Adaptive.Profiles.ProfileKeyHasher(
                        System.Text.Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")),
                },
                new QueueOptions { TimeProvider = clock });

            await Assert.ThrowsAsync<JevContractException>(async () =>
                await assessor.AssessAsync(
                    Input(TestPrincipals.AcmeTenant),
                    new AssessmentContext
                    {
                        TenantId = TestPrincipals.AcmeTenant,
                        ShadowMode = false,

                        // Assessment-only, so the pipeline reaches the classifier and stops there:
                        // no payload, no acceptance, nothing durable to clean up.
                        AssessmentOnly = true,
                        CorrelationId = "cor_credential_test",
                        TimeProvider = clock,
                    },
                    CancellationToken.None));

            // And the thing the whole change is for.
            Assert.True(health.IsRejected);
            Assert.Equal(HttpStatusCode.Unauthorized, health.RejectedWith);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static MailAnalysisInput Input(string tenantId) => new()
    {
        Envelope = new MailEnvelope
        {
            InternalMessageId = "msg_pipeline",
            TenantId = tenantId,
            Direction = MailDirection.Outbound,
            TrustedPrincipalId = TestPrincipals.AcmeSenderPrincipal,
            MailFrom = "sender@example.com",
            RcptTo = ["recipient@example.com"],
            ReceivedAt = DateTimeOffset.UnixEpoch,
            MimeDigest = new string('a', 64),

            // Ephemeral: assessment-only traffic carries no payload by definition, and the pipeline
            // must still run its semantic step.
            PayloadReference = PayloadReferences.Ephemeral,
        },
        Authentication = new AuthenticationContext
        {
            ConnectingIp = null,
            AuthenticatedAccount = TestPrincipals.AcmeSenderPrincipal,
            Results = [],

            // Matches the envelope sender, so step one raises no unapproved-identity violation and
            // the pipeline actually reaches the classifier.
            ApprovedSenderIdentities = ["sender@example.com"],
            ProvenanceIncomplete = true,
        },
        Channel = ChannelContext.Email,
        BodyText = "Please review the figures.",
        Links = [],
        Attachments = [],
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
    };
}
