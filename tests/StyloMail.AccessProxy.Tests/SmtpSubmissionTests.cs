using System.Text;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Smtp;
using StyloMail.AccessProxy.Submission;
using StyloMail.AccessProxy.Tests.Support;
using StyloMail.Embeddings;

namespace StyloMail.AccessProxy.Tests;

public sealed class SmtpSubmissionTests
{
    [Fact]
    public async Task Auth_before_starttls_is_refused_without_backend_attempt()
    {
        var h = new ProxyHarness();
        h.AddAccount();
        var backend = new ScriptedSmtpConnector();
        var session = NewSession(h, backend, new AllowAssessment(), new InMemoryEphemeralSubmissionStager());
        var client = new StartTlsTestChannel();
        var run = session.RunAsync(client, CancellationToken.None);
        await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("AUTH PLAIN AGFsaWNlQGV4YW1wbGUuY29tAGNsaWVudC1zaWRlLXBhc3N3b3JkLTlmM2E=\r\n");
        Assert.StartsWith("538", await client.Pipe.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(0, backend.ConnectCount);
        await client.Pipe.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task Auth_after_starttls_is_accepted_for_the_listener_tenant()
    {
        var h = new ProxyHarness(); h.AddAccount();
        var backend = new ScriptedSmtpConnector();
        var session = NewSession(h, backend, new AllowAssessment(), new InMemoryEphemeralSubmissionStager());
        var client = new StartTlsTestChannel(); var run = session.RunAsync(client, CancellationToken.None);
        await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("EHLO client\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("STARTTLS\r\n"); Assert.StartsWith("220", await client.Pipe.ReadLineAsync(), StringComparison.Ordinal);
        await client.Pipe.SendAsync("EHLO client\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.ReadLineAsync();
        var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{ProxyHarness.Login}\0{ProxyHarness.ClientPassword}"));
        await client.Pipe.SendAsync($"AUTH PLAIN {plain}\r\n");
        Assert.StartsWith("235", await client.Pipe.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(ProxyHarness.TenantId, backend.LastRequest!.TenantId);
        await client.Pipe.ClosePeerWriteAsync(); await run;
    }

    [Fact]
    public async Task Ehlo_advertises_exactly_the_capability_for_channel_state()
    {
        var h = new ProxyHarness(); var backend = new ScriptedSmtpConnector();
        var session = NewSession(h, backend, new AllowAssessment(), new InMemoryEphemeralSubmissionStager());
        var client = new StartTlsTestChannel(); var run = session.RunAsync(client, CancellationToken.None);
        await client.Pipe.ReadLineAsync(); await client.Pipe.SendAsync("EHLO client\r\n");
        var plain = (await client.Pipe.ReadLineAsync()) + "\n" + await client.Pipe.ReadLineAsync();
        Assert.Contains("STARTTLS", plain); Assert.DoesNotContain("AUTH", plain);
        await client.Pipe.SendAsync("STARTTLS\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.SendAsync("EHLO client\r\n");
        var secure = (await client.Pipe.ReadLineAsync()) + "\n" + await client.Pipe.ReadLineAsync();
        Assert.Contains("AUTH PLAIN LOGIN", secure); Assert.DoesNotContain("STARTTLS", secure);
        await client.Pipe.ClosePeerWriteAsync(); await run;
    }

    [Fact]
    public async Task Deterministic_refusal_never_relays_data_to_backend()
    {
        var h = new ProxyHarness(); h.AddAccount(); var backend = new ScriptedSmtpConnector();
        var session = NewSession(h, backend, new RefuseAssessment(), new InMemoryEphemeralSubmissionStager());
        var client = await AuthenticateAsync(session);
        await client.Pipe.SendAsync("MAIL FROM:<a@example.com>\r\n"); await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("RCPT TO:<b@example.com>\r\n"); await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("DATA\r\nbody\r\n.\r\n"); await client.Pipe.ReadLineAsync(); Assert.StartsWith("554", await client.Pipe.ReadLineAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(backend.Received, x => x == "DATA");
        await client.Pipe.ClosePeerWriteAsync();
    }

    [Fact]
    public async Task Stager_releases_bytes_after_allow_refusal_and_abort()
    {
        foreach (var assessment in new IProxySubmissionAssessment[] { new AllowAssessment(), new RefuseAssessment(), new AllowAssessment() })
        {
            var stager = new InMemoryEphemeralSubmissionStager();
            await using var staged = await stager.CreateAsync(128, CancellationToken.None);
            await staged.Content.WriteAsync("body"u8.ToArray(), CancellationToken.None);
            await staged.DisposeAsync();
            Assert.Equal(0, stager.ActiveCount);
        }
    }

    [Fact]
    public async Task Warning_tap_records_clean_and_marked_submissions_once_each()
    {
        foreach (var findings in new[] { Array.Empty<ProxyWarningFinding>(), new[] { Finding(ProxyWarningMarkerKind.LinkDomainMismatch), Finding(ProxyWarningMarkerKind.DisplayNameMismatch) } })
        {
            var h = new ProxyHarness(); h.AddAccount();
            var sink = new CapturingWarningSink();
            var session = NewSession(h, new ScriptedSmtpConnector(), new AllowAssessment(), new InMemoryEphemeralSubmissionStager(), sink, new StaticWarningAnalyzer(findings));
            var client = await AuthenticateAsync(session);
            await client.Pipe.SendAsync("MAIL FROM:<a@example.com>\r\n"); await client.Pipe.ReadLineAsync();
            await client.Pipe.SendAsync("RCPT TO:<b@example.com>\r\n"); await client.Pipe.ReadLineAsync();
            await client.Pipe.SendAsync("DATA\r\nbody\r\n.\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.ReadLineAsync();
            Assert.Single(sink.Outcomes);
            Assert.Equal(findings.Length, sink.Outcomes[0].MarkerCount);
            Assert.Equal(findings.Length, sink.Markers.Count);
            await client.Pipe.ClosePeerWriteAsync();
        }
    }

    [Fact]
    public async Task Throwing_element_sink_cannot_drop_a_submission_or_the_session()
    {
        var h = new ProxyHarness(); h.AddAccount();
        var backend = new ScriptedSmtpConnector();
        var sink = new ThrowingElementSink();
        var stager = new InMemoryEphemeralSubmissionStager();
        var session = NewSession(h, backend, new AllowAssessment(), stager,
            elementOrchestrator: new ProxyElementOrchestrator(new CompleteElementClassifier(), sink));
        var client = new StartTlsTestChannel();
        var run = session.RunAsync(client, CancellationToken.None);

        await AuthenticateAsync(client);
        await client.Pipe.SendAsync("MAIL FROM:<a@example.com>\r\n"); await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("RCPT TO:<b@example.com>\r\n"); await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("DATA\r\nbody\r\n.\r\n");
        Assert.StartsWith("354", await client.Pipe.ReadLineAsync(), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.Pipe.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(1, sink.PointAttempts);
        Assert.Contains("DATA", backend.Received);
        Assert.Equal(0, stager.ActiveCount);

        await client.Pipe.SendAsync("QUIT\r\n");
        Assert.StartsWith("221", await client.Pipe.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(SessionOutcome.Relayed, await run);
    }

    private static SmtpAccessProxySession NewSession(ProxyHarness h, IBackendConnector backend, IProxySubmissionAssessment assessment, IEphemeralSubmissionStager stager, IProxyWarningEventSink? warningSink = null, IProxyWarningAnalyzer? warningAnalyzer = null, ProxyElementOrchestrator? elementOrchestrator = null)
        => new(ProxyHarness.TenantId, h.AccountStore, h.PasswordHasher, backend, h.Limiter, h.Bounds, h.Clock, assessment, stager,
            warningSink: warningSink ?? new WarningSink(), warningAnalyzer: warningAnalyzer ?? new WarningAnalyzer(), elementOrchestrator: elementOrchestrator);

    private static async Task<StartTlsTestChannel> AuthenticateAsync(SmtpAccessProxySession session)
    {
        var client = new StartTlsTestChannel(); _ = session.RunAsync(client, CancellationToken.None);
        await AuthenticateAsync(client);
        return client;
    }

    private static async Task AuthenticateAsync(StartTlsTestChannel client)
    {
        await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("EHLO x\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.ReadLineAsync(); await client.Pipe.SendAsync("STARTTLS\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.SendAsync("EHLO x\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.ReadLineAsync();
        var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{ProxyHarness.Login}\0{ProxyHarness.ClientPassword}")); await client.Pipe.SendAsync($"AUTH PLAIN {plain}\r\n"); await client.Pipe.ReadLineAsync();
    }

    private sealed class StartTlsTestChannel : IStartTlsCapableChannel, ISecureDuplexChannel
    {
        public PipeDuplex Pipe { get; } = new("smtp-client"); public bool IsSecure { get; private set; }
        public Stream Input => Pipe.Input; public Stream Output => Pipe.Output; public string Description => Pipe.Description;
        public ValueTask<ISecureDuplexChannel> UpgradeToTlsAsync(CancellationToken cancellationToken) { IsSecure = true; return ValueTask.FromResult<ISecureDuplexChannel>(this); }
        public ValueTask DisposeAsync() => Pipe.DisposeAsync();
    }
    private sealed class AllowAssessment : IProxySubmissionAssessment { public ValueTask<ProxySubmissionAssessmentResult> AssessAsync(IStagedProxySubmission s, CancellationToken c) => ValueTask.FromResult(new ProxySubmissionAssessmentResult { Disposition = ProxySubmissionDisposition.Allow, IsDeterministic = true }); }
    private sealed class RefuseAssessment : IProxySubmissionAssessment { public ValueTask<ProxySubmissionAssessmentResult> AssessAsync(IStagedProxySubmission s, CancellationToken c) => ValueTask.FromResult(new ProxySubmissionAssessmentResult { Disposition = ProxySubmissionDisposition.Refuse, IsDeterministic = true }); }
    private sealed class ScriptedSmtpConnector : IBackendConnector
    {
        public int ConnectCount; public BackendConnectionRequest? LastRequest; public List<string> Received { get; } = [];
        public string ProviderLabel => "test";
        public ValueTask<IDuplexChannel> ConnectAsync(BackendConnectionRequest request, CancellationToken cancellationToken) { ConnectCount++; LastRequest = request; var p = new PipeDuplex("smtp-backend"); _ = Serve(p); return ValueTask.FromResult<IDuplexChannel>(p); }
        private async Task Serve(PipeDuplex p) { while (true) { var line = await p.ReadLineAsync(); if (line.Length == 0) return; Received.Add(line); if (line == "DATA") { await p.SendAsync("354 go\r\n"); while (await p.ReadLineAsync() != ".") { } await p.SendAsync("250 ok\r\n"); } else await p.SendAsync("250 ok\r\n"); } }
    }
    private sealed class WarningSink : IProxyWarningEventSink { public ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome o, CancellationToken c) => ValueTask.CompletedTask; public ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent m, CancellationToken c) => ValueTask.CompletedTask; }
    private sealed class WarningAnalyzer : IProxyWarningAnalyzer { public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(ProxyWarningAnalysisInput i, CancellationToken c) => ValueTask.FromResult(new ProxyWarningAnalysisResult { MessageToken = "message", ThreadToken = "thread", Availability = ProxyWarningAvailability.Available, AvailabilityReason = ProxyWarningAvailabilityReason.None, Findings = [] }); }
    private sealed class CapturingWarningSink : IProxyWarningEventSink { public List<ProxyWarningAnalysisOutcome> Outcomes { get; } = []; public List<ProxyDeterministicWarningEvent> Markers { get; } = []; public ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome o, CancellationToken c) { Outcomes.Add(o); return ValueTask.CompletedTask; } public ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent m, CancellationToken c) { Markers.Add(m); return ValueTask.CompletedTask; } }
    private sealed class StaticWarningAnalyzer(IReadOnlyList<ProxyWarningFinding> findings) : IProxyWarningAnalyzer { public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(ProxyWarningAnalysisInput i, CancellationToken c) => ValueTask.FromResult(new ProxyWarningAnalysisResult { MessageToken = "message", ThreadToken = "thread", Availability = ProxyWarningAvailability.Available, AvailabilityReason = ProxyWarningAvailabilityReason.None, Findings = findings }); }
    private sealed class ThrowingElementSink : IProxyElementSink
    {
        public int PointAttempts { get; private set; }
        public ValueTask RecordOutcomeAsync(ProxySegmentationOutcome outcome, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RecordPointAsync(ProxyElementPoint point, CancellationToken cancellationToken)
        {
            PointAttempts++;
            throw new InvalidOperationException("sink failure");
        }
    }
    private sealed class CompleteElementClassifier : IElementClassifier
    {
        public Task<ClassificationOutcome> ClassifyAsync(IReadOnlyList<string> segments, IReadOnlyList<string>? scorers = null, CancellationToken cancellationToken = default)
        {
            var results = ElementScorers.All.Select(scorer => new ElementScorerResult(scorer, ElementScorers.VersionOf(scorer)!,
                segments.Select((_, ordinal) => new SegmentElementScores(ordinal,
                    ElementVocabulary.All.Select(element => new ElementScore(element, ElementAvailability.Available, 0.5, null, ElementNotProducedReason.None)).ToArray())).ToArray())).ToArray();
            return Task.FromResult<ClassificationOutcome>(new ClassificationOutcome.Success(new ElementClassification(
                new EmbeddingMetadata("test-model", null, 3, 3, "test-prefix", "float32", "test-contract"), ElementVocabulary.Version, results)));
        }
    }
    private static ProxyWarningFinding Finding(ProxyWarningMarkerKind kind) => new() { MarkerKind = kind, FindingId = kind.ToString() };
}
