using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace StyloMail.Host.Proxy;

/// <summary>Whether a provider is pinned to a deployment-supplied root.</summary>
public sealed record ProxyProviderTrust(string ProviderId, bool CustomTrustAnchor);

/// <summary>
/// Loads and validates the opt-in root certificates for backend TLS, one scope per provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a root-set replacement, not an addition and not a bypass.</b> A provider with
/// <c>CaCertificatePath</c> validates its backend chain against exactly that anchor; hostname and
/// validity checks are still performed by the TLS stack, and no other provider or the client-facing
/// listener TLS sees this anchor.
/// </para>
/// <para>
/// A configured-but-unusable anchor is a startup fault. Falling back to the system roots because a
/// file was unreadable would make a deployment that intended to pin a private backend silently
/// trust the public web for it, which is the failure this setting exists to prevent.
/// </para>
/// </remarks>
public sealed class ProxyTrustAnchors
{
    private readonly Dictionary<string, X509Certificate2> _anchors;

    public ProxyTrustAnchors(IOptions<HostProxyOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _anchors = new Dictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);
        var now = clock.GetUtcNow().UtcDateTime;

        foreach (var (providerId, provider) in options.Value.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.CaCertificatePath))
            {
                continue;
            }

            var anchor = Load(provider.CaCertificatePath);

            // Expiry is checked here as well as by chain building, because the failure at startup
            // says which provider is misconfigured instead of surfacing as every session failing.
            if (anchor.NotAfter.ToUniversalTime() <= now || anchor.NotBefore.ToUniversalTime() > now)
            {
                anchor.Dispose();
                throw new ProxyConfigurationException(
                    $"The proxy backend trust anchor for provider '{providerId}' is outside its validity period.");
            }

            _anchors[providerId] = anchor;
        }
    }

    /// <summary>The anchor for a provider, or null when it uses the system roots.</summary>
    public X509Certificate2? Get(string providerId)
        => _anchors.TryGetValue(providerId, out var anchor) ? anchor : null;

    /// <summary>The provider/trust report the listener surface exposes.</summary>
    public IReadOnlyList<ProxyProviderTrust> Describe()
        => [.. _anchors.Keys
            .OrderBy(providerId => providerId, StringComparer.OrdinalIgnoreCase)
            .Select(providerId => new ProxyProviderTrust(providerId, true))];

    private static X509Certificate2 Load(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new ProxyConfigurationException(
                "A configured proxy backend trust anchor is missing or empty.");
        }

        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                // A trust anchor is a public certificate; it carries no private key. The loader
                // accepts PEM (certificate-only) and DER, which CreateFromPemFile does not.
                ".pfx" or ".p12" => X509CertificateLoader.LoadPkcs12FromFile(path, password: null),
                _ => X509CertificateLoader.LoadCertificateFromFile(path),
            };
        }
        catch (Exception exception) when (exception is CryptographicException or IOException
            or UnauthorizedAccessException)
        {
            throw new ProxyConfigurationException(
                "A configured proxy backend trust anchor could not be loaded.");
        }
    }
}
