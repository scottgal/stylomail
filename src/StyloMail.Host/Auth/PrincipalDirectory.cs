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
