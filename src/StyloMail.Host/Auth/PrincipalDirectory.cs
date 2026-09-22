using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace StyloMail.Host.Auth;

/// <summary>
/// Resolves an API key to the principal it belongs to.
/// </summary>
/// <remarks>
/// Shared by every channel that accepts a key, so there is exactly one implementation of "is this
/// key valid and what may it do". Two channels each doing their own lookup is how a host ends up
/// with a cookie path that forgets one of the checks the header path makes.
/// </remarks>
public sealed class PrincipalDirectory
{
    private readonly HostAuthOptions _options;

    public PrincipalDirectory(IOptions<HostAuthOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>Whether any principal is configured at all.</summary>
    public bool HasPrincipals => _options.Principals.Any(p => !string.IsNullOrEmpty(p.Key));

    /// <summary>
    /// The principals configured for one tenant, ordered by identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Returns the configuration's view of a principal, never its credential.</b> Callers project
    /// what they need from <see cref="HostPrincipalOptions"/>; nothing here hands out a key, and a
    /// listing built from this must not either. The ordering is fixed so that a listing is stable
    /// across calls — an operator reading a sidebar should not see rows move because a dictionary
    /// enumeration changed.
    /// </para>
    /// <para>
    /// A principal configured without a key is skipped. It cannot authenticate, so it cannot send,
    /// and listing it as a sender would advertise an account that does not exist. An empty tenant
    /// returns an empty list rather than null: "this tenant has no senders" and "that question has
    /// no answer" must not look the same to a caller.
    /// </para>
    /// </remarks>
    public IReadOnlyList<HostPrincipalOptions> ForTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return
        [
            .. _options.Principals
                .Where(p => !string.IsNullOrEmpty(p.Key)
                    && string.Equals(p.TenantId, tenantId, StringComparison.Ordinal))
                .OrderBy(p => p.PrincipalId, StringComparer.Ordinal),
        ];
    }

    public bool BrowserChannelEnabled => _options.EnableBrowserCookieChannel;

    /// <summary>
    /// Finds the principal a key belongs to, or null.
    /// </summary>
    /// <remarks>
    /// Keys are compared by SHA-256 digest using a fixed-time comparison, and the loop runs to
    /// completion even after a match. Without both, the time taken to reject a key would say
    /// something about how much of it was correct and which configured entry it was closest to.
    /// </remarks>
    public HostPrincipalOptions? Resolve(string? presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return null;
        }

        var presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        HostPrincipalOptions? match = null;

        foreach (var candidate in _options.Principals)
        {
            if (string.IsNullOrEmpty(candidate.Key))
            {
                continue;
            }

            var candidateDigest = SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Key));

            // Deliberately not an early exit: the loop runs to completion either way.
            if (CryptographicOperations.FixedTimeEquals(presentedDigest, candidateDigest) && match is null)
            {
                match = candidate;
            }
        }

        return match;
    }
}
