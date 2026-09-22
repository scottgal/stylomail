namespace StyloMail.Transport.Ingress;

/// <summary>
/// The recipient domains this deployment accepts mail for.
/// </summary>
/// <remarks>
/// This is the answer to "are we the destination for this address?", and it is the whole of the
/// inbound authorisation model: an unauthenticated connection may inject mail <em>only</em> for a
/// domain listed here. Anything else is a third party trying to relay through us, which the spec
/// forbids outright.
///
/// <para>
/// <b>No domains means no inbound.</b> The empty policy refuses everything rather than allowing
/// everything — a missing configuration must not read as a universal open relay.
/// </para>
/// <para>
/// Matching is exact and case-insensitive, with no subdomain or suffix matching. A suffix match would
/// make <c>example.com</c> silently authorise <c>evil-example.com</c>, and an attacker only has to
/// register the neighbour.
/// </para>
/// </remarks>
public sealed class RecipientDomainPolicy
{
    private readonly HashSet<string> _domains;

    public RecipientDomainPolicy(IEnumerable<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);

        _domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var domain in domains)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                continue;
            }

            _domains.Add(domain.Trim().TrimStart('@').TrimEnd('.'));
        }
    }

    /// <summary>A policy that accepts nothing.</summary>
    public static RecipientDomainPolicy None { get; } = new([]);

    /// <summary>The configured domains, for the operator surface.</summary>
    public IReadOnlyCollection<string> Domains => _domains;

    /// <summary>True when this deployment accepts inbound mail at all.</summary>
    public bool IsConfigured => _domains.Count > 0;

    /// <summary>Whether <paramref name="recipient"/> is addressed to a domain we serve.</summary>
    public bool Allows(string recipient)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return false;
        }

        // Last '@' rather than first: a quoted local part may legally contain one, and taking the
        // first would read the domain out of the local part.
        var at = recipient.LastIndexOf('@');
        if (at < 0 || at == recipient.Length - 1)
        {
            return false;
        }

        var domain = recipient[(at + 1)..].TrimEnd('.');

        // A domain that still contains a delimiter is malformed; refusing is safer than matching a
        // prefix of it.
        return !domain.Contains('@', StringComparison.Ordinal) && _domains.Contains(domain);
    }
}
