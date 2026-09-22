using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Mime.Tests;

/// <summary>
/// Authentication is provenance, not a verdict. These tests pin that distinction down, including
/// the case where a message tries to vouch for itself.
/// </summary>
public class AuthenticationSignalTests
{
    private static readonly BoundedMimeMessageAnalyzer Analyzer = new();

    [Fact]
    public void NoAuthenticationContext_IsRecordedAsIncompleteProvenance()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com"));

        Assert.Equal(1.0, result.Signal(MimeSignals.AuthenticationProvenance).Value);
        Assert.Equal(
            EvidenceAvailability.NotApplicable,
            result.Signal(MimeSignals.TrustedAuthenticationFailure).Availability);
    }

    [Fact]
    public void AMessageCannotVouchForItself()
    {
        // The message carries its own Authentication-Results header, claiming a clean pass. No
        // trusted verifier said so, therefore it carries no authority and provenance stays
        // incomplete. This is the spoofed-header case, and it must not read as authenticated.
        var bytes = SyntheticMessage.Utf8(
            """
            From: Alice <alice@example.com>
            To: bob@example.org
            Subject: Synthetic
            Date: Mon, 22 Sep 2026 09:15:00 +0100
            Message-ID: <selfauth@example.com>
            Authentication-Results: example.com; spf=pass; dkim=pass; dmarc=pass
            MIME-Version: 1.0
            Content-Type: text/plain; charset=utf-8

            body
            """);

        var result = Analyzer.Analyze(FixtureMessage.FromBytes(bytes));

        Assert.Equal(1.0, result.Signal(MimeSignals.AuthenticationProvenance).Value);
        Assert.Equal(
            EvidenceAvailability.NotApplicable,
            result.Signal(MimeSignals.TrustedAuthenticationFailure).Availability);
        Assert.True(result.Message!.Authentication.ProvenanceIncomplete);
    }

    [Fact]
    public void TrustedFailures_AreCounted()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "benign-plain.eml",
            mailFrom: "alice@example.com",
            authentication: FixtureMessage.TrustedAuthentication(
                FixtureMessage.Result("spf", "fail"),
                FixtureMessage.Result("dkim", "pass"),
                FixtureMessage.Result("dmarc", "fail"))));

        var signal = result.Signal(MimeSignals.TrustedAuthenticationFailure);
        Assert.Equal(EvidenceAvailability.Available, signal.Availability);
        Assert.Equal(2.0, signal.Value);
        Assert.Equal("fail", signal.Attribute("trusted.spf"));
        Assert.Equal("pass", signal.Attribute("trusted.dkim"));
        Assert.Equal(0.0, result.Signal(MimeSignals.AuthenticationProvenance).Value);
    }

    [Fact]
    public void TrustedPasses_AreRecordedButAreNotTreatedAsAFailure()
    {
        // A compromised authorised account authenticates correctly. A pass is recorded; it is not
        // evidence of anything benign, and it is not scored as one.
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "benign-plain.eml",
            mailFrom: "alice@example.com",
            authentication: FixtureMessage.TrustedAuthentication(
                FixtureMessage.Result("spf", "pass"),
                FixtureMessage.Result("dkim", "pass"),
                FixtureMessage.Result("dmarc", "pass"))));

        var signal = result.Signal(MimeSignals.TrustedAuthenticationFailure);
        Assert.Equal(EvidenceAvailability.Available, signal.Availability);
        Assert.Equal(0.0, signal.Value);
        Assert.Equal("pass", signal.Attribute("trusted.dmarc"));
    }

    [Fact]
    public void ResultsFromAnUntrustedSource_CarryNoAuthority()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "benign-plain.eml",
            mailFrom: "alice@example.com",
            authentication: new AuthenticationContext
            {
                ConnectingIp = null,
                AuthenticatedAccount = null,
                Results = [FixtureMessage.Result("dkim", "pass", trusted: false)],
                ApprovedSenderIdentities = [],
                ProvenanceIncomplete = false,
            }));

        var signal = result.Signal(MimeSignals.TrustedAuthenticationFailure);
        Assert.Equal(EvidenceAvailability.ReducedCoverage, signal.Availability);
        Assert.Null(signal.Value);
        Assert.Contains("not from a trusted verifier", signal.Attribute("untrusted.dkim"));

        // No trusted verifier reported anything, so provenance is still incomplete.
        Assert.Equal(1.0, result.Signal(MimeSignals.AuthenticationProvenance).Value);
    }

    [Fact]
    public void IncompleteProvenance_IsMarkedInCoverage()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com"));

        Assert.Contains(
            "authentication-provenance-incomplete",
            result.Signal(MimeSignals.AnalysisCoverage).AttributesNamed("reduced"));
    }
}
