using System.Text;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Threading;
using StyloMail.Host.Proxy;
using StyloMail.Mime;

namespace StyloMail.Host.Tests;

/// <summary>
/// The Host warning analyser: one MIME parser, access's extractor for tokens, and the four closed
/// marker kinds. A clean measured message must be distinguishable from an unparsed one.
/// </summary>
public sealed class ProxyWarningAnalyzerTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 17, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_clean_message_is_available_with_zero_findings()
    {
        var (analyzer, extractor) = Create();

        var result = await analyzer.AnalyzeAsync(Input(Message(
            "Hello Bob.\r\n",
            "From: Alice <alice@example.com>",
            "To: Bob <bob@example.net>",
            "Subject: hello",
            "Message-ID: <m1@example.com>",
            "Date: Sat, 17 Oct 2026 20:00:00 +0000")), CancellationToken.None);

        Assert.Equal(ProxyWarningAvailability.Available, result.Availability);
        Assert.Equal(ProxyWarningAvailabilityReason.None, result.AvailabilityReason);
        Assert.Empty(result.Findings);

        // One token rule: the tokens are the extractor's output for this message.
        var expected = extractor.Extract("acme", "pa_account", HeaderBlock(Message(
            "Hello Bob.\r\n",
            "From: Alice <alice@example.com>",
            "To: Bob <bob@example.net>",
            "Subject: hello",
            "Message-ID: <m1@example.com>",
            "Date: Sat, 17 Oct 2026 20:00:00 +0000")), At);
        Assert.Equal(expected.MessageToken, result.MessageToken);
        Assert.Equal(expected.ThreadToken, result.ThreadToken);
    }

    [Fact]
    public async Task No_analyser_is_not_produced_with_the_named_reason_and_real_tokens()
    {
        var (_, extractor) = Create();
        var analyzer = new HostProxyWarningAnalyzer(null, extractor, TimeProvider.System);
        var bytes = Message(
            "Hello.\r\n",
            "From: Alice <alice@example.com>",
            "Message-ID: <m2@example.com>");

        var result = await analyzer.AnalyzeAsync(Input(bytes), CancellationToken.None);

        Assert.Equal(ProxyWarningAvailability.NotProduced, result.Availability);
        Assert.Equal(ProxyWarningAvailabilityReason.NoAnalyserConfigured, result.AvailabilityReason);
        Assert.Empty(result.Findings);
        Assert.False(string.IsNullOrEmpty(result.MessageToken));
        Assert.False(string.IsNullOrEmpty(result.ThreadToken));
    }

    [Fact]
    public async Task An_unreadable_message_is_not_produced_with_its_own_reason()
    {
        var (analyzer, _) = Create();

        var result = await analyzer.AnalyzeAsync(
            Input(Encoding.UTF8.GetBytes("this is not a message at all")), CancellationToken.None);

        Assert.Equal(ProxyWarningAvailability.NotProduced, result.Availability);
        Assert.Equal(ProxyWarningAvailabilityReason.UnreadableMessage, result.AvailabilityReason);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task A_display_name_that_is_another_address_is_a_marker()
    {
        var (analyzer, _) = Create();

        var result = await analyzer.AnalyzeAsync(Input(Message(
            "Please pay.\r\n",
            "From: \"ceo@example.com\" <attacker@evil.example>",
            "To: Bob <bob@example.net>",
            "Subject: invoice",
            "Message-ID: <m3@example.com>")), CancellationToken.None);

        Assert.Equal(ProxyWarningAvailability.Available, result.Availability);
        Assert.Contains(result.Findings, finding => finding.MarkerKind == ProxyWarningMarkerKind.DisplayNameMismatch);

        // Finding ids are content-free.
        Assert.All(result.Findings, finding =>
        {
            Assert.DoesNotContain("evil.example", finding.FindingId, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ceo@example.com", finding.FindingId, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task An_attachment_whose_type_contradicts_its_name_is_a_marker()
    {
        var (analyzer, _) = Create();

        var message =
            "From: Alice <alice@example.com>\r\n" +
            "To: Bob <bob@example.net>\r\n" +
            "Subject: paperwork\r\n" +
            "Message-ID: <m4@example.com>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"b1\"\r\n" +
            "\r\n" +
            "--b1\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            "Attached.\r\n" +
            "--b1\r\n" +
            "Content-Type: application/x-msdownload; name=\"invoice.pdf\"\r\n" +
            "Content-Disposition: attachment; filename=\"invoice.pdf\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            "TVqQAAMAAAAEAAAA\r\n" +
            "--b1--\r\n";

        var result = await analyzer.AnalyzeAsync(
            Input(Encoding.UTF8.GetBytes(message)), CancellationToken.None);

        Assert.Equal(ProxyWarningAvailability.Available, result.Availability);
        Assert.Contains(result.Findings, finding => finding.MarkerKind == ProxyWarningMarkerKind.AttachmentTypeMismatch);
        Assert.All(result.Findings, finding =>
            Assert.DoesNotContain("invoice.pdf", finding.FindingId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_link_label_that_names_another_host_is_a_marker()
    {
        var (analyzer, _) = Create();

        var message =
            "From: Alice <alice@example.com>\r\n" +
            "To: Bob <bob@example.net>\r\n" +
            "Subject: your account\r\n" +
            "Message-ID: <m5@example.com>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "\r\n" +
            "<html><body><a href=\"https://evil.example/login\">https://paypal.example/account</a></body></html>\r\n";

        var result = await analyzer.AnalyzeAsync(
            Input(Encoding.UTF8.GetBytes(message)), CancellationToken.None);

        Assert.Equal(ProxyWarningAvailability.Available, result.Availability);
        Assert.Contains(result.Findings, finding => finding.MarkerKind == ProxyWarningMarkerKind.LinkDomainMismatch);
        Assert.All(result.Findings, finding =>
        {
            Assert.DoesNotContain("evil.example", finding.FindingId, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("paypal", finding.FindingId, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Analysis_is_reproducible_for_the_same_bytes()
    {
        var (analyzer, _) = Create();
        var bytes = Message(
            "Hello.\r\n",
            "From: Alice <alice@example.com>",
            "Message-ID: <m6@example.com>");

        var first = await analyzer.AnalyzeAsync(Input(bytes), CancellationToken.None);
        var second = await analyzer.AnalyzeAsync(Input(bytes), CancellationToken.None);

        Assert.Equal(first.MessageToken, second.MessageToken);
        Assert.Equal(first.ThreadToken, second.ThreadToken);
        Assert.Equal(first.Availability, second.Availability);
    }

    private static (HostProxyWarningAnalyzer Analyzer, ThreadingHeaderExtractor Extractor) Create()
    {
        var deriver = new HmacProxyThreadTokenDeriver(new FixtureSecretSource());
        var extractor = new ThreadingHeaderExtractor(deriver);
        var analyzer = new HostProxyWarningAnalyzer(
            new BoundedMimeMessageAnalyzer(), extractor, TimeProvider.System);
        return (analyzer, extractor);
    }

    private static ProxyWarningAnalysisInput Input(byte[] bytes) => new()
    {
        Message = bytes,
        TenantId = "acme",
        AccountId = "pa_account",
        ObservedAt = At,
    };

    private static byte[] Message(string body, params string[] headers)
        => Encoding.UTF8.GetBytes(string.Join("\r\n", headers) + "\r\n\r\n" + body);

    private static byte[] HeaderBlock(byte[] message)
    {
        var span = message.AsSpan();
        var end = span.IndexOf("\r\n\r\n"u8);
        return (end < 0 ? span : span[..end]).ToArray();
    }

    private sealed class FixtureSecretSource : IProxySecretSource
    {
        public byte[] GetLoginKey()
            => Encoding.UTF8.GetBytes("fixture-login-key-32-bytes-long!!");

        public byte[] GetProtectionKey()
            => Encoding.UTF8.GetBytes("fixture-protection-key-32-bytes!");
    }
}
