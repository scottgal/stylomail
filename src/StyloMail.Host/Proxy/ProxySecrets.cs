using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Credentials;

namespace StyloMail.Host.Proxy;

/// <summary>Supplies the access proxy's two key materials.</summary>
/// <remarks>
/// An interface so a test can supply fixture keys without touching the process environment, and so
/// the environment-only rule is a property of one implementation rather than of every caller.
/// </remarks>
public interface IProxySecretSource
{
    /// <summary>The HMAC-SHA-256 key for tenant-keyed login pseudonyms. At least 32 bytes.</summary>
    byte[] GetLoginKey();

    /// <summary>The AES-256 key protecting backend credentials. Exactly 32 bytes.</summary>
    byte[] GetProtectionKey();
}

/// <summary>Reads both proxy keys from the process environment.</summary>
/// <remarks>
/// <b>There is no fallback.</b> A missing or short key throws rather than deriving one from a
/// default, a machine name or the database. An unkeyed login pseudonym is a plain hash of an email
/// address, and a default protection key means every deployment shares one; both look like working
/// software while providing none of the property they exist for.
/// </remarks>
public sealed class EnvironmentProxySecretSource : IProxySecretSource
{
    private readonly IOptions<HostProxyOptions> _options;

    public EnvironmentProxySecretSource(IOptions<HostProxyOptions> options) => _options = options;

    public byte[] GetLoginKey() => Read(_options.Value.LoginKeyEnvironmentVariable, exactly: null);

    public byte[] GetProtectionKey() => Read(_options.Value.ProtectionKeyEnvironmentVariable, exactly: 32);

    private static byte[] Read(string variable, int? exactly)
    {
        if (string.IsNullOrWhiteSpace(variable))
        {
            throw new ProxyConfigurationException("A proxy key environment variable is not configured.");
        }

        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(value))
        {
            // The variable name is safe to name; the value, had there been one, is not.
            throw new ProxyConfigurationException(
                $"The proxy is enabled but '{variable}' is not set. The proxy refuses to start without its key.");
        }

        var key = Encoding.UTF8.GetBytes(value);
        if (key.Length < 32 || (exactly is { } required && key.Length != required))
        {
            CryptographicOperations.ZeroMemory(key);
            throw new ProxyConfigurationException(
                exactly is { } size
                    ? $"'{variable}' must be exactly {size} UTF-8 bytes."
                    : $"'{variable}' must be at least 32 UTF-8 bytes.");
        }

        return key;
    }
}

/// <summary>A login pseudonym cannot be produced because this deployment has no proxy key.</summary>
/// <remarks>
/// Deliberately a type of its own rather than a <c>null</c> or a plain hash: a caller that reaches
/// authentication without a key must fail closed, and the route layer turns this into an explicit
/// "not configured" answer rather than a 500.
/// </remarks>
public sealed class ProxyNotConfiguredException : Exception
{
    public ProxyNotConfiguredException()
        : base("The access proxy is not composed in this deployment.")
    {
    }
}

/// <summary>A proxy key is configured but unusable. Startup fails rather than degrading.</summary>
public sealed class ProxyConfigurationException : Exception
{
    public ProxyConfigurationException(string message) : base(message)
    {
    }
}

/// <summary>A deriver for a deployment with no proxy key, which refuses every derivation.</summary>
public sealed class UnconfiguredProxyLoginKeyDeriver : IProxyLoginKeyDeriver
{
    public string Derive(string tenantId, string normalizedLogin)
        => throw new ProxyNotConfiguredException();
}

/// <summary>A key ring for a deployment with no proxy key, which refuses every read.</summary>
public sealed class UnconfiguredSecretKeyRing : ISecretKeyRing
{
    public string CurrentKeyId => "unconfigured";

    public byte[]? GetKey(string keyId) => throw new ProxyNotConfiguredException();
}

/// <summary>Builds the one shared login-key deriver, or the refusing stand-in.</summary>
internal static class ProxyLoginKeys
{
    public static IProxyLoginKeyDeriver Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<HostProxyOptions>>().Value;
        if (!options.Enabled)
        {
            return new UnconfiguredProxyLoginKeyDeriver();
        }

        return new HmacProxyLoginKeyDeriver(
            services.GetRequiredService<IProxySecretSource>().GetLoginKey());
    }

    public static ISecretKeyRing CreateKeyRing(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<HostProxyOptions>>().Value;
        if (!options.Enabled)
        {
            return new UnconfiguredSecretKeyRing();
        }

        return new InMemorySecretKeyRing(
            options.ProtectionKeyId,
            services.GetRequiredService<IProxySecretSource>().GetProtectionKey());
    }
}
