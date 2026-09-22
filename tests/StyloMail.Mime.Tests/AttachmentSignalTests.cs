using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Mime.Tests;

public class AttachmentSignalTests
{
    private static readonly BoundedMimeMessageAnalyzer Analyzer = new();

    private static MimeAnalysisResult AnalyzeRemittance() =>
        Analyzer.Analyze(FixtureMessage.Request(
            "attachment-type-mismatch.eml",
            mailFrom: "accounts@example-vendor.test"));

    [Fact]
    public void ExtensionImplyingOneTypeOverAnotherDeclaredType_IsRecorded()
    {
        var result = AnalyzeRemittance();

        var signal = result.Signal(MimeSignals.AttachmentTypeMismatch);
        Assert.Equal(EvidenceAvailability.Available, signal.Availability);
        Assert.Equal(1.0, signal.Value);
        Assert.Contains("invoice.pdf declares image/png", signal.Attribute("mismatch"));
    }

    [Fact]
    public void DoubleExtensionWithAnExecutableTail_IsCounted()
    {
        var result = AnalyzeRemittance();

        var signal = result.Signal(MimeSignals.AttachmentTypeMismatch);
        Assert.Equal("1", signal.Attribute("doubleExtensionCount"));
        Assert.Equal("1", signal.Attribute("executableExtensionCount"));
    }

    [Fact]
    public void OneHashIsEmittedPerAttachment()
    {
        var result = AnalyzeRemittance();

        var hashes = result.Evidence.Where(e => e.SignalId == MimeSignals.AttachmentHash).ToList();
        Assert.Equal(2, hashes.Count);
        Assert.All(hashes, h => Assert.Equal("attachment", h.ObservedScope));
        Assert.All(hashes, h => Assert.StartsWith("sha256:", h.Attribute("hash")));
        Assert.Equal(
            hashes.Count,
            hashes.Select(h => h.Attribute("hash")).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AttachmentMetadata_CarriesTheDeclaredAndImpliedTypes()
    {
        var result = AnalyzeRemittance();

        var invoice = result.Message!.Attachments.Single(a => a.FileName == "invoice.pdf");
        Assert.Equal("image/png", invoice.DeclaredContentType);
        Assert.Equal("application/pdf", invoice.ExtensionImpliedContentType);
        Assert.NotNull(invoice.ContentHash);
        Assert.False(invoice.ContentUnavailable);
    }

    [Fact]
    public void HashingTheSameBytesTwice_GivesTheSameDigest()
    {
        var first = AnalyzeRemittance().Evidence.Where(e => e.SignalId == MimeSignals.AttachmentHash)
            .Select(e => e.Attribute("hash")).ToList();
        var second = AnalyzeRemittance().Evidence.Where(e => e.SignalId == MimeSignals.AttachmentHash)
            .Select(e => e.Attribute("hash")).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void EncryptedAttachment_IsUnavailableAndReducesCoverage()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "encrypted-smime.eml",
            mailFrom: "sender@example-secure.test"));

        Assert.Equal(1.0, result.Signal(MimeSignals.ContentEncrypted).Value);
        Assert.True(result.Coverage.ContentEncrypted);
        Assert.False(result.Coverage.BodyParsed);

        var unavailable = result.Signal(MimeSignals.AttachmentUnavailable);
        Assert.Equal("1", unavailable.Attribute("unavailableCount"));

        var attachment = result.Message!.Attachments.Single();

        // Unavailable means the content cannot be read, which is different from the bytes being
        // unreadable: a digest of the ciphertext is still a valid grouping key.
        Assert.True(attachment.ContentUnavailable);
        Assert.NotNull(attachment.ContentHash);
    }

    [Fact]
    public void EncryptedContent_IsRecordedAsReducedCoverageNotAsACleanVerdict()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request(
            "encrypted-smime.eml",
            mailFrom: "sender@example-secure.test"));

        var coverage = result.Signal(MimeSignals.AnalysisCoverage);
        Assert.Equal(EvidenceAvailability.ReducedCoverage, coverage.Availability);
        Assert.Contains("encrypted-content", coverage.AttributesNamed("reduced"));
    }

    [Fact]
    public void AnUninformativeDeclaredType_IsRecordedButNotCountedAsAMismatch()
    {
        // application/octet-stream says nothing, so it cannot be contradicted. Counting it would
        // produce a mismatch on a large share of ordinary business mail.
        var bytes = SyntheticMessage.Utf8(
            """
            From: Sender <sender@example.com>
            To: bob@example.org
            Subject: Synthetic
            Date: Mon, 22 Sep 2026 09:15:00 +0100
            Message-ID: <synth-att@example.com>
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain; charset=utf-8

            see attached

            --b
            Content-Type: application/octet-stream; name="report.pdf"
            Content-Disposition: attachment; filename="report.pdf"
            Content-Transfer-Encoding: base64

            JVBERi0xLjQK

            --b--
            """);

        var result = Analyzer.Analyze(FixtureMessage.FromBytes(bytes));

        var signal = result.Signal(MimeSignals.AttachmentTypeMismatch);
        Assert.Equal(0.0, signal.Value);
        Assert.Equal("1", signal.Attribute("genericDeclaredTypeCount"));
    }

    [Fact]
    public void MessageWithNoAttachments_ReportsNotApplicableNotNull()
    {
        var result = Analyzer.Analyze(FixtureMessage.Request("benign-plain.eml", mailFrom: "alice@example.com"));

        Assert.Equal(
            EvidenceAvailability.NotApplicable,
            result.Signal(MimeSignals.AttachmentTypeMismatch).Availability);
        Assert.False(result.Coverage.HasAttachments);
    }
}
