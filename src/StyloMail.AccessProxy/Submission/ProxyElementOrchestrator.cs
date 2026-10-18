using System.Buffers;
using System.Text;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Core;
using StyloMail.Embeddings;
using StyloMail.Mime;

namespace StyloMail.AccessProxy.Submission;

/// <summary>Bounded, local-only segment/classify emission. Callers keep this optional.</summary>
public sealed class ProxyElementOrchestrator
{
    private const int MaxSegments = 32;
    private const int MaxSegmentBytes = 6000;
    private readonly IElementClassifier _classifier;
    private readonly IProxyElementSink _sink;
    private readonly BoundedMimeMessageAnalyzer _mime = new();

    public ProxyElementOrchestrator(IElementClassifier classifier, IProxyElementSink sink)
        => (_classifier, _sink) = (classifier ?? throw new ArgumentNullException(nameof(classifier)), sink ?? throw new ArgumentNullException(nameof(sink)));

    public async ValueTask EmitAsync(ProxyWarningAnalysisInput identity, ReadOnlyMemory<byte> message, string messageToken, string threadToken, MessageTrafficKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var baseOutcome = new ProxySegmentationOutcome
        {
            TenantId = identity.TenantId, AccountId = identity.AccountId, TrafficKind = kind,
            MessageToken = messageToken, ThreadToken = threadToken, ObservedAt = identity.ObservedAt,
            Availability = ProxyWarningAvailability.NotProduced,
            AvailabilityReason = ProxySegmentationAvailabilityReason.UnsupportedContent,
            SegmentCount = 0, Truncated = false, AppliedMaxSegments = MaxSegments,
            AppliedMaxSegmentBytes = MaxSegmentBytes, AnalysedOrdinals = [], AnySegmentDropped = false,
        };
        var parsed = _mime.Analyze(new MimeAnalysisRequest
        {
            RawMessage = message,
            // Addresses are unavailable at this seam. Do not infer trusted identity from headers;
            // only the parser's new-body view is consumed, not its envelope evidence.
            Envelope = new MailEnvelope
            {
                InternalMessageId = messageToken, TenantId = identity.TenantId,
                TrustedPrincipalId = identity.AccountId,
                Direction = kind == MessageTrafficKind.Submission ? MailDirection.Outbound : MailDirection.Inbound,
                MailFrom = string.Empty, RcptTo = [], ReceivedAt = identity.ObservedAt,
                MimeDigest = string.Empty, PayloadReference = string.Empty,
            },
        });
        if (!parsed.IsAnalysable)
        {
            await _sink.RecordOutcomeAsync(baseOutcome with
            {
                AvailabilityReason = parsed.Disposition == MimeParseDisposition.Malformed
                    ? ProxySegmentationAvailabilityReason.UnreadableMessage
                    : ProxySegmentationAvailabilityReason.UnsupportedContent,
            }, ct).ConfigureAwait(false);
            return;
        }

        var segments = Segment(parsed.Message!.BodyText, out var truncated, out var dropped);
        if (segments is null || segments.Count == 0)
        {
            await _sink.RecordOutcomeAsync(baseOutcome with
            {
                AvailabilityReason = segments is null ? ProxySegmentationAvailabilityReason.UnreadableMessage
                    : ProxySegmentationAvailabilityReason.UnsupportedContent,
                Truncated = parsed.Coverage.Truncated, AnySegmentDropped = parsed.Coverage.Truncated,
            }, ct).ConfigureAwait(false);
            return;
        }
        baseOutcome = baseOutcome with
        {
            Availability = ProxyWarningAvailability.Available, AvailabilityReason = ProxySegmentationAvailabilityReason.None,
            SegmentCount = segments.Count, AnalysedOrdinals = Enumerable.Range(0, segments.Count).ToArray(),
            Truncated = truncated || parsed.Coverage.Truncated, AnySegmentDropped = dropped || parsed.Coverage.Truncated,
        };
        try
        {
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

    private static List<string>? Segment(string body, out bool truncated, out bool dropped)
    {
        // The parser bounds decoded text. Scan spans instead of a Split array or paragraph byte
        // buffer; only emitted chunks allocate strings, at most 32 chunks of 6000 UTF-8 bytes.
        var text = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var result = new List<string>();
        truncated = dropped = false;
        var position = 0;
        while (position < text.Length)
        {
            var paragraph = NextParagraph(text, ref position);
            if (paragraph.IsEmpty) continue;
            if (result.Count == MaxSegments)
            {
                truncated = dropped = true;
                break;
            }
            var chunkStart = 0;
            var bytes = 0;
            for (var offset = 0; offset < paragraph.Length;)
            {
                // A parser character cap can leave an unpaired surrogate. Refuse it instead of
                // manufacturing a replacement character in a classifier segment.
                if (Rune.DecodeFromUtf16(paragraph[offset..], out var rune, out var consumed) != OperationStatus.Done)
                    return null;
                if (bytes + rune.Utf8SequenceLength > MaxSegmentBytes)
                {
                    result.Add(paragraph[chunkStart..offset].ToString());
                    truncated = true;
                    if (result.Count == MaxSegments)
                    {
                        dropped = true;
                        return result;
                    }
                    chunkStart = offset;
                    bytes = 0;
                }
                bytes += rune.Utf8SequenceLength;
                offset += consumed;
            }
            result.Add(paragraph[chunkStart..].ToString());
        }
        return result;
    }

    private static ReadOnlySpan<char> NextParagraph(ReadOnlySpan<char> text, ref int position)
    {
        var start = position;
        while (position < text.Length)
        {
            var lineStart = position;
            var newline = text[position..].IndexOf('\n');
            var end = newline < 0 ? text.Length : position + newline;
            position = end < text.Length ? end + 1 : end;
            if (text[lineStart..end].Trim().IsEmpty)
            {
                var paragraph = text[start..lineStart].Trim();
                if (!paragraph.IsEmpty) return paragraph;
                start = position;
            }
        }
        return text[start..position].Trim();
    }
}
