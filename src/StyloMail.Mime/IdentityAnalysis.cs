using System.Text.RegularExpressions;
using MimeKit;
using static StyloMail.Mime.Attr;
using StyloMail.Core;

namespace StyloMail.Mime;

/// <summary>One address as it appeared in a header, with its display name kept alongside it.</summary>
internal sealed record AddressFact(string DisplayName, string Address, string Domain);

/// <summary>The identity comparisons that can be made without any baseline.</summary>
internal sealed record IdentityAnalysis
{
    public required IReadOnlyList<AddressFact> From { get; init; }

    public required IReadOnlyList<AddressFact> ReplyTo { get; init; }

    public required IReadOnlyList<AddressFact> Sender { get; init; }

    public required IReadOnlyList<AddressFact> ReturnPath { get; init; }

    /// <summary>Comparisons where the envelope and the headers name different identities.</summary>
    public required IReadOnlyList<EvidenceAttribute> EnvelopeMismatches { get; init; }

    /// <summary>Display names that assert an address or domain other than the one they are attached to.</summary>
    public required IReadOnlyList<EvidenceAttribute> DisplayNameMismatches { get; init; }

    /// <summary>Reply-To exists and names a different domain family from From.</summary>
    public required bool ReplyToDiverges { get; init; }
}

/// <summary>
/// Compares envelope identity against header identity, and display names against their addresses.
/// </summary>
/// <remarks>
/// <b>The envelope is not a hint; it is the only identity this system has.</b> A header <c>From</c>
/// is data the sender chose, so a disagreement between the two is recorded as a fact about the
/// message rather than resolved in either direction. The same applies to a display name: the name
/// "paypal.com Support" over the address <c>billing@evil.example</c> is an observation, and the
/// judgement about what it means belongs to policy.
/// </remarks>
internal static partial class IdentityInspector
{
    [GeneratedRegex(@"\b[a-z0-9\-]+(?:\.[a-z0-9\-]+)*\.[a-z]{2,24}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HostLike();

    [GeneratedRegex(@"^<?(?<addr>[^<>\s]+@[^<>\s]+)>?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BareAddress();

    public static IdentityAnalysis Analyse(MimeMessage message, MailEnvelope envelope)
    {
        var from = Facts(message.From);
        var replyTo = Facts(message.ReplyTo);
        var sender = Facts(message.Sender);
        var returnPath = Facts(message.Headers[HeaderId.ReturnPath]);

        var envelopeMismatches = new List<EvidenceAttribute>();
        var mailFrom = UrlTools.NormalizeAddress(NormalizeEnvelopeAddress(envelope.MailFrom));

        if (mailFrom.Length > 0)
        {
            AddMismatch("envelope-from-vs-header-from", mailFrom, from, envelopeMismatches);
            AddMismatch("envelope-from-vs-return-path", mailFrom, returnPath, envelopeMismatches);
        }

        var displayMismatches = new List<EvidenceAttribute>();
        CollectDisplayNameMismatches("from", from, displayMismatches);
        CollectDisplayNameMismatches("reply-to", replyTo, displayMismatches);
        CollectDisplayNameMismatches("sender", sender, displayMismatches);

        var replyToDiverges = false;
        if (from.Count > 0 && replyTo.Count > 0)
        {
            var fromFamily = UrlTools.DomainFamily(from[0].Domain);
            var replyFamily = UrlTools.DomainFamily(replyTo[0].Domain);
            replyToDiverges = fromFamily.Length > 0 && replyFamily.Length > 0 &&
                              !string.Equals(fromFamily, replyFamily, StringComparison.OrdinalIgnoreCase);
        }

        return new IdentityAnalysis
        {
            From = from,
            ReplyTo = replyTo,
            Sender = sender,
            ReturnPath = returnPath,
            EnvelopeMismatches = envelopeMismatches,
            DisplayNameMismatches = displayMismatches,
            ReplyToDiverges = replyToDiverges,
        };
    }

    private static void AddMismatch(
        string kind,
        string envelopeAddress,
        IReadOnlyList<AddressFact> headers,
        List<EvidenceAttribute> sink)
    {
        if (headers.Count == 0)
        {
            return;
        }

        var headerAddress = headers[0].Address;
        if (headerAddress.Length == 0)
        {
            return;
        }

        if (!string.Equals(envelopeAddress, headerAddress, StringComparison.OrdinalIgnoreCase))
        {
            sink.Add(Of(kind, $"{Redact(envelopeAddress)} vs {Redact(headerAddress)}"));
        }
    }

    private static void CollectDisplayNameMismatches(
        string field,
        IReadOnlyList<AddressFact> addresses,
        List<EvidenceAttribute> sink)
    {
        foreach (var fact in addresses)
        {
            if (fact.DisplayName.Length == 0 || fact.Address.Length == 0)
            {
                continue;
            }

            // The display name is itself an address that is not the one attached to it.
            var bare = BareAddress().Match(fact.DisplayName);
            if (bare.Success)
            {
                var claimed = bare.Groups["addr"].Value;
                if (!string.Equals(UrlTools.NormalizeAddress(claimed), fact.Address, StringComparison.Ordinal))
                {
                    sink.Add(Of($"{field}-name-is-address", $"{Redact(claimed)} vs {Redact(fact.Address)}"));
                    continue;
                }
            }

            // The display name asserts a domain different from the address's own.
            foreach (Match match in HostLike().Matches(fact.DisplayName))
            {
                var claimed = match.Value;
                var claimedFamily = UrlTools.DomainFamily(claimed);
                var actualFamily = UrlTools.DomainFamily(fact.Domain);
                if (claimedFamily.Length == 0 || actualFamily.Length == 0)
                {
                    continue;
                }

                if (!string.Equals(claimedFamily, actualFamily, StringComparison.OrdinalIgnoreCase))
                {
                    sink.Add(Of($"{field}-name-claims-domain", $"{claimed} vs {fact.Domain}"));
                    break;
                }
            }
        }
    }

    private static IReadOnlyList<AddressFact> Facts(InternetAddressList? list)
    {
        if (list is null || list.Count == 0)
        {
            return [];
        }

        var facts = new List<AddressFact>(list.Count);
        foreach (var address in list.Mailboxes)
        {
            facts.Add(Fact(address.Name, address.Address));
        }

        return facts;
    }

    private static IReadOnlyList<AddressFact> Facts(MailboxAddress? address) =>
        address is null ? [] : [Fact(address.Name, address.Address)];

    /// <summary>
    /// Records an address in a comparable form. The domain is normalised to its ASCII encoding so
    /// that an envelope naming the punycode form and a header naming the Unicode form are compared
    /// as the same identity rather than as a mismatch about encoding.
    /// </summary>
    private static AddressFact Fact(string? name, string? address)
    {
        var normalized = UrlTools.NormalizeAddress(address);
        var at = normalized.LastIndexOf('@');
        return new AddressFact(
            name ?? string.Empty,
            normalized,
            at >= 0 ? normalized[(at + 1)..] : string.Empty);
    }

    private static IReadOnlyList<AddressFact> Facts(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return [];
        }

        if (InternetAddressList.TryParse(headerValue, out var list))
        {
            return Facts(list);
        }

        var bare = BareAddress().Match(headerValue.Trim());
        if (bare.Success)
        {
            var address = bare.Groups["addr"].Value;
            var at = address.LastIndexOf('@');
            return [new AddressFact(string.Empty, address, at >= 0 ? address[(at + 1)..] : string.Empty)];
        }

        return [];
    }

    /// <summary>Strips the angle brackets and any display name from an SMTP <c>MAIL FROM</c> value.</summary>
    public static string NormalizeEnvelopeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('<') && trimmed.EndsWith('>'))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    /// <summary>
    /// Shortens an address for an evidence attribute. The ledger records that two identities
    /// disagreed; it does not need a full address book of them, and attribute values are visible
    /// to more consumers than the payload is.
    /// </summary>
    private static string Redact(string address)
    {
        var at = address.IndexOf('@');
        if (at <= 0)
        {
            return EvidenceBuilder.Truncate(address, 64);
        }

        var local = address[..at];
        var domain = address[(at + 1)..];
        var shown = local.Length <= 2 ? local : string.Concat(local.AsSpan(0, 2), "***");
        return EvidenceBuilder.Truncate($"{shown}@{domain}", 96);
    }
}
