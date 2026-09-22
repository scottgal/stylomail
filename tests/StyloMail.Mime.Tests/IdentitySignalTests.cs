using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Mime.Tests;

public class IdentitySignalTests
{
    private static readonly BoundedMimeMessageAnalyzer Analyzer = new();

    [Fact]
    public void DisplayNameThatIsAnotherAddress_IsRecorded()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "display-name-mismatch.eml",
            mailFrom: "attacker@evil.example"));

        var signal = result.Signal(MimeSignals.DisplayNameAddressMismatch);
        Assert.Equal(EvidenceAvailability.Available, signal.Availability);
        Assert.Equal(1.0, signal.Value);
        Assert.Contains("from-name-is-address", signal.AttributeNames());

        // The displayed address is recorded, but shortened: the ledger needs to show that two
        // identities disagreed, not to become a second address book.
        Assert.Contains("paypal.com", signal.Attribute("from-name-is-address"));
    }

    [Fact]
    public void ReplyToOnADifferentDomain_Diverges()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "display-name-mismatch.eml",
            mailFrom: "attacker@evil.example"));

        var signal = result.Signal(MimeSignals.ReplyToDivergence);
        Assert.Equal(EvidenceAvailability.Available, signal.Availability);
        Assert.Equal(1.0, signal.Value);
        Assert.Equal("evil.example", signal.Attribute("fromDomain"));
        Assert.Equal("mail-relay.example", signal.Attribute("replyToDomain"));
    }

    [Fact]
    public void ReplyToAbsent_IsNotApplicableRatherThanZero()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com"));

        var signal = result.Signal(MimeSignals.ReplyToDivergence);
        Assert.Equal(EvidenceAvailability.NotApplicable, signal.Availability);
        Assert.Null(signal.Value);
    }

    [Fact]
    public void EnvelopeNamingADifferentIdentityFromTheHeader_IsRecorded()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "bounce@mailer.test"));

        var signal = result.Signal(MimeSignals.EnvelopeHeaderIdentity);
        Assert.Equal(1.0, signal.Value);
        Assert.Contains("envelope-from-vs-header-from", signal.AttributeNames());
    }

    [Fact]
    public void InternationalisedDomain_IsNotReportedAsAnIdentityMismatch()
    {
        // The envelope carries the punycode form and the header carries the Unicode form of the
        // same domain. That is an encoding difference, not an identity difference, and reporting
        // it would make every internationalised sender look like an impersonation attempt.
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "idn-homograph.eml",
            mailFrom: "team@xn--pypal-4ve.com"));

        Assert.Equal(0.0, result.Signal(MimeSignals.EnvelopeHeaderIdentity).Value);
    }

    [Fact]
    public void DisplayNameClaimingAnUnrelatedDomain_IsRecorded()
    {
        var bytes = SyntheticMessage.Utf8(
            """
            From: "paypal.com Support" <billing@evil.example>
            To: bob@example.org
            Subject: Synthetic
            Date: Mon, 22 Sep 2026 09:15:00 +0100
            Message-ID: <synth-name@evil.example>
            MIME-Version: 1.0
            Content-Type: text/plain; charset=utf-8

            body
            """);

        var result = Analyzer.Analyze(FixtureMessage.FromBytes(bytes));

        var signal = result.Signal(MimeSignals.DisplayNameAddressMismatch);
        Assert.Equal(1.0, signal.Value);

        // The local part is shortened on purpose; the domain is the part that carries the claim.
        Assert.Contains("evil.example", signal.Attribute("from-name-claims-domain"));
    }

    [Fact]
    public void MessageIdHeader_IsCarriedThroughAsUntrusted()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com"));

        Assert.NotNull(result.Message);
        Assert.Equal("lunch-1@example.com", result.Message!.Envelope.UntrustedMessageIdHeader);
    }

    [Fact]
    public void MessageIdHeader_IsNeverUsedAsAnIdentifier()
    {
        // Two messages with the same Message-ID header and different bytes stay different
        // messages. The field exists for loop tracing, not for deduplication.
        var first = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com"));
        var second = Analyzer.Analyze(FixtureMessage.Request("benign-multipart.eml", mailFrom: "alice@example.com"));

        Assert.NotEqual(first.Message!.Envelope.InternalMessageId, second.Message!.Envelope.InternalMessageId);
        Assert.Equal(
            "lunch-1@example.com",
            first.Message.Envelope.UntrustedMessageIdHeader);
    }
}
