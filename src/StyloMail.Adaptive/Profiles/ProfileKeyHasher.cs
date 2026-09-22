using System.Security.Cryptography;
using System.Text;

namespace StyloMail.Adaptive.Profiles;

/// <summary>
/// Turns an identity into the tenant-scoped pseudonym used as a profile key.
/// </summary>
/// <remarks>
/// Email addresses are personal data, so profiles are keyed on a keyed hash rather than the
/// address itself. The tenant is mixed into the key derivation, which means the same address
/// pseudonymizes differently in every tenant: a profile row that leaks from one tenant cannot
/// be matched against another, and operators cannot correlate correspondents across tenants.
///
/// <para>
/// This is pseudonymization, not anonymization. Anyone holding the master key can confirm a
/// guessed address, which is exactly why the key is a secret and why rotation is a deliberate,
/// documented operation rather than a routine one: rotating changes every pseudonym, and the
/// old profiles become unreachable rather than aliased.
/// </para>
/// </remarks>
public sealed class ProfileKeyHasher
{
    /// <summary>Minimum master key length. A short key is brute-forceable, and a brute-forced key undoes the pseudonym.</summary>
    public const int MinimumKeyBytes = 32;

    private readonly byte[] _masterKey;

    public ProfileKeyHasher(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length < MinimumKeyBytes)
        {
            throw new ArgumentException(
                $"The master key must be at least {MinimumKeyBytes} bytes; got {masterKey.Length}.",
                nameof(masterKey));
        }

        _masterKey = masterKey.ToArray();
    }

    /// <summary>
    /// The profile key for <paramref name="identity"/> within <paramref name="tenantId"/>.
    /// </summary>
    /// <remarks>
    /// Identity is normalised before hashing. Case and surrounding whitespace would otherwise
    /// split one correspondent into several profiles: which is both a correctness problem and
    /// a small evasion primitive, since padding a display address is trivial.
    /// </remarks>
    public string Hash(string tenantId, string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        var tenantKey = HMACSHA256.HashData(_masterKey, Encoding.UTF8.GetBytes(tenantId));
        var normalised = identity.Trim().ToLowerInvariant();

        var digest = HMACSHA256.HashData(tenantKey, Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(digest);
    }
}
