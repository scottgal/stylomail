using System.Text;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Submission;
using StyloMail.AccessProxy.Tests.Support;
using StyloMail.Embeddings;

namespace StyloMail.AccessProxy.Tests;

public sealed class ProxyFetchEligibilityTests
{
    private const string Message = "Subject: valid even inside a partial literal\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nNew body";

    [Theory]
    [InlineData("BODY[]")]
    [InlineData("BODY.PEEK[]")]
    [InlineData("RFC822")]
    [InlineData("body[]")]
    [InlineData("body.peek[]")]
    [InlineData("rfc822")]
    public async Task Whole_message_items_are_eligible(string item)
    {
        var fixture = new Fixture();
        await using var assembler = fixture.Create();
        Feed(assembler, Fetch(item, Message));
        await assembler.DisposeAsync();

        Assert.Equal(1, fixture.WarningAnalyzer.Calls);
        Assert.Equal(1, fixture.Classifier.Calls);
        Assert.Equal(["New body"], fixture.Classifier.Segments);
        var outcome = Assert.Single(fixture.Elements.Outcomes);
        Assert.Equal(ProxyWarningAvailability.Available, outcome.Availability);
        Assert.Equal(1, outcome.SegmentCount);
        Assert.Single(fixture.Elements.Points);
    }

    [Theory]
    [InlineData("BODY[TEXT]")]
    [InlineData("BODY[HEADER]")]
    [InlineData("BODY[HEADER.FIELDS (SUBJECT FROM)]")]
    [InlineData("BODY[1]")]
    [InlineData("BODY[]<0.512>")]
    [InlineData("BODY[]<0>")]
    [InlineData("BODY.PEEK[TEXT]")]
    [InlineData("BODY.PEEK[]<0.512>")]
    [InlineData("RFC822.TEXT")]
    [InlineData("RFC822.HEADER")]
    [InlineData("XBODY[]")]
    [InlineData("X-RFC822")]
    public async Task Partial_or_unknown_items_are_excluded_before_parsing(string item)
    {
        var fixture = new Fixture();
        await using var assembler = fixture.Create();
        // This is valid MIME. Rejection must depend on the FETCH item, not the content parser.
        Feed(assembler, Fetch(item, Message));
        await assembler.DisposeAsync();
        AssertUnsupported(fixture);
    }

    [Theory]
    [InlineData("BODY[]")]
    [InlineData("BODY.PEEK[]")]
    [InlineData("RFC822")]
    public async Task Unreadable_whole_message_keeps_unreadable_reason(string item)
    {
        var fixture = new Fixture();
        await using var assembler = fixture.Create();
        Feed(assembler, Fetch(item, "bare body"));
        await assembler.DisposeAsync();

        Assert.Equal(1, fixture.WarningAnalyzer.Calls);
        Assert.Equal(0, fixture.Classifier.Calls);
        var outcome = Assert.Single(fixture.Elements.Outcomes);
        Assert.Equal(ProxyWarningAvailability.NotProduced, outcome.Availability);
        Assert.Equal(ProxySegmentationAvailabilityReason.UnreadableMessage, outcome.AvailabilityReason);
        Assert.Empty(fixture.Elements.Points);
    }

    [Fact]
    public async Task A_following_whole_literal_in_the_same_fetch_remains_eligible()
    {
        var fixture = new Fixture();
        await using var assembler = fixture.Create();
        var bytes = Encoding.UTF8.GetByteCount(Message);
        Feed(assembler, $"* 1 FETCH (BODY[TEXT] {{{bytes}}}\r\n{Message} BODY[] {{{bytes}}}\r\n{Message})\r\n");
        await assembler.DisposeAsync();

        Assert.Equal(1, fixture.WarningAnalyzer.Calls);
        Assert.Equal(1, fixture.Classifier.Calls);
        Assert.Equal(2, fixture.Elements.Outcomes.Count);
        Assert.Equal(ProxySegmentationAvailabilityReason.UnsupportedContent, fixture.Elements.Outcomes[0].AvailabilityReason);
        Assert.Equal(0, fixture.Elements.Outcomes[0].SegmentCount);
        Assert.Equal(ProxyWarningAvailability.Available, fixture.Elements.Outcomes[1].Availability);
        Assert.Single(fixture.Elements.Points);
    }

    [Fact]
    public async Task Empty_partial_literal_has_one_unsupported_outcome()
    {
        var fixture = new Fixture();
        await using var assembler = fixture.Create();
        Feed(assembler, Fetch("BODY[TEXT]", ""));
        await assembler.DisposeAsync();
        AssertUnsupported(fixture);
    }

    [Fact]
    public async Task A_partial_fetch_is_relayed_byte_for_byte_while_analysis_is_excluded()
    {
        var fixture = new Fixture();
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();
        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(retrievalAssemblerFactory: fixture.Factory).RunAsync(client, CancellationToken.None);
        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        Assert.StartsWith("a1 OK", await client.ReadLineAsync(), StringComparison.Ordinal);
        var literal = Encoding.UTF8.GetBytes(Fetch("BODY[]<0>", Message));
        await harness.Transport.Channel!.SendRawAsync(literal);
        var received = new byte[literal.Length];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.PeerInput.ReadExactlyAsync(received, deadline.Token);
        Assert.Equal(literal, received);
        await client.ClosePeerWriteAsync();
        Assert.Equal(SessionOutcome.Relayed, await run.WaitAsync(deadline.Token));
        AssertUnsupported(fixture);
    }

    private static void AssertUnsupported(Fixture fixture)
    {
        Assert.Equal(0, fixture.WarningAnalyzer.Calls);
        Assert.Equal(0, fixture.Classifier.Calls);
        var outcome = Assert.Single(fixture.Elements.Outcomes);
        Assert.Equal(ProxyWarningAvailability.NotProduced, outcome.Availability);
        Assert.Equal(ProxySegmentationAvailabilityReason.UnsupportedContent, outcome.AvailabilityReason);
        Assert.Equal(0, outcome.SegmentCount);
        Assert.Empty(outcome.AnalysedOrdinals);
        Assert.Empty(fixture.Elements.Points);
        var warning = Assert.Single(fixture.Warnings.Outcomes);
        Assert.Equal(ProxyWarningAvailability.NotProduced, warning.Availability);
        Assert.Equal(ProxyWarningAvailabilityReason.UnsupportedContent, warning.AvailabilityReason);
        Assert.Empty(fixture.Warnings.Markers);
        Assert.Equal(outcome.MessageToken, warning.MessageToken);
        Assert.Equal(outcome.ThreadToken, warning.ThreadToken);
    }

    private static string Fetch(string item, string message)
        => $"* 1 FETCH ({item} {{{Encoding.UTF8.GetByteCount(message)}}}\r\n{message})\r\n";

    private static void Feed(IProxyRetrievalWarningAssembler assembler, string wire)
    {
        var bytes = Encoding.UTF8.GetBytes(wire);
        // Small chunks make item specifiers and literal bytes cross observation boundaries.
        for (var offset = 0; offset < bytes.Length; offset += 7)
            assembler.Observe(bytes.AsSpan(offset, Math.Min(7, bytes.Length - offset)));
    }

    private sealed class Fixture
    {
        public WarningAnalyzer WarningAnalyzer { get; } = new();
        public Classifier Classifier { get; } = new();
        public ElementSink Elements { get; } = new();
        public WarningSink Warnings { get; } = new();
        public ProxyRetrievalWarningAssemblerFactory Factory => new(Warnings, WarningAnalyzer, TimeProvider.System,
            elementOrchestrator: new ProxyElementOrchestrator(Classifier, Elements));
        public IProxyRetrievalWarningAssembler Create() => Factory.Create("tenant", "account");
    }

    private sealed class WarningAnalyzer : IProxyWarningAnalyzer
    {
        public int Calls { get; private set; }
        public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(ProxyWarningAnalysisInput input, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new ProxyWarningAnalysisResult
            {
                MessageToken = "message", ThreadToken = "thread", Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = ProxyWarningAvailabilityReason.None, Findings = [],
            });
        }
    }

    private sealed class Classifier : IElementClassifier
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> Segments { get; private set; } = [];
        public Task<ClassificationOutcome> ClassifyAsync(IReadOnlyList<string> segments,
            IReadOnlyList<string>? scorers = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Segments = segments.ToArray();
            return Task.FromResult<ClassificationOutcome>(new ClassificationOutcome.Success(new ElementClassification(
                new EmbeddingMetadata("test-model", null, 3, 3, "test-prefix", "float32", "test-contract"), ElementVocabulary.Version,
                [new ElementScorerResult(ElementScorers.PrototypeKnn, ElementScorers.PrototypeKnnVersion,
                    segments.Select((_, ordinal) => new SegmentElementScores(ordinal,
                        [new ElementScore(ElementVocabulary.UrgencyPressure, ElementAvailability.Available,
                            0.5, null, ElementNotProducedReason.None)])).ToArray())])));
        }
    }

    private sealed class ElementSink : IProxyElementSink
    {
        public List<ProxySegmentationOutcome> Outcomes { get; } = [];
        public List<ProxyElementPoint> Points { get; } = [];
        public ValueTask RecordOutcomeAsync(ProxySegmentationOutcome outcome, CancellationToken cancellationToken)
        { Outcomes.Add(outcome); return ValueTask.CompletedTask; }
        public ValueTask RecordPointAsync(ProxyElementPoint point, CancellationToken cancellationToken)
        { Points.Add(point); return ValueTask.CompletedTask; }
    }

    private sealed class WarningSink : IProxyWarningEventSink
    {
        public List<ProxyWarningAnalysisOutcome> Outcomes { get; } = [];
        public List<ProxyDeterministicWarningEvent> Markers { get; } = [];
        public ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome outcome, CancellationToken cancellationToken)
        { Outcomes.Add(outcome); return ValueTask.CompletedTask; }
        public ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent marker, CancellationToken cancellationToken)
        { Markers.Add(marker); return ValueTask.CompletedTask; }
    }
}
