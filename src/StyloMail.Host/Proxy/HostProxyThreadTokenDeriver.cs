using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using StyloMail.AccessProxy.Threading;

namespace StyloMail.Host.Proxy;

/// <summary>A token deriver for a deployment with no proxy key, which refuses every derivation.</summary>
/// <remarks>
/// Constructing it is safe; only using it fails. That keeps a disabled proxy from reading a key it
/// does not need while still making an accidental derivation fail closed.
/// </remarks>
public sealed class UnconfiguredThreadTokenDeriver : IThreadTokenDeriver
{
    public string Derive(string tenantId, string value)
        => throw new ProxyNotConfiguredException();
}

/// <summary>Builds the one thread-token deriver, or the refusing stand-in.</summary>
internal static class ProxyThreadTokens
{
    public static IThreadTokenDeriver Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostProxyOptions>>().Value;
        if (!options.Enabled)
        {
            return new UnconfiguredThreadTokenDeriver();
        }

        return new HmacProxyThreadTokenDeriver(
            services.GetRequiredService<IProxySecretSource>());
    }
}

/// <summary>
/// Derives the non-reversible message and thread tokens the warning tap stores.
/// </summary>
/// <remarks>
/// <para>
/// This is the only token derivation for proxied warnings, and it exists under the same
/// environment-only key as the login pseudonym rather than a new secret. A fixed domain-separation
/// prefix keeps a thread token from colliding with a login pseudonym for the same string.
/// </para>
/// <para>
/// Tenants are length-prefixed into the HMAC input, so the same Message-ID in two tenants produces
/// two unrelated tokens and the store cannot correlate conversations across tenants.
/// </para>
/// </remarks>
public sealed class HmacProxyThreadTokenDeriver : IThreadTokenDeriver, IDisposable
{
    private static readonly byte[] Domain = "stylomail-proxy-thread-token\0"u8.ToArray();
    private byte[]? _key;

    public HmacProxyThreadTokenDeriver(IProxySecretSource secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var key = secrets.GetLoginKey();
        if (key.Length < 32)
        {
            throw new ProxyConfigurationException("The proxy thread-token key must be at least 32 bytes.");
        }

        _key = key;
    }

    public string Derive(string tenantId, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(value);

        var key = _key ?? throw new ObjectDisposedException(nameof(HmacProxyThreadTokenDeriver));
        var tenant = Encoding.UTF8.GetBytes(tenantId);
        var payload = Encoding.UTF8.GetBytes(value);
        var input = new byte[Domain.Length + 8 + tenant.Length + payload.Length];

        try
        {
            Domain.CopyTo(input, 0);
            var offset = Domain.Length;
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(offset), tenant.Length);
            tenant.CopyTo(input, offset + 4);
            offset += 4 + tenant.Length;
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(offset), payload.Length);
            payload.CopyTo(input, offset + 4);

            using var hmac = new HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(input));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    public void Dispose()
    {
        if (_key is { } key)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        _key = null;
    }
}
