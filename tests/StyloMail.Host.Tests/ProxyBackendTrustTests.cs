using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;
using StyloMail.Host.Proxy;

namespace StyloMail.Host.Tests;

/// <summary>
/// The opt-in per-provider backend trust anchor: one provider's backend TLS only, root replacement
/// rather than a bypass, name and validity checks still on, and a bad anchor failing closed.
/// </summary>
public sealed class ProxyBackendTrustTests
{
    [Fact]
    public async Task A_pinned_private_backend_is_trusted_by_its_own_anchor()
    {
        using var ca = CreateAuthority();
        using var server = CreateServerCertificate(ca, ipAddress: true);
        var caPath = WritePem(ca);

        var channel = await OpenAsync(server, ProviderOptions(caPath));

        var secure = Assert.IsAssignableFrom<ISecureDuplexChannel>(channel);
        Assert.True(secure.IsSecure);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Without_the_anchor_the_same_self_signed_backend_is_rejected()
    {
        using var ca = CreateAuthority();
        using var server = CreateServerCertificate(ca, ipAddress: true);

        // Same endpoint and certificate, but no pinned root: the public roots cannot validate it.
        await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(
            () => OpenAsync(server, ProviderOptions(caCertificatePath: null)));
    }

    [Fact]
    public async Task A_matching_root_does_not_disable_name_validation()
    {
        using var ca = CreateAuthority();
        // The certificate names only "localhost"; the Host dials 127.0.0.1.
        using var server = CreateServerCertificate(ca, ipAddress: false);
        var caPath = WritePem(ca);

        await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(
            () => OpenAsync(server, ProviderOptions(caPath)));
    }

    [Fact]
    public void An_expired_anchor_is_a_startup_failure_not_a_fallback()
    {
        using var ca = CreateAuthority(expired: true);
        var caPath = WritePem(ca);

        var exception = Assert.Throws<ProxyConfigurationException>(
            () => new ProxyTrustAnchors(Options.Create(OptionsWith(caPath)), TimeProvider.System));

        Assert.Contains("validity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_anchor_file_is_a_startup_failure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-ca-{Guid.NewGuid():N}.pem");

        Assert.Throws<ProxyConfigurationException>(
            () => new ProxyTrustAnchors(Options.Create(OptionsWith(path)), TimeProvider.System));
    }

    [Fact]
    public void The_trust_report_names_only_pinned_providers()
    {
        using var ca = CreateAuthority();
        var caPath = WritePem(ca);

        var options = Options.Create(new HostProxyOptions
        {
            Providers = new Dictionary<string, HostProxyProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["pinned"] = new() { CaCertificatePath = caPath },
                ["public"] = new(),
            },
        });

        var anchors = new ProxyTrustAnchors(options, TimeProvider.System);

        Assert.NotNull(anchors.Get("pinned"));
        Assert.Null(anchors.Get("public"));
        var report = Assert.Single(anchors.Describe());
        Assert.Equal("pinned", report.ProviderId);
        Assert.True(report.CustomTrustAnchor);
    }

    // ---------------------------------------------------------------------------------------------
    // TLS probe
    // ---------------------------------------------------------------------------------------------

    private static async Task<IDuplexChannel> OpenAsync(
        X509Certificate2 serverCertificate,
        HostProxyProviderOptions provider)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(serverCertificate);
            }
            catch (Exception)
            {
                // The rejection case closes without a completed handshake; the assertion is on the
                // client side, which is the side the Host actually runs.
            }
            finally
            {
                listener.Stop();
            }
        });

        provider.ImapHost = "127.0.0.1";
        provider.ImapPort = port;
        provider.ImapTls = true;

        var options = Options.Create(new HostProxyOptions
        {
            Providers = new Dictionary<string, HostProxyProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["harness"] = provider,
            },
        });

        var transport = new HostProxyBackendTransport(
            options,
            new FixedCredentialStore("harness"),
            new ProxyTrustAnchors(options, TimeProvider.System),
            new AccessProxyBounds());

        try
        {
            return await transport.OpenAsync(
                new BackendConnectionRequest
                {
                    TenantId = "acme",
                    AccountId = "backend-1",
                    Protocol = BackendProtocol.Imap,
                    TimeProvider = TimeProvider.System,
                },
                CancellationToken.None);
        }
        finally
        {
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static HostProxyProviderOptions ProviderOptions(string? caCertificatePath)
        => new() { CaCertificatePath = caCertificatePath };

    private static HostProxyOptions OptionsWith(string caPath) => new()
    {
        Providers = new Dictionary<string, HostProxyProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["harness"] = new() { CaCertificatePath = caPath },
        },
    };

    private static string WritePem(X509Certificate2 authority)
    {
        var path = Path.Combine(Path.GetTempPath(), $"stylomail-ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, authority.ExportCertificatePem());
        return path;
    }

    private static X509Certificate2 CreateAuthority(bool expired = false)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=StyloMail harness root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        var notBefore = expired ? DateTimeOffset.UtcNow.AddDays(-3) : DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(1);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static X509Certificate2 CreateServerCertificate(
        X509Certificate2 authority,
        bool ipAddress)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(
            ipAddress ? "CN=127.0.0.1" : "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        if (ipAddress)
        {
            names.AddIpAddress(IPAddress.Loopback);
        }
        else
        {
            names.AddDnsName("localhost");
        }

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        using var issued = request.Create(
            authority,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1),
            RandomNumberGenerator.GetBytes(8));

        return issued.CopyWithPrivateKey(key);
    }

    private sealed class FixedCredentialStore : IBackendCredentialStore
    {
        private readonly string _providerId;

        public FixedCredentialStore(string providerId) => _providerId = providerId;

        public ValueTask<BackendCredentialRecord?> FindAsync(
            string tenantId,
            string backendAccountId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<BackendCredentialRecord?>(new BackendCredentialRecord
            {
                AccountId = backendAccountId,
                TenantId = tenantId,
                ProviderId = _providerId,
                Discriminator = "app-password",
                ProtectedSecret = [1],
                ProtectionKeyId = "fixture",
                BackendUsername = "backend-user",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

        public ValueTask<BackendCredentialRecord> StoreAsync(
            BackendCredentialWrite write,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<bool> RevokeAsync(
            string tenantId,
            string backendAccountId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
