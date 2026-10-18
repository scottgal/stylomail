using System.Text;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Submission;
using StyloMail.Embeddings;

namespace StyloMail.AccessProxy.Tests;

public sealed class ProxyElementSegmentationTests
{
    private const string Headers = "From: sender@example.test\r\nSubject: HEADER-SENTINEL\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n";

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n")]
    public async Task Only_body_paragraphs_are_classified_for_each_line_ending(string newline)
    {
        var (classifier, sink) = await Emit(Encoding.UTF8.GetBytes(Headers +
            $"First paragraph{newline}continues here{newline} \t{newline}Second paragraph"));

        Assert.Equal(["First paragraph\ncontinues here", "Second paragraph"], classifier.Segments);
        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(2, outcome.SegmentCount);
        Assert.False(outcome.Truncated);
        Assert.False(outcome.AnySegmentDropped);
        Assert.Equal([0, 1], outcome.AnalysedOrdinals);
    }

    [Fact]
    public async Task Oversized_paragraph_preserves_all_scalars_in_bounded_utf8_chunks()
    {
        // The old raw-wire byte cut bisects the emoji; the body itself also exceeds 6000 bytes.
        var body = new string('a', 5999 - Encoding.UTF8.GetByteCount(Headers)) + "😀" + new string('b', 500);
        var (classifier, sink) = await Emit(Encoding.UTF8.GetBytes(Headers + body));

        Assert.All(classifier.Segments, segment => Assert.DoesNotContain("\uFFFD", segment));
        Assert.Equal(body, string.Concat(classifier.Segments));
        Assert.Equal(2, classifier.Segments.Count);
        var strictUtf8 = new UTF8Encoding(false, true);
        Assert.All(classifier.Segments, segment => Assert.InRange(strictUtf8.GetByteCount(segment), 1, 6000));
        var outcome = Assert.Single(sink.Outcomes);
        Assert.True(outcome.Truncated); // Splitting is visible even though no content was dropped.
        Assert.False(outcome.AnySegmentDropped);
    }

    [Fact]
    public async Task Segment_cap_keeps_whole_paragraphs_and_records_dropped_tail()
    {
        var paragraphs = Enumerable.Range(0, 35).Select(i => $"Paragraph {i:D2}").ToArray();
        var (classifier, sink) = await Emit(Encoding.UTF8.GetBytes(Headers + string.Join("\r\n\r\n", paragraphs)));

        Assert.Equal(paragraphs.Take(32), classifier.Segments);
        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(32, outcome.SegmentCount);
        Assert.Equal(Enumerable.Range(0, 32), outcome.AnalysedOrdinals);
        Assert.True(outcome.Truncated);
        Assert.True(outcome.AnySegmentDropped);
    }

    [Theory]
    [InlineData("€")]
    [InlineData("😀")]
    public async Task Body_byte_cap_moves_the_entire_boundary_scalar_to_the_next_chunk(string scalar)
    {
        var prefix = new string('a', 5999);
        var body = prefix + scalar + "tail";
        var (classifier, sink) = await Emit(Encoding.UTF8.GetBytes(Headers + body));

        Assert.Equal([prefix, scalar + "tail"], classifier.Segments);
        Assert.All(classifier.Segments, segment => Assert.InRange(new UTF8Encoding(false, true).GetByteCount(segment), 1, 6000));
        var outcome = Assert.Single(sink.Outcomes);
        Assert.True(outcome.Truncated);
        Assert.False(outcome.AnySegmentDropped);
    }

    [Fact]
    public async Task Parser_rejection_is_not_produced_without_a_classifier_call()
    {
        var (classifier, sink) = await Emit("not a MIME message"u8.ToArray());

        Assert.Equal(0, classifier.Calls);
        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(ProxyWarningAvailability.NotProduced, outcome.Availability);
        Assert.Equal(ProxySegmentationAvailabilityReason.UnreadableMessage, outcome.AvailabilityReason);
        Assert.Equal(0, outcome.SegmentCount);
        Assert.Empty(outcome.AnalysedOrdinals);
        Assert.Empty(sink.Points);
    }

    [Fact]
    public async Task Transfer_decoding_and_quoted_history_use_the_mime_body_contract()
    {
        var body = "New paragraph\r\n\r\nAnother paragraph\r\n\r\n> QUOTED-SENTINEL";
        var message = Headers.Replace("\r\n\r\n", "\r\nContent-Transfer-Encoding: base64\r\n\r\n")
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        var (classifier, sink) = await Emit(Encoding.UTF8.GetBytes(message));

        Assert.Equal(["New paragraph", "Another paragraph"], classifier.Segments);
        Assert.False(Assert.Single(sink.Outcomes).Truncated);
    }

    [Fact]
    public async Task Exactly_the_segment_cap_does_not_report_truncation()
    {
        var paragraphs = Enumerable.Range(0, 32).Select(i => $"Paragraph {i:D2}").ToArray();
        var (classifier, sink) = await Emit(Encoding.UTF8.GetBytes(Headers + string.Join("\n\n", paragraphs) + "\n\n"));

        Assert.Equal(paragraphs, classifier.Segments);
        Assert.False(Assert.Single(sink.Outcomes).Truncated);
    }

    [Fact]
    public async Task Quote_only_body_has_no_new_segments_or_classifier_call()
    {
        var (classifier, sink) = await Emit(Encoding.UTF8.GetBytes(Headers + "> Previous message"));

        Assert.Equal(0, classifier.Calls);
        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(ProxyWarningAvailability.NotProduced, outcome.Availability);
        Assert.Equal(ProxySegmentationAvailabilityReason.UnsupportedContent, outcome.AvailabilityReason);
        Assert.Equal(0, outcome.SegmentCount);
    }

    private static async Task<(RecordingClassifier Classifier, RecordingSink Sink)> Emit(byte[] message)
    {
        var classifier = new RecordingClassifier();
        var sink = new RecordingSink();
        await new ProxyElementOrchestrator(classifier, sink).EmitAsync(new ProxyWarningAnalysisInput
        {
            Message = message, TenantId = "tenant", AccountId = "account", ObservedAt = DateTimeOffset.UnixEpoch,
        }, message, "opaque-message", "opaque-thread", MessageTrafficKind.Retrieval, CancellationToken.None);
        return (classifier, sink);
    }

    private sealed class RecordingClassifier : IElementClassifier
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> Segments { get; private set; } = [];
        public Task<ClassificationOutcome> ClassifyAsync(IReadOnlyList<string> segments,
            IReadOnlyList<string>? scorers = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Segments = segments.ToArray();
            var results = ElementScorers.All.Select(scorer => new ElementScorerResult(scorer, ElementScorers.VersionOf(scorer)!,
                segments.Select((_, ordinal) => new SegmentElementScores(ordinal,
                    ElementVocabulary.All.Select(element => new ElementScore(element, ElementAvailability.Available,
                        0.5, null, ElementNotProducedReason.None)).ToArray())).ToArray())).ToArray();
            return Task.FromResult<ClassificationOutcome>(new ClassificationOutcome.Success(new ElementClassification(
                new EmbeddingMetadata("test-model", null, 3, 3, "test-prefix", "float32", "test-contract"),
                ElementVocabulary.Version, results)));
        }
    }

    private sealed class RecordingSink : IProxyElementSink
    {
        public List<ProxySegmentationOutcome> Outcomes { get; } = [];
        public List<ProxyElementPoint> Points { get; } = [];
        public ValueTask RecordOutcomeAsync(ProxySegmentationOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            return ValueTask.CompletedTask;
        }
        public ValueTask RecordPointAsync(ProxyElementPoint point, CancellationToken cancellationToken)
        {
            Points.Add(point);
            return ValueTask.CompletedTask;
        }
    }
}
