using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace StyloMail.Host.Auth;

/// <summary>
/// How a minted API key is shaped, how its digest is derived, and how a presented key is read.
/// </summary>
/// <remarks>
/// <para>
/// <b>A minted value is <c>smk_&lt;key id&gt;_&lt;secret&gt;</c>.</b> The key id is random, public and
/// not a secret; the secret is 32 bytes from a cryptographic RNG. The id is what makes a slow KDF
/// affordable: a presented key names its own row, so authentication costs <em>one</em> derivation
/// rather than one per stored principal, and no fast digest of the secret is written down anywhere.
/// A store that held a plain SHA-256 to make lookup cheap would be offline-crackable, which is the
/// defect this shape exists to avoid rather than to work around.
/// </para>
/// <para>
/// <b>The id is hex and must stay hex.</b> <see cref="TryReadKeyId"/> finds the boundary at the first
/// underscore after the scheme, so an id whose alphabet contained an underscore would be read as
/// only the part before it and every such key would silently fail to resolve. This is why the id is
/// not base64url, whose alphabet does contain one; the secret is base64url and may, because nothing
/// parses past the id.
/// </para>
/// <para>
/// <b>A credential with structure is not a weaker credential.</b> The secret carries every bit of the
/// entropy; the prefix and the id are published by design. What they buy is that the expensive part
/// of verification is reached only by a caller who already knows which row to attack, so an
/// unauthenticated flood of garbage keys costs a lookup miss and no hashing at all.
/// </para>
/// </remarks>
public static class MintedApiKey
{
    /// <summary>Distinguishes a minted key from anything else a deployment might use as one.</summary>
    public const string Scheme = "smk";

    /// <summary>
    /// The KDF recorded in every minted row.
    /// </summary>
    /// <remarks>
    /// <b>A slow, iterated KDF, not a bare hash.</b> A single SHA-256 of an API key can be attacked
    /// offline at billions of guesses a second by anyone who copies the store file, which makes a
    /// store of SHA-256 digests a plaintext store with extra steps. The parameters are recorded per
    /// row rather than assumed, so raising the cost later is a re-mint and never a break, and a row
    /// written by an older build still verifies.
    /// </remarks>
    public const string Algorithm = "pbkdf2-sha256";

    /// <summary>
    /// Iterations for a newly minted key.
    /// </summary>
    /// <remarks>
    /// <b>600,000, measured rather than chosen by habit: 71 ms per derivation on this machine.</b>
    /// That cost is paid once per minted key per authentication, and the resolution cache in
    /// <see cref="PrincipalDirectory"/> is what keeps it off the ordinary request path. It is paid in
    /// full by a caller presenting a key whose id matches a row and whose secret does not, which is
    /// the intended shape: guessing is meant to be expensive, and reading a key you hold is not.
    /// </remarks>
    public const int Iterations = 600_000;

    private const int KeyIdBytes = 16;
    private const int SecretBytes = 32;
    private const int SaltBytes = 16;
    private const int DigestBytes = 32;

    /// <summary>A freshly minted key: the value to show once, and the material to store.</summary>
    public sealed record Material
    {
        /// <summary>The public lookup handle. Not a secret.</summary>
        public required string KeyId { get; init; }

        /// <summary>Carried on the material rather than read from <see cref="MintedApiKey"/> at the call site.</summary>
        /// <remarks>
        /// A mint and its row record the same algorithm by construction, so a deployment that minted
        /// keys under one parameterisation and now runs a build defaulting to another cannot write a
        /// row whose digest was derived under neither.
        /// </remarks>
        public required string Algorithm { get; init; }

        /// <summary>
        /// The value the operator is shown exactly once.
        /// </summary>
        /// <remarks>Never stored, never logged, never recoverable. Only <see cref="Digest"/> is kept.</remarks>
        public required string Value { get; init; }

        /// <summary>Per-key salt, so two principals minted the same secret still differ in the store.</summary>
        public required byte[] Salt { get; init; }

        public required byte[] Digest { get; init; }

        public required int Iterations { get; init; }
    }

    public static Material Mint()
    {
        // Hex, not base64url, and that is load-bearing rather than stylistic. The id is separated
        // from the secret by an underscore, and base64url's alphabet contains an underscore: an id
        // encoded that way would contain the separator about 30% of the time, the parser would read
        // the id as everything before the first one, and that key would never resolve. A credential
        // that works most of the time is worse than one that never does, because it reads as a
        // flaky host.
        var keyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(KeyIdBytes)).ToLowerInvariant();
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));
        var value = Compose(keyId, secret);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);

        return new Material
        {
            KeyId = keyId,
            Algorithm = Algorithm,
            Value = value,
            Salt = salt,
            Digest = DeriveDigest(value, salt, Iterations),
            Iterations = Iterations,
        };
    }

    public static string Compose(string keyId, string secret) => $"{Scheme}_{keyId}_{secret}";

    /// <summary>
    /// Reads the row a presented key names, or returns false when it names none.
    /// </summary>
    /// <remarks>
    /// <b>Returns false rather than throwing on anything malformed.</b> This runs on the path taken
    /// by every unrecognised credential, so it must be total: a key that is not in this scheme is not
    /// an error, it is simply not a minted key, and the caller falls through to the other source.
    /// </remarks>
    /// <remarks>
    /// <b>The boundary is the first underscore after the scheme, so the id must not contain one.</b>
    /// The secret's alphabet does contain one and that is fine, because nothing is parsed past the
    /// id; the id's alphabet does not, and <see cref="Mint"/> is why. An id carrying the separator
    /// would be read as only the part before it, which is a lookup miss rather than an error, so the
    /// key would simply never resolve.
    /// </remarks>
    public static bool TryReadKeyId(string? presentedKey, out string keyId)
    {
        keyId = string.Empty;

        if (string.IsNullOrEmpty(presentedKey))
        {
            return false;
        }

        var prefix = Scheme + "_";
        if (!presentedKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = presentedKey.AsSpan(prefix.Length);
        var separator = rest.IndexOf('_');

        if (separator <= 0 || separator == rest.Length - 1)
        {
            return false;
        }

        // A bounded id keeps a hostile credential from turning this into an unbounded lookup, and
        // the bound is generous against the 32 characters a real one is.
        if (separator > 64)
        {
            return false;
        }

        keyId = rest[..separator].ToString();
        return true;
    }

    /// <summary>
    /// Derives the digest a stored row is compared against.
    /// </summary>
    /// <remarks>
    /// The whole presented value is the password, not the secret alone: the key id is part of the
    /// credential's identity, so a digest cannot be satisfied by a value carrying a different one.
    /// </remarks>
    public static byte[] DeriveDigest(string presentedKey, byte[] salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(presentedKey);
        ArgumentNullException.ThrowIfNull(salt);

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(presentedKey),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            DigestBytes);
    }

    /// <summary>
    /// Compares a derived digest against a stored one without leaking how much of it matched.
    /// </summary>
    /// <remarks>
    /// A length mismatch is answered rather than thrown: <see cref="CryptographicOperations"/>
    /// rejects unequal lengths, and a truncated or corrupted stored digest must read as "does not
    /// match" rather than as an exception out of the authentication path.
    /// </remarks>
    public static bool DigestMatches(byte[] stored, byte[] derived)
        => stored.Length == derived.Length && CryptographicOperations.FixedTimeEquals(stored, derived);
}
