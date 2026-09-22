using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace StyloMail.Transport.Smtp;

/// <summary>
/// A duplex byte channel to an upstream, and its ability to become encrypted.
/// </summary>
/// <remarks>
/// Exists as a seam so the protocol state machine can be driven over an in-memory stream in tests
/// without a socket, while production still gets a real one. It deliberately exposes the raw stream
/// rather than command-level methods: the <em>protocol</em> is the thing under test, and a channel
/// that spoke SMTP would put the logic being tested on the wrong side of the seam.
/// </remarks>
internal interface ISmtpChannel : IAsyncDisposable
{
    /// <summary>The current byte stream. Changes identity after a TLS upgrade.</summary>
    Stream Stream { get; }

    /// <summary>True once the channel is encrypted.</summary>
    bool IsEncrypted { get; }

    /// <summary>A description of the peer for transcripts. Never includes credentials.</summary>
    string PeerDescription { get; }

    /// <summary>
    /// Upgrades the connection to TLS in place, returning the encrypted stream.
    /// </summary>
    ValueTask<Stream> StartTlsAsync(string targetHost, CancellationToken cancellationToken);
}

/// <summary>A real TCP connection, optionally upgraded to TLS.</summary>
internal sealed class SocketSmtpChannel : ISmtpChannel
{
    private readonly string _host;
    private readonly string _peerDescription;
    private readonly TimeProvider _timeProvider;
    private readonly RemoteCertificateValidationCallback? _validationOverride;
    private readonly TcpClient _client;
    private Stream _stream;
    private SslStream? _tls;
    private bool _disposed;

    private SocketSmtpChannel(
        string host,
        string peerDescription,
        TcpClient client,
        Stream stream,
        bool encrypted,
        TimeProvider timeProvider,
        RemoteCertificateValidationCallback? validationOverride)
    {
        _host = host;
        _peerDescription = peerDescription;
        _client = client;
        _stream = stream;
        _timeProvider = timeProvider;
        _validationOverride = validationOverride;
        IsEncrypted = encrypted;
    }

    public Stream Stream => _stream;

    public bool IsEncrypted { get; private set; }

    public string PeerDescription => _peerDescription;

    /// <summary>
    /// Opens a TCP connection to <paramref name="host"/>, optionally encrypted from the first byte.
    /// </summary>
    /// <param name="validationOverride">
    /// <b>Test only.</b> Production callers must pass <c>null</c>, which uses the platform trust
    /// store. It exists so a test can trust the self-signed certificate of its own in-process server;
    /// it is never reachable from configuration, and exposing it any other way would turn
    /// "which certificates do we trust?" into a deployment setting, which is how TLS gets disabled
    /// by accident.
    /// </param>
    internal static async ValueTask<SocketSmtpChannel> ConnectAsync(
        string host,
        int port,
        bool implicitTls,
        SmtpBounds bounds,
        TimeProvider timeProvider,
        RemoteCertificateValidationCallback? validationOverride,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };

        using (var timeoutSource = new CancellationTokenSource(bounds.ConnectTimeout, timeProvider))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token))
        {
            try
            {
                await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw new SmtpTimeoutException(
                    $"Connecting to {host}:{port.ToString(CultureInfo.InvariantCulture)} exceeded " +
                    $"{bounds.ConnectTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s.");
            }
            catch (SocketException ex)
            {
                client.Dispose();
                throw new SmtpConnectionLostException($"Could not connect to {host}:{port.ToString(CultureInfo.InvariantCulture)}.", ex);
            }
        }

        var peerDescription = $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
        var channel = new SocketSmtpChannel(
            host,
            peerDescription,
            client,
            client.GetStream(),
            encrypted: false,
            timeProvider,
            validationOverride);

        if (implicitTls)
        {
            await channel.StartTlsAsync(host, cancellationToken).ConfigureAwait(false);
        }

        return channel;
    }

    public async ValueTask<Stream> StartTlsAsync(string targetHost, CancellationToken cancellationToken)
    {
        if (IsEncrypted)
        {
            throw new InvalidOperationException("The channel is already encrypted.");
        }

        var ssl = new SslStream(
            _stream,
            leaveInnerStreamOpen: true,
            _validationOverride);

        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new SmtpConnectionLostException(
                $"TLS authentication with {_peerDescription} failed: {ex.Message}", ex);
        }

        // The plaintext stream is replaced, not wrapped again: a second upgrade on the same channel
        // would nest SslStreams and is always a logic error.
        _tls = ssl;
        _stream = ssl;
        IsEncrypted = true;
        return ssl;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_tls is not null)
        {
            await _tls.DisposeAsync().ConfigureAwait(false);
        }

        _client.Dispose();
    }
}
