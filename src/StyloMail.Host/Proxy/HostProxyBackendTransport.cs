using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.Host.Proxy;

/// <summary>Builds the connector for one protocol, all sharing the deployment transport.</summary>
public sealed class HostProxyBackendConnectorFactory : IProxyBackendConnectorFactory
{
    private readonly IBackendTransport _transport;
    private readonly BackendCredentialResolver _credentials;
    private readonly AccessProxyBounds _bounds;

    public HostProxyBackendConnectorFactory(
        IBackendTransport transport,
        BackendCredentialResolver credentials,
        AccessProxyBounds bounds)
    {
        _transport = transport;
        _credentials = credentials;
        _bounds = bounds;
    }

    public IBackendConnector Create(ProxyListenerProtocol protocol) => protocol switch
    {
        ProxyListenerProtocol.Imap => new ImapBackendConnector(_transport, _credentials, _bounds),
        ProxyListenerProtocol.Pop3 => new Pop3BackendConnector(_transport, _credentials, _bounds),
        ProxyListenerProtocol.Smtp => new SmtpBackendConnector(_transport, _credentials, _bounds),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };
}

/// <summary>
/// Opens the backend connection for one account using deployment-configured endpoints.
/// </summary>
/// <remarks>
/// <para>
/// The provider is read from the stored credential, never from the request. The request carries a
/// tenant and a backend account id; the endpoint is a property of the deployment's configuration
/// for that provider. A caller therefore cannot point a proxied session at a host of its choosing.
/// </para>
/// <para>
/// A missing endpoint entry fails closed before a socket is opened: the session is refused, not
/// retried and not sent somewhere convenient. Plaintext is accepted only for a loopback endpoint,
/// which is the E2E harness case; a non-loopback plaintext provider is a configuration fault.
/// </para>
/// </remarks>
public sealed class HostProxyBackendTransport : IBackendTransport
{
    private readonly IOptions<HostProxyOptions> _options;
    private readonly IBackendCredentialStore _credentials;
    private readonly ProxyTrustAnchors _anchors;
    private readonly AccessProxyBounds _bounds;

    public HostProxyBackendTransport(
        IOptions<HostProxyOptions> options,
        IBackendCredentialStore credentials,
        ProxyTrustAnchors anchors,
        AccessProxyBounds bounds)
    {
        _options = options;
        _credentials = credentials;
        _anchors = anchors;
        _bounds = bounds;
    }

    public async ValueTask<IDuplexChannel> OpenAsync(
        BackendConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var record = await _credentials.FindAsync(request.TenantId, request.AccountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CredentialUnavailableException(
                "The account has no backend credential enrolled.");

        if (record.Revoked)
        {
            throw new CredentialUnavailableException("The backend credential has been revoked.");
        }

        if (!_options.Value.Providers.TryGetValue(record.ProviderId, out var provider))
        {
            throw new ProxyConfigurationException(
                "No backend endpoint is configured for this account's provider.");
        }

        var (host, port, tls) = request.Protocol switch
        {
            BackendProtocol.Imap => (provider.ImapHost, provider.ImapPort, provider.ImapTls),
            BackendProtocol.Pop3 => (provider.Pop3Host, provider.Pop3Port, provider.Pop3Tls),
            BackendProtocol.Smtp => (provider.SmtpHost, provider.SmtpPort, provider.SmtpTls),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ProxyConfigurationException(
                "The configured provider has no endpoint for this protocol.");
        }

        // One deadline covers DNS, TCP and TLS establishment. Without it, a black-holed or
        // half-open backend waits on the operating system's connect/handshake timeout, well past
        // the session's configured bound, before the client sees a failure.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_bounds.BackendConnectTimeout);

        if (!tls && !await IsLoopbackAsync(host, deadline.Token).ConfigureAwait(false))
        {
            throw new ProxyConfigurationException(
                "A plaintext backend endpoint is permitted only on loopback.");
        }

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new AccessProxyTimeoutException(
                "The backend connection exceeded the configured connect bound.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var description = $"backend:{record.ProviderId}/{request.Protocol}";
        if (!tls)
        {
            var stream = client.GetStream();
            return new StreamDuplexChannel(stream, stream, description);
        }

        var secure = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            var clientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            };

            if (_anchors.Get(record.ProviderId) is { } anchor)
            {
                // CustomRootTrust replaces the system roots for this connection only. Name
                // validation and validity checking are left to the TLS stack; there is no policy
                // error callback and no way here to accept an untrusted peer.
                var policy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    // A private backend has no public CRL/OCSP to consult; online revocation here
                    // would stall every session. Expiry is still a chain failure.
                    RevocationMode = X509RevocationMode.NoCheck,
                };
                policy.CustomTrustStore.Add(anchor);
                clientOptions.CertificateChainPolicy = policy;
            }

            await secure.AuthenticateAsClientAsync(clientOptions, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await secure.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            throw new AccessProxyTimeoutException(
                "The backend TLS handshake exceeded the configured connect bound.");
        }
        catch
        {
            await secure.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            throw;
        }

        return new TlsClientDuplexChannel(secure, client, description);
    }

    private static async Task<bool> IsLoopbackAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return IPAddress.IsLoopback(literal);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0 && addresses.All(IPAddress.IsLoopback);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>A TLS backend channel. Secure only after the handshake, which is reported honestly.</summary>
    private sealed class TlsClientDuplexChannel : ISecureDuplexChannel
    {
        private readonly SslStream _stream;
        private readonly TcpClient _client;

        public TlsClientDuplexChannel(SslStream stream, TcpClient client, string description)
        {
            _stream = stream;
            _client = client;
            Description = description;
        }

        public Stream Input => _stream;

        public Stream Output => _stream;

        public bool IsSecure => _stream.IsAuthenticated;

        public string Description { get; }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _client.Dispose();
        }
    }
}
