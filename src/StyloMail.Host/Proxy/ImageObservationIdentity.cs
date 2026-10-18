using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using StyloMail.Host.Hosting;

namespace StyloMail.Host.Proxy;

/// <summary>
/// Keyed, length-prefixed image observation identity derived from the tenant profile key.
/// </summary>
/// <remarks>
/// The content fingerprint is an HMAC over the tenant and the complete raw message literal, never a
/// plain content hash. Only the keyed value is persisted; the raw digest never is. The tenant is
/// length-prefixed into every input so two tenants cannot correlate equal content.
/// </remarks>
public sealed class ImageObservationIdentity(IAssessmentSecretSource secrets)
{
    private static readonly byte[] Domain = "stylomail-image-observation-key\0"u8.ToArray();

    public bool TryCreate(
        string tenantId,
        string accountId,
        string direction,
        string messageToken,
        ReadOnlySpan<byte> rawMessage,
        out ImageObservationKey key)
    {
        key = null!;
        var material = secrets.Read().ProfileMasterKey;
        if (string.IsNullOrWhiteSpace(material))
        {
            return false;
        }

        var master = Encoding.UTF8.GetBytes(material);
        if (master.Length < 32)
        {
            CryptographicOperations.ZeroMemory(master);
            return false;
        }

        try
        {
            var tenant = Encoding.UTF8.GetBytes(tenantId);
            var account = Encoding.UTF8.GetBytes(accountId);
            var directionBytes = Encoding.UTF8.GetBytes(direction);
            var token = Encoding.UTF8.GetBytes(messageToken);

            byte[] contentFingerprint;
            using (var contentHash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, master))
            {
                Append(contentHash, tenant);
                contentHash.AppendData(rawMessage);
                contentFingerprint = contentHash.GetHashAndReset();
            }

            byte[] observationKey;
            using (var identityHash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, master))
            {
                Append(identityHash, Domain);
                Append(identityHash, tenant);
                Append(identityHash, account);
                Append(identityHash, directionBytes);
                Append(identityHash, token);
                Append(identityHash, contentFingerprint);
                observationKey = identityHash.GetHashAndReset();
            }

            key = new ImageObservationKey(
                Convert.ToHexStringLower(observationKey),
                Convert.ToHexStringLower(contentFingerprint),
                (byte[])master.Clone());

            CryptographicOperations.ZeroMemory(contentFingerprint);
            CryptographicOperations.ZeroMemory(observationKey);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    /// <summary>Keyed digest of the canonical result; equal means an exact-repeat no-op.</summary>
    public static string ResultDigest(ImageObservationKey key, ReadOnlySpan<byte> canonical)
    {
        ArgumentNullException.ThrowIfNull(key);
        var digest = HMACSHA256.HashData(key.KeyMaterial, canonical);
        return Convert.ToHexStringLower(digest);
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> part)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, part.Length);
        hash.AppendData(length);
        hash.AppendData(part);
    }
}

/// <summary>The derived observation key, fingerprint and tenant-key material for one analysis.</summary>
public sealed class ImageObservationKey : IDisposable
{
    private byte[]? _keyMaterial;

    internal ImageObservationKey(string observationKey, string contentFingerprint, byte[] keyMaterial)
    {
        ObservationKey = observationKey;
        ContentFingerprint = contentFingerprint;
        _keyMaterial = keyMaterial;
    }

    public string ObservationKey { get; }

    public string ContentFingerprint { get; }

    internal byte[] KeyMaterial =>
        _keyMaterial ?? throw new ObjectDisposedException(nameof(ImageObservationKey));

    public void Dispose()
    {
        if (_keyMaterial is { } key)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        _keyMaterial = null;
    }
}
