using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Threading;
using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Host.Proxy;

/// <summary>
/// The Host's deterministic warning analysis: access's bounded header extractor for tokens, and the
/// existing MIME analyser for the four closed marker kinds.
/// </summary>
/// <remarks>
/// <para>
/// <b>One parser.</b> The message is parsed by <see cref="IMimeMessageAnalyzer"/>, the same analyser
/// the rest of the host uses. The only other read is the bounded header block access's
/// <see cref="ThreadingHeaderExtractor"/> requires, which is a header-line allowlist rather than a
/// MIME parser. No second content parser is introduced here.
/// </para>
/// <para>
/// <b>No content leaves this type.</b> Findings carry a closed marker kind and a finding id made of
/// the stable MIME signal id plus an ordinal. The evidence attributes that contain display names,
/// URLs or attachment names are read only to count findings; they are never copied into a result,
/// a finding id, a log line or the store.
/// </para>
/// </remarks>
public sealed class HostProxyWarningAnalyzer : IProxyWarningAnalyzer
{
    private const int MaximumHeaderBytes = 16 * 1024;

    /// <summary>Per-kind cap so one hostile message cannot inflate the store.</summary>
    internal const int MaximumFindingsPerKind = 32;

    private readonly IMimeMessageAnalyzer? _mime;
    private readonly ThreadingHeaderExtractor _threading;
    private readonly TimeProvider _clock;

    public HostProxyWarningAnalyzer(
        IMimeMessageAnalyzer? mime,
        ThreadingHeaderExtractor threading,
        TimeProvider clock)
    {
        _mime = mime;
        _threading = threading ?? throw new ArgumentNullException(nameof(threading));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(
        ProxyWarningAnalysisInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var message = input.Message.Span;
        var header = message[..Math.Min(message.Length, MaximumHeaderBytes)];

        // The token rule is the extractor's, in every branch, including the no-analyser one.
        var threading = _threading.Extract(input.TenantId, input.AccountId, header, input.ObservedAt);

        if (_mime is null)
        {
            return ValueTask.FromResult(Result(
                threading, ProxyWarningAvailability.NotProduced,
                ProxyWarningAvailabilityReason.NoAnalyserConfigured, []));
        }

        var envelope = new MailEnvelope
        {
            InternalMessageId = threading.MessageToken,
            TenantId = input.TenantId,
            Direction = MailDirection.Outbound,
            TrustedPrincipalId = input.AccountId,
            // Envelope facts are not part of the warning input and are not needed by the four
            // deterministic rules. Empty values are recorded as unknown, never fabricated.
            MailFrom = string.Empty,
            RcptTo = [],
            ReceivedAt = input.ObservedAt,
            MimeDigest = string.Empty,
            PayloadReference = string.Empty,
        };

        MimeAnalysisResult analysis;
        try
        {
            analysis = _mime.Analyze(new MimeAnalysisRequest
            {
                Envelope = envelope,
                RawMessage = input.Message,
                TimeProvider = _clock,
            });
        }
        catch (Exception)
        {
            // The MIME adapter is documented not to throw for hostile input. If that ever changes,
            // an unreadable message is still a named outcome rather than an exception into the
            // relay or a fabricated clean result.
            return ValueTask.FromResult(Result(
                threading, ProxyWarningAvailability.NotProduced,
                ProxyWarningAvailabilityReason.UnreadableMessage, []));
        }

        if (!analysis.IsAnalysable || analysis.Message is null)
        {
            return ValueTask.FromResult(Result(
                threading, ProxyWarningAvailability.NotProduced,
                ProxyWarningAvailabilityReason.UnreadableMessage, []));
        }

        if (!analysis.Message.Coverage.BodyParsed)
        {
            // Parsed structurally, but no analysable body (for example wholly encrypted content).
            return ValueTask.FromResult(Result(
                threading, ProxyWarningAvailability.NotProduced,
                ProxyWarningAvailabilityReason.UnsupportedContent, []));
        }

        return ValueTask.FromResult(Result(
            threading, ProxyWarningAvailability.Available,
            ProxyWarningAvailabilityReason.None, Findings(analysis.Evidence)));
    }

    private static ProxyWarningAnalysisResult Result(
        ProxyThreadingEvent threading,
        ProxyWarningAvailability availability,
        ProxyWarningAvailabilityReason reason,
        IReadOnlyList<ProxyWarningFinding> findings)
        => new()
        {
            MessageToken = threading.MessageToken,
            ThreadToken = threading.ThreadToken,
            Availability = availability,
            AvailabilityReason = reason,
            Findings = findings,
        };

    /// <summary>
    /// Maps the MIME adapter's deterministic signals onto the closed marker kinds.
    /// </summary>
    /// <remarks>
    /// Only an <see cref="EvidenceAvailability.Available"/> signal contributes a finding.
    /// <c>ReducedCoverage</c> means the signal exists but its input was weaker (for example no
    /// trusted verifier for authentication), and promoting it to a marker would state more than the
    /// parser observed.
    /// </remarks>
    private static IReadOnlyList<ProxyWarningFinding> Findings(IReadOnlyList<Evidence> evidence)
    {
        var findings = new List<ProxyWarningFinding>();

        foreach (var signal in evidence)
        {
            if (signal.Availability != EvidenceAvailability.Available)
            {
                continue;
            }

            var (kind, count) = signal.SignalId switch
            {
                MimeSignals.TrustedAuthenticationFailure =>
                    (ProxyWarningMarkerKind.AuthenticationFailed, Count(signal.Value)),
                MimeSignals.LinkDisplayMismatch =>
                    (ProxyWarningMarkerKind.LinkDomainMismatch, AttributeCount(signal, "mismatchCount")),
                MimeSignals.DisplayNameAddressMismatch =>
                    (ProxyWarningMarkerKind.DisplayNameMismatch, Count(signal.Value)),
                MimeSignals.AttachmentTypeMismatch =>
                    (ProxyWarningMarkerKind.AttachmentTypeMismatch, Count(signal.Value)),
                _ => (ProxyWarningMarkerKind.AuthenticationFailed, 0),
            };

            if (count == 0)
            {
                continue;
            }

            // Stable, content-free ids: the signal id plus an ordinal. The evidence attributes,
            // which do contain display names and URLs, are never used here.
            for (var index = 0; index < Math.Min(count, MaximumFindingsPerKind); index++)
            {
                findings.Add(new ProxyWarningFinding
                {
                    MarkerKind = kind,
                    FindingId = $"{signal.SignalId}:{index}",
                });
            }
        }

        return findings;
    }

    private static int Count(double? value)
        => value is { } number && number > 0 ? (int)Math.Min(number, MaximumFindingsPerKind) : 0;

    private static int AttributeCount(Evidence signal, string name)
    {
        foreach (var attribute in signal.Attributes ?? [])
        {
            if (string.Equals(attribute.Name, name, StringComparison.Ordinal)
                && int.TryParse(attribute.Value, out var count))
            {
                return Math.Clamp(count, 0, MaximumFindingsPerKind);
            }
        }

        return 0;
    }
}
