using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StyloMail.AccessProxy.Credentials;

/// <summary>Supplies the encryption keys a credential store encrypts under, by key id.</summary>
/// <remarks>
/// A ring rather than a single key, because spec §9.4 requires a documented key-rotation procedure
/// and rotation is impossible without the previous key remaining available long enough to re-wrap
/// what it protected. The abstraction is also where a real deployment would put a KMS or an
/// OS keystore; the in-memory implementation in this project exists so tests and the host can wire
/// something, and is explicitly not a production key store.
/// </remarks>
public interface ISecretKeyRing
{
    /// <summary>The key new credentials are encrypted under.</summary>
    string CurrentKeyId { get; }

    /// <summary>
    /// The key for <paramref name="keyId"/>, or null when it is unknown or retired.
    /// </summary>
    /// <remarks>
    /// Returning null, rather than throwing, is deliberate: an unknown key id means the
    /// credential cannot be decrypted, which must surface as a fail-closed authentication failure,
    /// not an unhandled crash that a retry loop might hammer.
    ///
    /// <para>
    /// <b>The returned array is borrowed, not owned.</b> The caller must not modify it and must not
    /// clear it. Ownership of key material stays with the ring, and this is not a formality: an
    /// implementation of <see cref="ISecretProtector"/> that cleared the key it was handed would
    /// blank the ring's only copy, so the key would work for exactly one operation and every
    /// credential protected or read afterwards would silently be encrypted under zeros. Returning
    /// null rather than an empty array is also the honest signal here, since an empty key is a
    /// valid-looking value that no caller could distinguish from a real one.
    /// </para>
    /// </remarks>
    byte[]? GetKey(string keyId);
}

/// <summary>Encrypts and decrypts credential material for storage.</summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>, binding it to <paramref name="binding"/>.</summary>
    ProtectedSecret Protect(SecretValue plaintext, in SecretBinding binding);

    /// <summary>
    /// Decrypts <paramref name="protectedSecret"/>, requiring <paramref name="binding"/> to match
    /// the one used at encryption time. Throws <see cref="CredentialUnavailableException"/> when the
    /// ciphertext is not authentic under this binding.
    /// </summary>
    SecretValue Unprotect(byte[] protectedSecret, string protectionKeyId, in SecretBinding binding);
}

/// <summary>
/// What a credential's ciphertext is bound to.
/// </summary>
/// <remarks>
/// Bound as AES-GCM additional authenticated data, so the ciphertext is only decryptable in the
/// exact context it was encrypted for. Three attacks this closes, all of which a plain
/// encrypt-the-blob store leaves open:
/// <list type="number">
/// <item>
/// <b>Re-labelling the discriminator.</b> Swapping <c>oauth-refresh-token</c> for
/// <c>app-password</c> on a stored record would otherwise hand a refresh token to the app-password
/// provider, which would happily send it as a password. With the discriminator authenticated, that
/// edit makes the credential undecryptable instead.
/// </item>
/// <item><b>Cross-tenant replay.</b> Copying a ciphertext into another tenant's row fails.</item>
/// <item><b>Cross-account replay.</b> Copying it between accounts within a tenant fails.</item>
/// </list>
/// </remarks>
public readonly record struct SecretBinding(string TenantId, string AccountId, string Discriminator)
{
    internal byte[] ToAad()
    {
        // Length-prefixed so that ("a", "bc", "d") cannot collide with ("ab", "c", "d").
        var parts = new[] { TenantId, AccountId, Discriminator };
        var total = 0;
        foreach (var part in parts)
        {
            total += 4 + Encoding.UTF8.GetByteCount(part);
        }

        var aad = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            var written = Encoding.UTF8.GetBytes(part, aad.AsSpan(offset + 4));
            BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(offset), written);
            offset += 4 + written;
        }

        return aad;
    }
}

/// <summary>
/// AES-256-GCM credential protection with an explicit, versioned blob format.
/// </summary>
/// <remarks>
/// <para>
/// The blob layout is <c>[1 byte version][12 byte nonce][16 byte tag][ciphertext]</c>. The version
/// byte is not ceremony: it is what makes a later algorithm change a migration rather than a
/// flag day, and it is the first thing a future reader will look for. The key id is kept on the
/// record rather than in the blob so that a key rotation can see which credentials need re-wrapping
/// without attempting a decrypt.
/// </para>
/// <para>
/// Encryption at rest is necessary and not sufficient (spec §9.4 item 2). This type is the
/// "necessary" half, the isolation, rotation and breach-path half is a deployment concern and is
/// reported as not implemented rather than quietly assumed.
/// </para>
/// </remarks>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const byte FormatVersion = 1;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int HeaderBytes = 1 + NonceBytes + TagBytes;

    private readonly ISecretKeyRing _keyRing;

    public AesGcmSecretProtector(ISecretKeyRing keyRing)
        => _keyRing = keyRing ?? throw new ArgumentNullException(nameof(keyRing));

    public ProtectedSecret Protect(SecretValue plaintext, in SecretBinding binding)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var keyId = _keyRing.CurrentKeyId;
        var key = _keyRing.GetKey(keyId)
            ?? throw new CredentialUnavailableException(
                $"Key ring has no key '{keyId}', which it just reported as current.");

        var blob = new byte[HeaderBytes + plaintext.Length];
        blob[0] = FormatVersion;
        var nonce = blob.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        var tag = blob.AsSpan(1 + NonceBytes, TagBytes);
        var ciphertext = blob.AsSpan(HeaderBytes);

        // The key is borrowed from the ring, not owned here. Clearing it would blank the ring's only
        // copy, so the key would work for exactly one operation and every credential protected or
        // read afterwards would silently use zeros. Ownership of key material stays with the ring.
        using var aes = new AesGcm(key, TagBytes);
        var aad = binding.ToAad();
        aes.Encrypt(nonce, plaintext.Utf8, ciphertext, tag, aad);

        return new ProtectedSecret(blob, keyId);
    }

    public SecretValue Unprotect(byte[] protectedSecret, string protectionKeyId, in SecretBinding binding)
    {
        ArgumentNullException.ThrowIfNull(protectedSecret);
        ArgumentException.ThrowIfNullOrEmpty(protectionKeyId);

        if (protectedSecret.Length < HeaderBytes)
        {
            throw new CredentialUnavailableException("Stored credential blob is truncated.");
        }

        if (protectedSecret[0] != FormatVersion)
        {
            // Named without describing the credential: a format the running build cannot read is a
            // fail-closed condition, not something to guess at.
            throw new CredentialUnavailableException(
                $"Stored credential blob has unsupported format version {protectedSecret[0]}.");
        }

        var key = _keyRing.GetKey(protectionKeyId)
            ?? throw new CredentialUnavailableException(
                $"Stored credential was encrypted under key '{protectionKeyId}', which is not in the key ring.");

        var nonce = protectedSecret.AsSpan(1, NonceBytes);
        var tag = protectedSecret.AsSpan(1 + NonceBytes, TagBytes);
        var ciphertext = protectedSecret.AsSpan(HeaderBytes);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            var aad = binding.ToAad();
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            // The binding mismatch case lands here, and it is a security event worth naming as one.
            // The message carries no credential material and no key material.
            throw new CredentialUnavailableException(
                "Stored credential failed authentication. It was encrypted for a different tenant, " +
                "account or credential kind, or has been tampered with.", ex);
        }

        // The key is borrowed, not owned, see Protect. Zeroing it here would blank the ring.
        return SecretValue.FromBytes(plaintext);
    }
}

/// <summary>A protected credential blob and the key id it was encrypted under.</summary>
public readonly record struct ProtectedSecret(byte[] Ciphertext, string KeyId)
{
    /// <summary>Redacts the ciphertext. See <see cref="BackendCredentialRecord.ToString"/>.</summary>
    public override string ToString() => $"{nameof(ProtectedSecret)} {{ KeyId = {KeyId}, Ciphertext = [redacted] }}";
}

/// <summary>
/// An in-memory key ring.
/// </summary>
/// <remarks>
/// <b>Not a production key store.</b> Keys live in process memory, so they die with the process and
/// are not protected by a KMS, an HSM or the OS keystore, which means this cannot satisfy the
/// "per-tenant key isolation" half of spec §9.4 item 2 on its own. It is here so the store and the
/// protector are exercised by tests and so the host has something to inject; the deployment step of
/// replacing it is reported as outstanding rather than assumed done.
/// </remarks>
public sealed class InMemorySecretKeyRing : ISecretKeyRing
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public InMemorySecretKeyRing(string currentKeyId, byte[] currentKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentKeyId);
        ArgumentNullException.ThrowIfNull(currentKey);
        if (currentKey.Length != 32)
        {
            throw new ArgumentException("AES-256 requires a 32-byte key.", nameof(currentKey));
        }

        CurrentKeyId = currentKeyId;
        _keys[currentKeyId] = currentKey;
    }

    public string CurrentKeyId { get; }

    /// <summary>Generates a key ring with a random key. For tests and local runs.</summary>
    public static InMemorySecretKeyRing CreateRandom(string currentKeyId = "local-1")
        => new(currentKeyId, RandomNumberGenerator.GetBytes(32));

    /// <summary>Registers an additional, older key so credentials under it remain decryptable.</summary>
    public InMemorySecretKeyRing WithRetiredKey(string keyId, byte[] key)
    {
        _keys[keyId] = key;
        return this;
    }

    public byte[]? GetKey(string keyId) => _keys.TryGetValue(keyId, out var key) ? key : null;
}

/// <summary>
/// A credential cannot be produced: revoked, undecryptable, unknown kind, or the provider refused
/// to issue one.
/// </summary>
/// <remarks>
/// <b>This is a terminal, fail-closed condition.</b> Spec §9.5 risk 4 requires that a revoked
/// credential surface as an authentication failure and never become a silent retry loop against
/// Google. Nothing in the proxy retries on this exception: it is thrown once, the session is
/// refused, and the client is told the session could not be authenticated. Retrying a revoked
/// credential is both useless and indistinguishable from a credential-stuffing pattern to the
/// provider, which is a good way to get an account locked.
///
/// <para>
/// Messages on this exception never contain credential material. They name the failure and the
/// opaque key id, which is what an operator needs to act and is not itself a secret.
/// </para>
/// </remarks>
public sealed class CredentialUnavailableException : Exception
{
    public CredentialUnavailableException(string message)
        : base(message)
    {
    }

    public CredentialUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
