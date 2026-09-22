using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StyloMail.Core;

namespace StyloMail.Assessment.Semantic;

/// <summary>
/// A digest over the parts of a message that change what it <em>does</em>, as distinct from how
/// it reads.
/// </summary>
/// <remarks>
/// <b>This exists to stop a near-duplicate from being smoothed into a reuse.</b> Two messages can
/// be nearly identical in wording and completely different in effect: a campaign that reuses a
/// known-good template while swapping in a new bank account is semantically similar and
/// operationally new. Any reuse gate that compares presentation — body text, subject, semantic
/// dimension vector — will consider those the same message, which is exactly the evasion it is
/// supposed to catch.
///
/// <para>
/// So the security-bearing properties are collected separately and compared for <em>exact</em>
/// agreement: where links actually point, what attachments actually are, which sender context
/// actually carried the message, and which payment destinations it actually names. Two messages
/// whose fingerprints differ are not the same message no matter how alike they read, and the
/// pipeline answers them independently.
/// </para>
///
/// <para>
/// Only the digest is retained. The components can contain addresses, account numbers and URLs,
/// and a cache is not a place for any of them — the digest is enough to answer "identical?" and
/// carries nothing readable.
/// </para>
/// </remarks>
public sealed record SecurityBearingFingerprint
{
    /// <summary>Lowercase hexadecimal SHA-256 over the ordered security-bearing components.</summary>
    public required string Digest { get; init; }

    /// <summary>How many components went into the digest. A fingerprint over nothing is not agreement.</summary>
    public required int ComponentCount { get; init; }

    /// <summary>
    /// Computes the fingerprint for one message.
    /// </summary>
    /// <remarks>
    /// Link and attachment order is preserved, and every occurrence is kept rather than
    /// deduplicated: a message that lists two destinations and one that lists the same destination
    /// twice are different messages, and collapsing them would make the second look like the first.
    /// </remarks>
    public static SecurityBearingFingerprint Compute(MailAnalysisInput message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var builder = new StringBuilder();
        var components = 0;

        void Add(string label, string? value)
        {
            components++;
            builder.Append(label).Append('=');

            if (value is null)
            {
                builder.Append("-;\n");
                return;
            }

            builder.Append(value.Length).Append(':').Append(value).Append(";\n");
        }

        // Where links actually point. This is the whole point: displayed text is presentation,
        // the target is the instruction.
        foreach (var link in message.Links)
        {
            Add("link.target", NormalizeTarget(link.ActualTarget));
        }

        // What attachments actually are, by content hash where one was computed and by name
        // otherwise — a rename with unchanged bytes is the same payload.
        foreach (var attachment in message.Attachments)
        {
            Add("attachment.hash", attachment.ContentHash);
            Add("attachment.name", attachment.FileName.Trim().ToLowerInvariant());
        }

        // Sender context: which identity carried this, and how it authenticated. A message that
        // reads identically but arrives under a different authenticated principal is a different
        // event, and treating it as a duplicate would let one principal inherit another's history.
        Add("sender.mail_from", NormalizeAddress(message.Envelope.MailFrom));
        Add("sender.authenticated_account", NormalizeAddress(message.Authentication.AuthenticatedAccount));

        foreach (var result in message.Authentication.Results.Where(r => r.FromTrustedVerifier))
        {
            Add("sender.auth", $"{result.Mechanism.ToLowerInvariant()}={result.Result.ToLowerInvariant()}");
        }

        // Payment destinations the body names. Extracted deterministically — no network, no
        // resolution — because a changed account number is the canonical example of an identical
        // template with a different effect.
        foreach (var identifier in PaymentIdentifiers.Extract(message.BodyText))
        {
            Add("payment.identifier", identifier);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return new SecurityBearingFingerprint
        {
            Digest = Convert.ToHexStringLower(digest),
            ComponentCount = components,
        };
    }

    /// <summary>
    /// Normalises a link target enough to compare two copies of the same link without erasing the
    /// differences that matter.
    /// </summary>
    /// <remarks>
    /// Scheme and host are case-insensitive and are folded; path, query and fragment are not,
    /// because <c>?account=1</c> and <c>?account=2</c> are different destinations. Redirects are
    /// never followed — resolving a target means touching an attacker-chosen host from inside the
    /// trust boundary, which the spec forbids in the MVP.
    /// </remarks>
    internal static string NormalizeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        var trimmed = target.Trim();

        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return trimmed;
        }

        var scheme = trimmed[..schemeEnd].ToLowerInvariant();
        var rest = trimmed[(schemeEnd + 3)..];
        var pathStart = rest.IndexOfAny(['/', '?', '#']);
        var host = pathStart < 0 ? rest : rest[..pathStart];
        var path = pathStart < 0 ? string.Empty : rest[pathStart..];

        return $"{scheme}://{host.ToLowerInvariant()}{path}";
    }

    /// <summary>Folds case and surrounding whitespace, and nothing else.</summary>
    /// <remarks>
    /// Plus-addressing and dot-folding are deliberately not applied. They are provider-specific,
    /// and guessing wrong would merge two genuinely different senders into one — turning a
    /// security-bearing difference into an apparent match.
    /// </remarks>
    internal static string NormalizeAddress(string? address) =>
        string.IsNullOrWhiteSpace(address) ? string.Empty : address.Trim().ToLowerInvariant();
}

/// <summary>
/// Deterministic extraction of payment-shaped identifiers from body text.
/// </summary>
/// <remarks>
/// This is a <em>comparison</em> aid, not a detector: it exists so that two messages naming
/// different destinations produce different fingerprints, and nothing here decides anything about
/// a message. It never fetches, resolves or validates — an IBAN checksum would tell us nothing we
/// need, and the extractor's only job is to be stable.
///
/// <para>
/// Bounded on both dimensions: the body examined is capped and the number of identifiers returned
/// is capped, so a message crafted to make this scan expensive cannot make it expensive.
/// </para>
/// </remarks>
public static class PaymentIdentifiers
{
    /// <summary>Characters of body text scanned. Beyond this the message is not compared for payment changes.</summary>
    public const int MaxScanCharacters = 200_000;

    /// <summary>Maximum identifiers extracted per message.</summary>
    public const int MaxIdentifiers = 32;

    /// <summary>Shortest digit run treated as an account-shaped identifier.</summary>
    public const int MinimumDigitRun = 8;

    // IBAN: two country letters, two check digits, then 10-30 alphanumerics. Anchored on word
    // boundaries so it cannot match the middle of a longer token.
    private static readonly Regex Iban = new(
        @"\b[A-Z]{2}\d{2}[A-Z0-9]{10,30}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Long digit runs, optionally grouped: account numbers, sort codes, card numbers, routing
    // numbers. Separators are stripped before comparison so "1234 5678" and "12345678" agree.
    private static readonly Regex DigitRun = new(
        @"(?<!\d)(?:\d[\s-]?){8,}(?<=\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Payment-shaped identifiers found in the text, uppercased and separator-free, in order of
    /// appearance and without duplicates.
    /// </summary>
    public static IReadOnlyList<string> Extract(string? bodyText)
    {
        if (string.IsNullOrEmpty(bodyText))
        {
            return [];
        }

        var text = bodyText.Length > MaxScanCharacters ? bodyText[..MaxScanCharacters] : bodyText;
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in Iban.Matches(text))
        {
            if (Add(match.Value.ToUpperInvariant()))
            {
                return found;
            }
        }

        foreach (Match match in DigitRun.Matches(text))
        {
            var digits = new string([.. match.Value.Where(char.IsAsciiDigit)]);

            // The digit-run pattern is a superset of an IBAN's tail, so a run that is only a
            // fragment of an identifier already recorded must not be recorded again — otherwise
            // one changed digit would look like two changed identifiers.
            if (digits.Length < MinimumDigitRun || seen.Contains(digits))
            {
                continue;
            }

            if (Add(digits))
            {
                return found;
            }
        }

        return found;

        bool Add(string identifier)
        {
            if (!seen.Add(identifier))
            {
                return false;
            }

            found.Add(identifier);
            return found.Count >= MaxIdentifiers;
        }
    }
}
