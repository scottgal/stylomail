using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Mime.Tests;

public class ContentComparisonTests
{
    private static readonly BoundedMimeMessageAnalyzer Analyzer = new();

    [Fact]
    public void HtmlAndTextSayingDifferentThings_IsRecorded()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request("html-text-disagreement.eml"));

        var signal = result.Signal(MimeSignals.HtmlTextDisagreement);
        Assert.Equal(EvidenceAvailability.Available, signal.Availability);
        Assert.True(signal.Value > 0.5, $"expected a large disagreement, got {signal.Value}");
        Assert.True(result.Coverage.HtmlTextDisagreement);
    }

    [Fact]
    public void HtmlAndTextSayingTheSameThing_IsNotADisagreement()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "benign-multipart.eml",
            mailFrom: "alice@example.com"));

        var signal = result.Signal(MimeSignals.HtmlTextDisagreement);
        Assert.Equal(EvidenceAvailability.Available, signal.Availability);
        Assert.InRange(signal.Value!.Value, 0.0, 0.2);
        Assert.False(result.Coverage.HtmlTextDisagreement);
    }

    [Theory]
    [InlineData("benign-plain.eml")]
    [InlineData("link-display-mismatch.eml")]
    public void AMessageWithOnlyOneRepresentation_IsNotApplicable(string fixture)
    {
        // Both directions matter, and they are guarded differently. The plain-only fixture is
        // excluded by a token count; the HTML-only one has plenty of tokens on both sides, so only
        // the "a real plain part must exist" gate keeps it out. Without that gate the comparison
        // is the HTML against itself, which reports a confident zero for a question never asked.
        var result = Analyzer.Analyze(FixtureMessage.Request(fixture));

        Assert.Equal(
            EvidenceAvailability.NotApplicable,
            result.Signal(MimeSignals.HtmlTextDisagreement).Availability);
    }

    [Fact]
    public void TheAnalysisView_PrefersPlainTextAndKeepsQuotedHistorySeparate()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "benign-multipart.eml",
            mailFrom: "alice@example.com"));

        Assert.NotNull(result.Message!.BodyText);
        Assert.Contains("invoice for August", result.Message.BodyText);

        // No attribution line in this message, so nothing is claimed to be quoted. Inventing a
        // split would invent the distinction the classifier is meant to weigh.
        Assert.Null(result.Message.QuotedText);
        Assert.Equal("none", result.Signal(MimeSignals.QuotedHistory).Attribute("marker"));
    }

    [Fact]
    public void QuotedHistory_IsSeparatedFromNewText()
    {
        var bytes = SyntheticMessage.WithBody(
            """
            Yes, Thursday works for me.

            On Mon, 21 Sep 2026 at 14:02, Bob Example <bob@example.org> wrote:
            > Can we move the review to Thursday?
            > Let me know what suits you.
            """);

        var result = Analyzer.Analyze(FixtureMessage.FromBytes(bytes));

        Assert.Contains("Thursday works", result.Message!.BodyText);
        Assert.DoesNotContain("move the review", result.Message.BodyText);
        Assert.Contains("move the review", result.Message.QuotedText);

        var signal = result.Signal(MimeSignals.QuotedHistory);
        Assert.Equal("attribution-line", signal.Attribute("marker"));
        Assert.True(signal.Value > 0.0);
    }

    [Fact]
    public void SameTemplateWithDifferentFillers_GivesTheSameSkeleton()
    {
        // Template similarity is what makes bounded near-duplicate campaign grouping possible, so
        // the fingerprint has to survive the parts that change between two sendings: the recipient
        // address, the identifiers and the amounts. Names and wording are content and stay.
        var first = Analyzer.Analyze(FixtureMessage.FromBytes(SyntheticMessage.WithBody(
            "Your invoice 44714471 for 120.00 GBP is ready. Contact billing@example.com.")));
        var second = Analyzer.Analyze(FixtureMessage.FromBytes(SyntheticMessage.WithBody(
            "Your invoice 99159915 for 109.50 GBP is ready. Contact accounts@example.org.")));

        var firstPrint = first.Signal(MimeSignals.TemplateFingerprint);
        var secondPrint = second.Signal(MimeSignals.TemplateFingerprint);

        Assert.Equal(firstPrint.Attribute("skeletonDigest"), secondPrint.Attribute("skeletonDigest"));
    }

    [Fact]
    public void DifferentText_GivesDifferentFingerprints()
    {
        var first = Analyzer.Analyze(FixtureMessage.FromBytes(SyntheticMessage.WithBody(
            "Dear Alice, your invoice 4471 is ready.")));
        var second = Analyzer.Analyze(FixtureMessage.FromBytes(SyntheticMessage.WithBody(
            "Please review the attached safety briefing before Friday.")));

        Assert.NotEqual(
            first.Signal(MimeSignals.TemplateFingerprint).Attribute("skeletonDigest"),
            second.Signal(MimeSignals.TemplateFingerprint).Attribute("skeletonDigest"));
    }

    [Fact]
    public void TemplateFingerprint_IsStableAcrossRuns()
    {
        var first = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com"));
        var second = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com"));

        Assert.Equal(
            first.Signal(MimeSignals.TemplateFingerprint).Attribute("simhash"),
            second.Signal(MimeSignals.TemplateFingerprint).Attribute("simhash"));
    }

    [Fact]
    public void RecipientFanOut_IsReported()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "benign-plain.eml",
            mailFrom: "alice@example.com",
            rcptTo: ["a@example.org", "b@example.org", "c@other.test"]));

        var signal = result.Signal(MimeSignals.RecipientCount);
        Assert.Equal(3.0, signal.Value);
        Assert.Equal("2", signal.Attribute("distinctRecipientDomains"));
    }
}
