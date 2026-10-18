using System.Text;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Embeddings;

namespace StyloMail.AccessProxy.Submission;

/// <summary>Bounded, local-only segment/classify emission. Callers keep this optional.</summary>
public sealed class ProxyElementOrchestrator
{
    private readonly IElementClassifier _classifier;
    private readonly IProxyElementSink _sink;

    public ProxyElementOrchestrator(IElementClassifier classifier, IProxyElementSink sink)
        => (_classifier, _sink) = (classifier ?? throw new ArgumentNullException(nameof(classifier)), sink ?? throw new ArgumentNullException(nameof(sink)));

    public async ValueTask EmitAsync(ProxyWarningAnalysisInput identity, ReadOnlyMemory<byte> message, string messageToken, string threadToken, MessageTrafficKind kind, CancellationToken ct)
    {
        var segments = Segment(message, 32, 6000, out var truncated);
        try
        {
            var baseOutcome = new ProxySegmentationOutcome { TenantId = identity.TenantId, AccountId = identity.AccountId, TrafficKind = kind, MessageToken = messageToken, ThreadToken = threadToken, ObservedAt = identity.ObservedAt, Availability = ProxyWarningAvailability.Available, AvailabilityReason = ProxySegmentationAvailabilityReason.None, SegmentCount = segments.Count, Truncated = truncated, AppliedMaxSegments = 32, AppliedMaxSegmentBytes = 6000, AnalysedOrdinals = Enumerable.Range(0, segments.Count).ToArray(), AnySegmentDropped = truncated };
            var classified = await _classifier.ClassifyAsync(segments, cancellationToken: ct).ConfigureAwait(false);
            if (classified is ClassificationOutcome.Unavailable unavailable)
            {
                await _sink.RecordOutcomeAsync(baseOutcome with { Availability = ProxyWarningAvailability.NotProduced, AvailabilityReason = Map(unavailable.Reason) }, ct).ConfigureAwait(false);
                return;
            }
            var result = ((ClassificationOutcome.Success)classified).Classification;
            if (result.Metadata is null)
            {
                await _sink.RecordOutcomeAsync(baseOutcome with { Availability = ProxyWarningAvailability.NotProduced, AvailabilityReason = ProxySegmentationAvailabilityReason.ContractViolation }, ct).ConfigureAwait(false);
                return;
            }
            if (result.Scorers.Any(s => s.Segments.Count != segments.Count)) throw new InvalidOperationException("Classifier returned incomplete segment coverage.");
            var points = new List<ProxyElementPoint>();
            foreach (var scorer in result.Scorers) foreach (var segment in scorer.Segments) foreach (var score in segment.Elements)
                points.Add(new ProxyElementPoint { TenantId=identity.TenantId, AccountId=identity.AccountId, TrafficKind=kind, MessageToken=messageToken, ThreadToken=threadToken, Ordinal=segment.Ordinal, Element=score.Element, Score=score.Score, Scorer=scorer.Scorer, ScorerVersion=scorer.ScorerVersion, NearestExemplarId=score.NearestExemplarId, Availability=score.Availability == ElementAvailability.Available ? ProxyWarningAvailability.Available : ProxyWarningAvailability.NotProduced, AvailabilityReason=ElementReasons.ToWireName(score.Reason), ModelTag=result.Metadata.ModelTag, PrefixVersion=result.Metadata.PrefixVersion, Dimension=result.Metadata.Dimension, VocabularyVersion=result.VocabularyVersion, ObservedAt=identity.ObservedAt });
            await _sink.RecordBatchAsync(baseOutcome, points, ct).ConfigureAwait(false);
        }
        finally { foreach (var segment in segments) { /* strings are ephemeral and not retained beyond this call */ } }
    }

    private static ProxySegmentationAvailabilityReason Map(ClassificationUnavailableReason reason) => reason switch
    {
        ClassificationUnavailableReason.NoAnalyserConfigured => ProxySegmentationAvailabilityReason.NoAnalyserConfigured,
        ClassificationUnavailableReason.ServiceUnavailable => ProxySegmentationAvailabilityReason.ServiceUnavailable,
        ClassificationUnavailableReason.TimedOut => ProxySegmentationAvailabilityReason.TimedOut,
        ClassificationUnavailableReason.Unauthorized => ProxySegmentationAvailabilityReason.Unauthorized,
        ClassificationUnavailableReason.ContractViolation => ProxySegmentationAvailabilityReason.ContractViolation,
        ClassificationUnavailableReason.UnsupportedModel => ProxySegmentationAvailabilityReason.UnsupportedModel,
        ClassificationUnavailableReason.InvalidRequest => ProxySegmentationAvailabilityReason.InvalidRequest,
        _ => ProxySegmentationAvailabilityReason.ContractViolation,
    };

    private static List<string> Segment(ReadOnlyMemory<byte> message, int maxSegments, int maxBytes, out bool truncated)
    {
        var text = Encoding.UTF8.GetString(message.Span); var result = new List<string>(); truncated = false;
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { if (result.Count == maxSegments) { truncated=true; break; } var bytes=Encoding.UTF8.GetBytes(paragraph); if(bytes.Length>maxBytes) { result.Add(Encoding.UTF8.GetString(bytes,0,maxBytes)); truncated=true; } else result.Add(paragraph); Array.Clear(bytes); }
        return result;
    }
}
