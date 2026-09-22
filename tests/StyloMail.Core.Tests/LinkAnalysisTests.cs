using StyloMail.Core;

namespace StyloMail.Core.Tests;

/// <summary>
/// The link and internationalised-domain analysis, which is shared vocabulary rather than an email
/// concern.
/// </summary>
/// <remarks>
/// This lived inside the MIME adapter, where the only way to reuse it was to depend on a MIME parser
/// or to copy a security-relevant heuristic. It is in Core because a link lure is a link lure on
/// every channel, and because a second copy of the homograph check is the copy nobody re-reads.
///
/// The MIME suite remaining green after the move is the evidence it moved rather than changed. These
/// pin the surface itself, and they live here because that is where the code now is.
/// </remarks>
public sealed class LinkAnalysisTests
{
    [Fact]
    public void A_punycode_host_is_read_as_the_unicode_it_stands_for()
    {
        var observed = UrlTools.Observe("http://xn--pypal-4ve.com/login");

        Assert.NotNull(observed);

        // Both forms are recorded, because a homograph comparison needs to see what was transmitted
        // next to what it renders as.
        Assert.Equal("xn--pypal-4ve.com", observed.Host);
        Assert.Equal("pаypal.com", observed.UnicodeHost);
    }

    [Fact]
    public void A_cyrillic_a_in_a_host_reads_as_a_latin_a_and_is_named_as_a_confusable()
    {
        // The attack in one assertion: the host is not paypal.com, and it is built to look like it.
        var observed = UrlTools.Observe("http://xn--pypal-4ve.com/login");
        Assert.NotNull(observed);

        var idn = UrlTools.InspectIdn(observed);

        Assert.NotNull(idn);
        Assert.Equal("paypal.com", idn.AsciiSkeleton);
        Assert.Contains("U+0430->a", idn.Confusables);
    }

    [Fact]
    public void A_plain_ascii_host_has_no_internationalised_observation()
    {
        // Not applicable rather than an empty observation: absence is a distinct state, and returning
        // an observation with nothing in it would let a reader mistake "nothing to see" for "seen".
        var observed = UrlTools.Observe("http://example.com/");

        Assert.NotNull(observed);
        Assert.Null(UrlTools.InspectIdn(observed));
    }

    [Fact]
    public void A_host_that_cannot_be_parsed_is_not_half_parsed()
    {
        Assert.Null(UrlTools.Observe("   "));
        Assert.Null(UrlTools.Observe(null));
    }

    [Theory]
    [InlineData("see http://example.com/a and https://other.example/b", 2)]
    [InlineData("go to www.example.com now", 1)]
    public void Bare_urls_in_plain_text_are_found(string text, int expected)
    {
        // Plain text is the only thing a chat message has. This is the entry point that makes the
        // analysis reachable for a channel with no HTML part at all.
        Assert.Equal(expected, LinkAnalysis.BareUrlsIn(text).Count());
    }

    [Fact]
    public void A_bare_url_is_its_own_label_and_so_cannot_disagree_with_itself()
    {
        var findings = LinkAnalysis.FromPlainText("please review http://example.com/invoice", maxLinks: 8);

        var finding = Assert.Single(findings);
        Assert.Equal("http://example.com/invoice", finding.ActualTarget);

        // In plain text the URL is its own label. It still names a host, so it counts as a claiming
        // label rather than a "click here" and belongs in the denominator, but there is nothing for
        // it to disagree with. Reporting a mismatch here would be the analysis attributing a claim
        // the message never made.
        Assert.Equal(finding.ActualTarget, finding.DisplayedText);
        Assert.True(finding.LabelMakesHostClaim);
        Assert.False(finding.DisplayMismatch);
        Assert.Null(finding.MismatchKind);
    }

    [Fact]
    public void A_label_naming_a_different_host_than_it_points_at_is_a_mismatch()
    {
        var findings = LinkAnalysis.Extract(
            declaredLinks: [("http://paypal.com.evil.example/login", "paypal.com")],
            plainText: string.Empty,
            maxLinks: 8);

        var finding = Assert.Single(findings);

        // The label and the target are kept separately and never collapsed, which is the whole point:
        // storing only one of them loses the detection.
        Assert.Equal("paypal.com", finding.DisplayedText);
        Assert.Equal("http://paypal.com.evil.example/login", finding.ActualTarget);
        Assert.True(finding.DisplayMismatch);
        Assert.Equal("label-host", finding.MismatchKind);
    }

    [Fact]
    public void A_label_that_names_no_destination_is_not_compared()
    {
        // "Click here" claims nothing, so it cannot disagree with anything. Counting it would dilute
        // the ratio with labels that were never in play.
        var findings = LinkAnalysis.Extract(
            declaredLinks: [("http://example.com/x", "Click here")],
            plainText: string.Empty,
            maxLinks: 8);

        var finding = Assert.Single(findings);
        Assert.False(finding.LabelMakesHostClaim);
        Assert.False(finding.DisplayMismatch);
    }

    [Fact]
    public void The_limit_bounds_how_many_findings_come_back()
    {
        // Bounded cardinality is a property of this system rather than tuning, and this runs over
        // content an outside party chose.
        var findings = LinkAnalysis.FromPlainText(
            "http://a.example/1 http://b.example/2 http://c.example/3", maxLinks: 2);

        Assert.Equal(2, findings.Count);
    }

    [Fact]
    public void The_same_link_twice_is_one_finding()
    {
        var findings = LinkAnalysis.FromPlainText(
            "http://example.com/x and again http://example.com/x", maxLinks: 8);

        Assert.Single(findings);
    }
}
