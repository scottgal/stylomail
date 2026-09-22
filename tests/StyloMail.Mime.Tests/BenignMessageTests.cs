using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Mime.Tests;

/// <summary>
/// The message the system must stay quiet about. A detector that fires on ordinary
/// correspondence is a detector that gets switched off, so this is the case worth pinning down
/// first: everything that can be "found" is asserted to be found as zero, not as absent.
/// </summary>
public class BenignMessageTests
{
    private static readonly BoundedMimeMessageAnalyzer Analyzer = new();

    private static MimeAnalysisResult AnalyzeBenign() =>
        Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com"));

    [Fact]
    public void PlainBenignMessage_ParsesAndProducesNoPositiveSignals()
    {
        var result = AnalyzeBenign();

        Assert.Equal(MimeParseDisposition.Parsed, result.Disposition);

        Assert.Equal(0.0, result.Signal(MimeSignals.EnvelopeHeaderIdentity).Value);
        Assert.Equal(0.0, result.Signal(MimeSignals.DisplayNameAddressMismatch).Value);
        Assert.Equal(0.0, result.Signal(MimeSignals.PaddingObfuscation).Value);
        Assert.Equal(0.0, result.Signal(MimeSignals.ThreadHeaderConsistency).Value);
        Assert.Equal(0.0, result.Signal(MimeSignals.ContentEncrypted).Value);
        Assert.Equal(EvidenceAvailability.NotApplicable, result.Signal(MimeSignals.ReplyToDivergence).Availability);
        Assert.Equal(EvidenceAvailability.NotApplicable, result.Signal(MimeSignals.LinkDisplayMismatch).Availability);
        Assert.Equal(EvidenceAvailability.NotApplicable, result.Signal(MimeSignals.HtmlTextDisagreement).Availability);
    }

    [Fact]
    public void PlainBenignMessage_ProducesNoHardPositiveFactsAtAll()
    {
        // Descriptive signals (size, structure, recipient count) legitimately carry non-zero
        // values. What must stay at zero is every signal that reports a *disagreement*.
        string[] anomalySignals =
        [
            MimeSignals.EnvelopeHeaderIdentity,
            MimeSignals.DisplayNameAddressMismatch,
            MimeSignals.ReplyToDivergence,
            MimeSignals.TrustedAuthenticationFailure,
            MimeSignals.LinkDisplayMismatch,
            MimeSignals.LinkIdn,
            MimeSignals.LinkIdnHomograph,
            MimeSignals.AttachmentTypeMismatch,
            MimeSignals.HtmlTextDisagreement,
            MimeSignals.PaddingObfuscation,
            MimeSignals.ThreadHeaderConsistency,
            MimeSignals.ContentEncrypted,
        ];

        var result = AnalyzeBenign();
        var suspicious = result.Evidence
            .Where(e => anomalySignals.Contains(e.SignalId))
            .Where(e => e.Availability == EvidenceAvailability.Available && e.Value is > 0)
            .Select(e => $"{e.SignalId}={e.Value}")
            .ToList();

        Assert.True(
            suspicious.Count == 0,
            $"A benign message produced non-zero signals: {string.Join(", ", suspicious)}");
    }

    [Fact]
    public void BenignMultipart_ReportsAgreeingRepresentations()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request("benign-multipart.eml", mailFrom: "alice@example.com"));

        Assert.Equal(MimeParseDisposition.Parsed, result.Disposition);

        var disagreement = result.Signal(MimeSignals.HtmlTextDisagreement);
        Assert.Equal(EvidenceAvailability.Available, disagreement.Availability);
        Assert.InRange(disagreement.Value!.Value, 0.0, 0.2);
        Assert.False(result.Coverage.HtmlTextDisagreement);
    }

    [Fact]
    public void MatchingEnvelopeAndHeaderIdentity_IsNotAMismatch()
    {
        // The same message with a MAIL FROM that matches its From header must not report one.
        var matching = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com"));
        var differing = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "someone@else.test"));

        Assert.Equal(0.0, matching.Signal(MimeSignals.EnvelopeHeaderIdentity).Value);
        Assert.Equal(1.0, differing.Signal(MimeSignals.EnvelopeHeaderIdentity).Value);
    }

    [Fact]
    public void MissingProvenance_IsRecordedRatherThanAssumedClean()
    {
        var result = AnalyzeBenign();

        var provenance = result.Signal(MimeSignals.AuthenticationProvenance);
        Assert.Equal(EvidenceAvailability.Available, provenance.Availability);
        Assert.Equal(1.0, provenance.Value);
        Assert.Equal("True", provenance.Attribute("provenanceIncomplete"));

        // Authentication unknown, conversation context absent: both are visible as coverage, not
        // as a clean bill of health.
        Assert.Equal(EvidenceAvailability.ReducedCoverage, result.Signal(MimeSignals.AnalysisCoverage).Availability);
        Assert.True(result.Coverage.ConversationContextMissing);
    }

    [Fact]
    public void SuppliedProvenanceAndContext_RemoveTheReducedCoverageMarkers()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "benign-plain.eml",
            mailFrom: "alice@example.com",
            authentication: FixtureMessage.TrustedAuthentication(FixtureMessage.Result("spf", "pass")),
            conversationContext: ["earlier message"]));

        Assert.Equal(EvidenceAvailability.Available, result.Signal(MimeSignals.AnalysisCoverage).Availability);
        Assert.False(result.Coverage.ConversationContextMissing);
        Assert.Equal(0.0, result.Signal(MimeSignals.AuthenticationProvenance).Value);
    }
}
