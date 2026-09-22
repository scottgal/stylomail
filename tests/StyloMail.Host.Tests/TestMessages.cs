using System.Text;

namespace StyloMail.Host.Tests;

/// <summary>Fixture messages and request bodies shared by the endpoint tests.</summary>
internal static class TestMessages
{
    public const string SampleMime =
        """
        From: "Sender" <sender@example.com>
        To: <recipient@example.com>
        Subject: Quarterly figures
        Message-ID: <abc123@example.com>
        Date: Mon, 21 Sep 2026 09:00:00 +0000
        Content-Type: text/plain; charset=utf-8

        Please review the attached quarterly figures before Thursday.
        """;

    public static string Base64(string mime) =>
        // CRLF is the on-the-wire line ending for SMTP; normalising here means tests do not each
        // have to care which way their string literal's newlines survived.
        Convert.ToBase64String(Encoding.UTF8.GetBytes(mime.Replace("\r\n", "\n").Replace("\n", "\r\n")));

    public static readonly string SampleMimeBase64 = Base64(SampleMime);

    public static object Request(
        string? tenantId = null,
        bool shadowMode = false,
        string rawMime = "",
        string mailFrom = "sender@example.com",
        string[]? rcptTo = null) => new
        {
            tenantId,
            direction = "Outbound",
            mailFrom,
            rcptTo = rcptTo ?? ["recipient@example.com"],
            rawMime = string.IsNullOrEmpty(rawMime) ? SampleMimeBase64 : rawMime,
            authenticatedAccount = "sender@example.com",
            approvedSenderIdentities = new[] { "sender@example.com" },
            shadowMode,
        };
}
