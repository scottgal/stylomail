using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Mime.Tests;

public class LinkSignalTests
{
    private static readonly BoundedMimeMessageAnalyzer Analyzer = new();

    [Fact]
    public void LabelNamingOneHostOverATargetOnAnother_IsRecorded()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "link-display-mismatch.eml",
            mailFrom: "billing@example-billing.test"));

        var signal = result.Signal(MimeSignals.LinkDisplayMismatch);
        Assert.Equal(EvidenceAvailability.Available, signal.Availability);
        Assert.Equal("1", signal.Attribute("mismatchCount"));
        Assert.Contains("label-host", signal.AttributeNames());
        Assert.Contains("198.51.100.7", signal.Attribute("label-host"));
    }

    [Fact]
    public void MatchingLabelAndTarget_IsNotAMismatch()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "benign-multipart.eml",
            mailFrom: "alice@example.com"));

        var signal = result.Signal(MimeSignals.LinkDisplayMismatch);
        Assert.Equal(EvidenceAvailability.Available, signal.Availability);
        Assert.Equal(0.0, signal.Value);
        Assert.Equal("0", signal.Attribute("mismatchCount"));
    }

    [Fact]
    public void LabelsThatNameNoDestination_AreNotTreatedAsMismatches()
    {
        // "click here" claims nothing, so there is nothing for it to disagree with, and it must
        // not dilute the ratio either.
        var result = Analyzer.Analyze(FixtureMessage.FromBytes(SyntheticMessage.WithBody(
            """<a href="https://evil.example/x">click here</a><a href="https://evil.example/y">open the portal</a>""",
            "text/html; charset=utf-8")));

        var signal = result.Signal(MimeSignals.LinkDisplayMismatch);
        Assert.Equal(EvidenceAvailability.Available, signal.Availability);
        Assert.Equal("0", signal.Attribute("labelledLinkCount"));
        Assert.Equal("0", signal.Attribute("mismatchCount"));
        Assert.Null(signal.Value);
    }

    [Fact]
    public void IdnHost_IsDetectedWithBothRepresentations()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "idn-homograph.eml",
            mailFrom: "team@xn--pypal-4ve.com"));

        var idn = result.Signal(MimeSignals.LinkIdn);
        Assert.Equal(EvidenceAvailability.Available, idn.Availability);
        Assert.Equal(1.0, idn.Value);
        Assert.Contains("xn--pypal-4ve.com", idn.Attribute("idn"));

        var link = result.Message!.Links.Single();
        Assert.Equal("xn--pypal-4ve.com", link.AsciiHost);
        Assert.NotNull(link.UnicodeHost);
        Assert.Contains('\u0430', link.UnicodeHost!);
    }

    [Fact]
    public void IdnHomograph_IsReportedWithTheConfusableFoldedToAscii()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "idn-homograph.eml",
            mailFrom: "team@xn--pypal-4ve.com"));

        var homograph = result.Signal(MimeSignals.LinkIdnHomograph);
        Assert.Equal(EvidenceAvailability.Available, homograph.Availability);
        Assert.Equal(1.0, homograph.Value);

        var detail = homograph.Attribute("homograph")!;
        Assert.Contains("looks like paypal.com", detail);
        Assert.Contains("cyrillic", detail);
        Assert.Contains("U+0430->a", detail);
    }

    [Fact]
    public void AsciiOnlyHosts_ProduceNoIdnSignal()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "benign-multipart.eml",
            mailFrom: "alice@example.com"));

        Assert.Equal(EvidenceAvailability.NotApplicable, result.Signal(MimeSignals.LinkIdn).Availability);
        Assert.Equal(EvidenceAvailability.NotApplicable, result.Signal(MimeSignals.LinkIdnHomograph).Availability);
    }

    [Fact]
    public void LinkHostProfile_ReportsTheObservedFactsAndAStableDigest()
    {
        // Novelty is a baseline comparison and is not decided here. What is recorded is the host
        // set itself, with a digest the adaptive engine can compare against a recipient's history.
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "link-display-mismatch.eml",
            mailFrom: "billing@example-billing.test"));

        var profile = result.Signal(MimeSignals.LinkHostProfile);
        Assert.Equal(EvidenceAvailability.Available, profile.Availability);
        Assert.Equal("1", profile.Attribute("ipLiteralCount"));
        Assert.Equal("1", profile.Attribute("plaintextHttpCount"));
        Assert.NotNull(profile.Attribute("hostSetDigest"));
    }

    [Fact]
    public void IpLiteralTargetUnderAnHttpsLabel_IsStillAReportedMismatch()
    {
        // The label claims TLS; the target is a bare address over plaintext HTTP. Both facts are
        // recorded, and neither is resolved in the sender's favour.
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "link-display-mismatch.eml",
            mailFrom: "billing@example-billing.test"));

        Assert.Contains(
            result.Message!.Links,
            l => l.ActualTarget.StartsWith("http://198.51.100.7", StringComparison.Ordinal));
    }

    [Fact]
    public void LinkExtraction_DoesNotFetchAnything()
    {
        // The message points at a reserved TEST-NET address. If anything here tried to resolve or
        // fetch it, the test would either hang or fail; it completes because nothing dials out.
        var bytes = SyntheticMessage.WithBody(
            """<img src="http://198.51.100.7/pixel.gif"><a href="http://198.51.100.7/verify">http://198.51.100.7/verify</a>""",
            "text/html; charset=utf-8");

        var result = Analyzer.Analyze(FixtureMessage.FromBytes(bytes));

        Assert.Equal(MimeParseDisposition.Parsed, result.Disposition);

        // The remote image is recorded as observed markup, and never retrieved.
        var markup = result.Signal(MimeSignals.HtmlMarkupObservation);
        Assert.Equal(EvidenceAvailability.Available, markup.Availability);
        Assert.Equal("1", markup.Attribute("externalImageCount"));
    }
}
