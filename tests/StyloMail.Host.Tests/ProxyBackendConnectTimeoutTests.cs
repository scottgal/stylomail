using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;
using StyloMail.Host.Proxy;

namespace StyloMail.Host.Tests;

/// <summary>
/// The backend connect bound covers DNS, TCP and TLS establishment. A refused port and a socket
/// that accepts TCP then never completes TLS must both fail within the configured deadline, not on
/// the operating system's much longer timeout.
/// </summary>
public sealed class ProxyBackendConnectTimeoutTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task A_refused_port_returns_within_the_connect_bound()
    {
        var port = FreePort();
        var transport = Transport(port, tls: true);

        var started = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => transport.OpenAsync(
            Request(), CancellationToken.None).AsTask());

        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(2),
            $"Refused connect took {started.Elapsed}, expected within the bound plus slack.");
    }

    [Fact]
    public async Task A_socket_that_accepts_tcp_and_never_completes_tls_returns_as_a_typed_timeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var transport = Transport(port, tls: true);

            var accept = listener.AcceptTcpClientAsync();
            var started = Stopwatch.StartNew();
            var open = transport.OpenAsync(Request(), CancellationToken.None).AsTask();

            using var accepted = await accept;
            var exception = await Assert.ThrowsAsync<AccessProxyTimeoutException>(() => open);

            Assert.True(
                started.Elapsed < TimeSpan.FromSeconds(2),
                $"TLS establishment took {started.Elapsed}, expected within the bound plus slack.");
            Assert.Contains("connect bound", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static HostProxyBackendTransport Transport(int port, bool tls)
    {
        var options = Options.Create(new HostProxyOptions
        {
            Providers = new Dictionary<string, HostProxyProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["harness"] = new()
                {
                    ImapHost = "127.0.0.1",
                    ImapPort = port,
                    ImapTls = tls,
                },
            },
        });

        return new HostProxyBackendTransport(
            options,
            new FixedCredentialStore(),
            new ProxyTrustAnchors(options, TimeProvider.System),
            new AccessProxyBounds { BackendConnectTimeout = Bound });
    }

    private static BackendConnectionRequest Request() => new()
    {
        TenantId = "acme",
        AccountId = "backend-1",
        Protocol = BackendProtocol.Imap,
        TimeProvider = TimeProvider.System,
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class FixedCredentialStore : IBackendCredentialStore
    {
        public ValueTask<BackendCredentialRecord?> FindAsync(
            string tenantId,
            string backendAccountId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<BackendCredentialRecord?>(new BackendCredentialRecord
            {
                AccountId = backendAccountId,
                TenantId = tenantId,
                ProviderId = "harness",
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
