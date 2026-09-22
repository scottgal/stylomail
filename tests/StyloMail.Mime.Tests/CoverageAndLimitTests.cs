using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Mime.Tests;

/// <summary>
/// The cases where the adapter cannot do its job. Each must produce an explicit disposition —
/// never a partial parse presented as a complete message.
/// </summary>
public class CoverageAndLimitTests
{
    private static readonly BoundedMimeMessageAnalyzer Analyzer = new();

    [Fact]
    public void MessageLargerThanTheLimit_IsRejectedAsOversize()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "benign-plain.eml",
            mailFrom: "alice@example.com",
            limits: MimeParseLimits.Default with { MaxMessageBytes = 100 }));

        Assert.Equal(MimeParseDisposition.Oversize, result.Disposition);
        Assert.Equal("message-bytes", result.Rejection!.Reason);
        Assert.Equal(100, result.Rejection.Limit);
        Assert.True(result.Coverage.ParserLimitExceeded);
    }

    [Fact]
    public void MessageWithTooManyParts_IsRejectedBeforeParsing()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "many-parts.eml",
            mailFrom: "bulk@example-bulk.test",
            limits: MimeParseLimits.Default with { MaxParts = 10 }));

        Assert.Equal(MimeParseDisposition.LimitExceeded, result.Disposition);

        // The bare reason is the pre-scan's. An "-after-parse" suffix would mean the parser had
        // already been handed the message, which is the outcome the structural scan exists to
        // prevent — so asserting the bare reason is what gives this test teeth.
        Assert.Equal("part-count", result.Rejection!.Reason);
        Assert.True(result.Rejection.Observed > 10);
    }

    [Fact]
    public void TheSameMessageUnderGenerousLimits_ParsesNormally()
    {
        // The rejection above must be about the configured limit, not about the fixture.
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "many-parts.eml",
            mailFrom: "bulk@example-bulk.test"));

        Assert.Equal(MimeParseDisposition.Parsed, result.Disposition);
        Assert.Equal(41.0, result.Signal(MimeSignals.MessageStructure).Value);
    }

    [Fact]
    public void DeeplyNestedMessage_IsRejectedRatherThanSilentlyTruncated()
    {
        // This is the case that matters most. If nesting were left to the parser's own ceiling, the
        // parser would stop descending and the readable outer layer would be analysed as though the
        // message ended there.
        var result = Analyzer.Analyze(FixtureMessage.FromBytes(
            SyntheticMessage.NestedMultipart(8),
            MimeParseLimits.Default with { MaxMimeDepth = 3 }));

        Assert.Equal(MimeParseDisposition.LimitExceeded, result.Disposition);
        Assert.Equal("mime-depth", result.Rejection!.Reason);
        Assert.Equal(3, result.Rejection.Limit);
        Assert.True(result.Rejection.Observed > 3);
    }

    [Fact]
    public void NestingWithinTheLimit_IsAccepted()
    {
        var result = Analyzer.Analyze(FixtureMessage.FromBytes(SyntheticMessage.NestedMultipart(4)));

        Assert.Equal(MimeParseDisposition.Parsed, result.Disposition);

        // The root multipart, four nested multiparts and the innermost text part: six entities.
        // The depth here is measured on the parsed tree, which is one level deeper than the
        // boundary nesting the preflight counts.
        Assert.Equal(6.0, result.Signal(MimeSignals.MessageStructure).Value);
        Assert.Equal("6", result.Signal(MimeSignals.MessageStructure).Attribute("maxDepth"));
    }

    [Fact]
    public void MessageWithTooManyHeaders_IsRejected()
    {
        var result = Analyzer.Analyze(FixtureMessage.FromBytes(
            SyntheticMessage.WithManyHeaders(400),
            MimeParseLimits.Default with { MaxHeaderCount = 50 }));

        Assert.Equal(MimeParseDisposition.LimitExceeded, result.Disposition);
        Assert.Equal("header-count", result.Rejection!.Reason);
    }

    [Fact]
    public void OverlongHeaderLine_IsRejected()
    {
        // Unfolding is where a modest header becomes an expensive one, so the check happens on the
        // raw bytes before anyone tries.
        var result = Analyzer.Analyze(FixtureMessage.FromBytes(
            SyntheticMessage.WithLongHeaderLine(64 * 1024),
            MimeParseLimits.Default with { MaxHeaderLineLength = 4096 }));

        Assert.Equal(MimeParseDisposition.LimitExceeded, result.Disposition);
        Assert.Equal("header-line-length", result.Rejection!.Reason);
    }

    [Theory]
    [InlineData("this is just some prose, not a message at all", "no-header-body-separator")]
    [InlineData("Hello,\n\nHow are you?\n\nRegards", "no-header-fields")]
    [InlineData("<html><body>not mail</body></html>", "no-header-body-separator")]
    public void BytesThatAreNotAMessage_AreRejectedAsMalformed(string text, string expectedReason)
    {
        var result = Analyzer.Analyze(FixtureMessage.FromBytes(SyntheticMessage.Utf8(text)));

        Assert.Equal(MimeParseDisposition.Malformed, result.Disposition);
        Assert.Null(result.Message);

        // The reason is asserted, not just the disposition, because two different things can
        // produce "Malformed": our own header-block validation refusing the input, and the parser
        // throwing on headers we let through. Only the first is a guarantee we control — the
        // second depends on MimeKit happening to be strict here, which is not a property to rely
        // on for hostile input.
        Assert.Equal(expectedReason, result.Rejection!.Reason);
    }

    [Fact]
    public void EmptyInput_IsRejectedAsMalformed()
    {
        var result = Analyzer.Analyze(FixtureMessage.FromBytes([]));

        Assert.Equal(MimeParseDisposition.Malformed, result.Disposition);
        Assert.Equal("empty-input", result.Rejection!.Reason);
    }

    [Fact]
    public void AHeadersOnlyMessage_IsStillAMessage()
    {
        // No blank line and no body, but every line is a well-formed header field.
        var bytes = SyntheticMessage.Utf8(
            "From: Alice <alice@example.com>\r\n" +
            "Subject: Headers only\r\n");

        var result = Analyzer.Analyze(FixtureMessage.FromBytes(bytes));

        Assert.Equal(MimeParseDisposition.Parsed, result.Disposition);
        Assert.Equal("Headers only", result.Message!.Subject);
    }

    [Fact]
    public void ARejectedMessage_ProducesNoAnalysisViewAtAll()
    {
        // The one thing that must never happen is a half-read message being handed on as though it
        // were whole. A rejection means no MailAnalysisInput, and the reason is on the ledger.
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "many-parts.eml",
            mailFrom: "bulk@example-bulk.test",
            limits: MimeParseLimits.Default with { MaxParts = 10 }));

        Assert.Null(result.Message);
        Assert.False(result.IsAnalysable);
        Assert.NotNull(result.Rejection);

        // The rejection is still visible as evidence, so the ledger records what was seen.
        Assert.Contains(result.Evidence, e => e.SignalId == MimeSignals.AnalysisCoverage);
        Assert.Contains(result.Evidence, e => e.SignalId == MimeSignals.MessageSize);
    }

    [Fact]
    public void TruncatedMultipart_IsMarkedTruncatedRatherThanComplete()
    {
        // The closing boundary never arrives: the message was cut short in transit.
        var bytes = SyntheticMessage.Utf8(
            """
            From: Alice <alice@example.com>
            To: bob@example.org
            Subject: Cut short
            Date: Mon, 22 Sep 2026 09:15:00 +0100
            Message-ID: <trunc@example.com>
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="cut"

            --cut
            Content-Type: text/plain; charset=utf-8

            the message stops here
            """);

        var result = Analyzer.Analyze(FixtureMessage.FromBytes(bytes));

        Assert.Equal(MimeParseDisposition.Parsed, result.Disposition);
        Assert.True(result.Coverage.Truncated);
        Assert.Contains("truncated", result.Signal(MimeSignals.AnalysisCoverage).Attribute("reduced"));
    }

    [Fact]
    public void EveryReducedCoverageReasonSurvives_NotJustTheLastOne()
    {
        // This is the bug the list-shaped Attributes contract was changed to fix. Encrypted
        // ciphertext with no provenance and no conversation context reduces coverage for several
        // independent reasons, and a dictionary silently kept only the final one — so the ledger
        // reported a single cause for a message that had four.
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "encrypted-smime.eml",
            mailFrom: "sender@example-secure.test"));

        var coverage = result.Signal(MimeSignals.AnalysisCoverage);
        var reasons = coverage.AttributesNamed("reduced");

        Assert.True(
            reasons.Count >= 3,
            $"expected several distinct reduced-coverage reasons, got: [{string.Join(", ", reasons)}]");

        Assert.Equal(reasons.Count, reasons.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(reasons.Count.ToString(), coverage.Attribute("reducedCount"));
        Assert.Equal(reasons.Count, coverage.Value);
    }

    [Fact]
    public void AnOversizeMessage_IsRecordedAsOversizeRatherThanStructurallyHostile()
    {
        // "Too big" and "maliciously shaped" need different responses, and a single
        // ParserLimitExceeded flag would lose that distinction in the ledger.
        var oversize = Analyzer.Analyze(FixtureMessage.Request(
            "benign-plain.eml",
            mailFrom: "alice@example.com",
            limits: MimeParseLimits.Default with { MaxMessageBytes = 100 }));

        var structural = Analyzer.Analyze(FixtureMessage.Request(
            "many-parts.eml",
            mailFrom: "bulk@example-bulk.test",
            limits: MimeParseLimits.Default with { MaxParts = 10 }));

        Assert.True(oversize.Coverage.OversizeRejected);
        Assert.False(structural.Coverage.OversizeRejected);
        Assert.True(structural.Coverage.ParserLimitExceeded);
    }

    [Fact]
    public void Coverage_RecordsWhatCouldNotBeRead()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com"));

        Assert.True(result.Coverage.BodyParsed);
        Assert.False(result.Coverage.HtmlPresent);
        Assert.False(result.Coverage.ContentEncrypted);
        Assert.False(result.Coverage.ParserLimitExceeded);
        Assert.True(result.Coverage.ConversationContextMissing);
    }
}
