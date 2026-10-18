using System.Text;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Submission;
using StyloMail.AccessProxy.Tests.Support;
using StyloMail.Embeddings;

namespace StyloMail.AccessProxy.Tests;

/// <summary>
/// Constraint 5: assessment of retrieved messages is optional and non-blocking.
/// </summary>
/// <remarks>
/// The property that matters is not "assessment works", it is that <em>a client fetching mail is
/// never held hostage by the semantic provider</em>. These tests are about the absence of coupling,
/// which is why several of them assert that something did not happen.
/// </remarks>
public sealed class RetrievalObserverTests
{
    [Fact]
    public async Task WithNoObserverConfigured_TheSessionRelaysNormally()
    {
        // The default deployment. Assessment is off, and the retrieval path is unaffected by that.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(observer: null).RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        Assert.StartsWith("a1 OK", await client.ReadLineAsync(), StringComparison.Ordinal);

        byte[] message = [(byte)'*', (byte)' ', (byte)'1', (byte)' ', (byte)'O', (byte)'K', (byte)'\r', (byte)'\n'];
        await harness.Transport.Channel!.SendRawAsync(message);

        var received = new byte[message.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);

        Assert.Equal(message, received);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task TheNullObserver_ObservesNothing()
    {
        // Named rather than null so a deployment can tell "assessment is off" from "never wired".
        var observer = NullRetrievalObserver.Instance;

        observer.Observe([1, 2, 3, 4]);

        await Task.CompletedTask;
        Assert.Same(NullRetrievalObserver.Instance, observer);
    }

    [Fact]
    public async Task ConfiguredObserver_SeesExactlyTheBytesTheClientReceives()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var observer = new RecordingRetrievalObserver();
        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(observer).RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();

        byte[] message = [(byte)'*', (byte)' ', (byte)'9', (byte)' ', (byte)'F', (byte)'E', (byte)'T', (byte)'C', (byte)'H', (byte)'\r', (byte)'\n'];
        await harness.Transport.Channel!.SendRawAsync(message);

        var received = new byte[message.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);

        // Give the pump a moment to hand the chunk to the observer, which happens before the write.
        var observed = await WaitForObservationAsync(observer, message.Length);

        Assert.Equal(message, observed);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task ObserverDoesNotSeeClientToBackendBytes()
    {
        // The tap is on retrieval. A client's own commands are not "retrieved messages", and a tap
        // that saw both directions would be handing a future assessment hook the user's credentials
        // along with their mail.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var observer = new RecordingRetrievalObserver();
        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(observer).RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();

        // A command the client sends after authentication, which the relay forwards upstream.
        await client.SendAsync("a2 SELECT INBOX\r\n");

        await Task.Delay(200);

        Assert.DoesNotContain(
            ProxyHarness.ClientPassword,
            System.Text.Encoding.UTF8.GetString(observer.Observed),
            StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task AnObserverThatThrows_DoesNotTakeTheSessionDown()
    {
        // The documented contract says an observer must not throw. A documented contract is not an
        // enforced one, and the failure mode if it were merely documented is a user's mailbox dying
        // because a semantic classifier had a bad day.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var observer = new ThrowingRetrievalObserver();
        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(observer).RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();

        byte[] message = [(byte)'*', (byte)' ', (byte)'1', (byte)' ', (byte)'O', (byte)'K', (byte)'\r', (byte)'\n'];
        await harness.Transport.Channel!.SendRawAsync(message);

        // The bytes still arrive, despite the observer failing on every chunk.
        var received = new byte[message.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);

        Assert.Equal(message, received);
        Assert.True(observer.CallCount > 0, "The observer was never offered anything, so this proved nothing.");

        await client.ClosePeerWriteAsync();
        Assert.Equal(SessionOutcome.Relayed, await run);
    }

    [Fact]
    public void TheObserverContractIsSynchronous_SoTheRelayStructurallyCannotAwaitIt()
    {
        // "Non-blocking" is enforced by the signature rather than by good intentions: Observe returns
        // void, so there is no Task for the relay to await and no way for it to wait on a provider.
        // If this ever becomes Task-returning, the relay gains the ability to block a user's mailbox
        // on a semantic provider, which is the exact coupling the brief forbids. The assertion is a
        // tripwire for that change, not a style preference.
        var method = typeof(IRetrievalObserver).GetMethod(nameof(IRetrievalObserver.Observe));

        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
    }

    [Fact]
    public async Task Fetch_literal_assembler_records_a_clean_available_outcome()
    {
        var sink = new CapturingWarningSink();
        var factory = new ProxyRetrievalWarningAssemblerFactory(sink, new StaticWarningAnalyzer(), TimeProvider.System);
        await using var assembler = factory.Create("tenant", "account");

        FeedFetchLiteral(assembler, "Subject: clean\r\n\r\nbody", splitAt: 11);

        await assembler.DisposeAsync();
        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(MessageTrafficKind.Retrieval, outcome.TrafficKind);
        Assert.Equal(ProxyWarningAvailability.Available, outcome.Availability);
        Assert.Equal(ProxyWarningAvailabilityReason.None, outcome.AvailabilityReason);
        Assert.Equal(0, outcome.MarkerCount);
        Assert.Empty(sink.Markers);
    }

    [Fact]
    public async Task Fetch_literal_assembler_persists_markers_for_an_available_marked_message()
    {
        var sink = new CapturingWarningSink();
        var finding = new ProxyWarningFinding { MarkerKind = ProxyWarningMarkerKind.LinkDomainMismatch, FindingId = "marked-link" };
        var factory = new ProxyRetrievalWarningAssemblerFactory(sink,
            new StaticWarningAnalyzer(findings: [finding]), TimeProvider.System);
        await using var assembler = factory.Create("tenant", "account");

        FeedFetchLiteral(assembler, "Subject: marked\r\n\r\nbody", splitAt: 9);

        await assembler.DisposeAsync();
        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(ProxyWarningAvailability.Available, outcome.Availability);
        Assert.Equal(1, outcome.MarkerCount);
        var marker = Assert.Single(sink.Markers);
        Assert.Equal(finding.MarkerKind, marker.MarkerKind);
        Assert.Equal(finding.FindingId, marker.FindingId);
    }

    [Fact]
    public async Task Session_factory_analyzes_a_complete_fetch_literal_after_authentication()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();
        var sink = new CapturingWarningSink();
        var factory = new ProxyRetrievalWarningAssemblerFactory(sink, new StaticWarningAnalyzer(), harness.Clock);
        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(retrievalAssemblerFactory: factory).RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();
        var literal = Encoding.ASCII.GetBytes("* 1 FETCH (BODY[] {4}\r\ntest\r\n)\r\n");
        await harness.Transport.Channel!.SendRawAsync(literal);
        var received = new byte[literal.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);
        Assert.Equal(literal, received);

        await client.ClosePeerWriteAsync();
        Assert.Equal(SessionOutcome.Relayed, await run);
        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(MessageTrafficKind.Retrieval, outcome.TrafficKind);
        Assert.Equal(ProxyWarningAvailability.Available, outcome.Availability);
    }

    [Fact]
    public async Task Complete_fetch_literal_emits_retrieval_element_points_after_its_warning_outcome()
    {
        var warningSink = new CapturingWarningSink();
        var elementSink = new CapturingElementSink();
        var factory = new ProxyRetrievalWarningAssemblerFactory(warningSink, new StaticWarningAnalyzer(), TimeProvider.System,
            elementOrchestrator: new ProxyElementOrchestrator(new CompleteElementClassifier(), elementSink));
        await using var assembler = factory.Create("tenant", "account");

        FeedFetchLiteral(assembler, "Subject: clean\r\n\r\nbody", splitAt: 7);

        await assembler.DisposeAsync();
        Assert.Single(warningSink.Outcomes);
        Assert.Single(elementSink.Outcomes);
        Assert.NotEmpty(elementSink.Points);
        Assert.All(elementSink.Points, point => Assert.Equal(MessageTrafficKind.Retrieval, point.TrafficKind));
    }

    [Fact]
    public async Task Oversized_fetch_literal_records_unreadable_and_never_available()
    {
        var sink = new CapturingWarningSink();
        var elementSink = new CapturingElementSink();
        var factory = new ProxyRetrievalWarningAssemblerFactory(sink, new StaticWarningAnalyzer(), TimeProvider.System,
            new ProxyRetrievalWarningAssemblerOptions { MaximumMessageBytes = 4, MaximumSessionBufferedBytes = 128 },
            new ProxyElementOrchestrator(new CompleteElementClassifier(), elementSink));
        await using var assembler = factory.Create("tenant", "account");

        FeedFetchLiteral(assembler, "12345", splitAt: 2);

        await assembler.DisposeAsync();
        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(ProxyWarningAvailability.NotProduced, outcome.Availability);
        Assert.Equal(ProxyWarningAvailabilityReason.UnreadableMessage, outcome.AvailabilityReason);
        Assert.Empty(sink.Markers);
        Assert.Empty(elementSink.Points);
    }

    [Fact]
    public async Task Far_oversized_fetch_literal_never_reaches_the_analyzer_as_a_large_copy()
    {
        const int messageCap = 64;
        var sink = new CapturingWarningSink();
        var analyzer = new RecordingInputWarningAnalyzer();
        var factory = new ProxyRetrievalWarningAssemblerFactory(sink, analyzer, TimeProvider.System,
            new ProxyRetrievalWarningAssemblerOptions { MaximumMessageBytes = messageCap, MaximumSessionBufferedBytes = 16 * 1024 });
        await using var assembler = factory.Create("tenant", "account");
        var body = new string('x', messageCap * 128);
        var header = Encoding.ASCII.GetBytes($"* 1 FETCH (BODY[] {{{body.Length}}}\r\n");

        assembler.Observe(header);
        await Task.Yield();
        for (var offset = 0; offset < body.Length; offset += 32)
            assembler.Observe(Encoding.ASCII.GetBytes(body.Substring(offset, Math.Min(32, body.Length - offset))));

        await assembler.DisposeAsync();
        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(ProxyWarningAvailability.NotProduced, outcome.Availability);
        Assert.Equal(ProxyWarningAvailabilityReason.UnreadableMessage, outcome.AvailabilityReason);
        Assert.True(analyzer.LargestInputBytes <= messageCap, "The assembler handed the analyzer a payload larger than its configured cap.");
    }

    [Fact]
    public async Task Truncated_fetch_literal_never_records_an_available_outcome()
    {
        var sink = new CapturingWarningSink();
        var factory = new ProxyRetrievalWarningAssemblerFactory(sink, new StaticWarningAnalyzer(), TimeProvider.System);
        await using var assembler = factory.Create("tenant", "account");

        assembler.Observe("* 1 FETCH (BODY[] {20}\r\npartial"u8);

        await assembler.DisposeAsync();
        Assert.DoesNotContain(sink.Outcomes, outcome => outcome.Availability == ProxyWarningAvailability.Available);
        Assert.Empty(sink.Outcomes);
    }

    [Fact]
    public async Task Configured_unavailable_analyzer_records_no_analyser_configured()
    {
        var sink = new CapturingWarningSink();
        var factory = new ProxyRetrievalWarningAssemblerFactory(sink,
            new StaticWarningAnalyzer(ProxyWarningAvailability.NotProduced, ProxyWarningAvailabilityReason.NoAnalyserConfigured),
            TimeProvider.System);
        await using var assembler = factory.Create("tenant", "account");

        FeedFetchLiteral(assembler, "body", splitAt: 3);

        await assembler.DisposeAsync();
        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(ProxyWarningAvailability.NotProduced, outcome.Availability);
        Assert.Equal(ProxyWarningAvailabilityReason.NoAnalyserConfigured, outcome.AvailabilityReason);
        Assert.Empty(sink.Markers);
    }

    [Fact]
    public async Task No_assembler_factory_means_retrieval_analysis_is_off()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();
        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(retrievalAssemblerFactory: null).RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();
        var literal = Encoding.ASCII.GetBytes("* 1 FETCH (BODY[] {4}\r\ntest\r\n)\r\n");
        await harness.Transport.Channel!.SendRawAsync(literal);
        var received = new byte[literal.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);
        Assert.Equal(literal, received);

        await client.ClosePeerWriteAsync();
        Assert.Equal(SessionOutcome.Relayed, await run);
    }

    [Fact]
    public async Task Throwing_retrieval_element_sink_cannot_drop_the_fetch_or_session()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();
        var warningSink = new CapturingWarningSink();
        var elementSink = new ThrowingElementSink();
        var factory = new ProxyRetrievalWarningAssemblerFactory(warningSink, new StaticWarningAnalyzer(), harness.Clock,
            elementOrchestrator: new ProxyElementOrchestrator(new CompleteElementClassifier(), elementSink));
        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(retrievalAssemblerFactory: factory).RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();
        var literal = Encoding.ASCII.GetBytes("* 1 FETCH (BODY[] {4}\r\ntest\r\n)\r\n");
        await harness.Transport.Channel!.SendRawAsync(literal);
        var received = new byte[literal.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);
        Assert.Equal(literal, received);

        await client.ClosePeerWriteAsync();
        Assert.Equal(SessionOutcome.Relayed, await run);
        Assert.True(elementSink.PointAttempts > 0, "The element sink was never exercised.");
    }

    private static async Task<byte[]> WaitForObservationAsync(RecordingRetrievalObserver observer, int expectedLength)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var seen = observer.Observed;
            if (seen.Length >= expectedLength)
            {
                return seen;
            }

            await Task.Delay(20);
        }

        return observer.Observed;
    }

    private static void FeedFetchLiteral(IProxyRetrievalWarningAssembler assembler, string message, int splitAt)
    {
        var payload = Encoding.ASCII.GetBytes($"* 1 FETCH (BODY[] {{{Encoding.ASCII.GetByteCount(message)}}}\r\n{message}\r\n)\r\n");
        assembler.Observe(payload.AsSpan(0, splitAt));
        assembler.Observe(payload.AsSpan(splitAt));
        Array.Clear(payload);
    }

    private sealed class CapturingWarningSink : IProxyWarningEventSink
    {
        public List<ProxyWarningAnalysisOutcome> Outcomes { get; } = [];
        public List<ProxyDeterministicWarningEvent> Markers { get; } = [];
        public ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome outcome, CancellationToken cancellationToken) { Outcomes.Add(outcome); return ValueTask.CompletedTask; }
        public ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent marker, CancellationToken cancellationToken) { Markers.Add(marker); return ValueTask.CompletedTask; }
    }

    private sealed class StaticWarningAnalyzer(
        ProxyWarningAvailability availability = ProxyWarningAvailability.Available,
        ProxyWarningAvailabilityReason reason = ProxyWarningAvailabilityReason.None,
        IReadOnlyList<ProxyWarningFinding>? findings = null) : IProxyWarningAnalyzer
    {
        public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(ProxyWarningAnalysisInput input, CancellationToken cancellationToken) => ValueTask.FromResult(new ProxyWarningAnalysisResult
        {
            MessageToken = "message", ThreadToken = "thread", Availability = availability,
            AvailabilityReason = reason, Findings = findings ?? [],
        });
    }

    private sealed class RecordingInputWarningAnalyzer : IProxyWarningAnalyzer
    {
        public int LargestInputBytes { get; private set; }
        public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(ProxyWarningAnalysisInput input, CancellationToken cancellationToken)
        {
            LargestInputBytes = Math.Max(LargestInputBytes, input.Message.Length);
            return ValueTask.FromResult(new ProxyWarningAnalysisResult
            {
                MessageToken = "message", ThreadToken = "thread", Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = ProxyWarningAvailabilityReason.None, Findings = [],
            });
        }
    }

    private sealed class CapturingElementSink : IProxyElementSink
    {
        public List<ProxySegmentationOutcome> Outcomes { get; } = [];
        public List<ProxyElementPoint> Points { get; } = [];
        public ValueTask RecordOutcomeAsync(ProxySegmentationOutcome outcome, CancellationToken cancellationToken) { Outcomes.Add(outcome); return ValueTask.CompletedTask; }
        public ValueTask RecordPointAsync(ProxyElementPoint point, CancellationToken cancellationToken) { Points.Add(point); return ValueTask.CompletedTask; }
    }

    private sealed class ThrowingElementSink : IProxyElementSink
    {
        public int PointAttempts { get; private set; }
        public ValueTask RecordOutcomeAsync(ProxySegmentationOutcome outcome, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RecordPointAsync(ProxyElementPoint point, CancellationToken cancellationToken)
        {
            PointAttempts++;
            throw new InvalidOperationException("element store failed");
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
}
