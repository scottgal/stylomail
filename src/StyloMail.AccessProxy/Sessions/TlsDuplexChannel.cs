using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace StyloMail.AccessProxy.Sessions;

/// <summary>A channel whose peer transport has completed TLS authentication.</summary>
public interface ISecureDuplexChannel : IDuplexChannel
{
    /// <summary>True only after the TLS handshake has succeeded.</summary>
    bool IsSecure { get; }
}

/// <summary>A plaintext SMTP channel that can perform exactly one Host-configured STARTTLS upgrade.</summary>
public interface IStartTlsCapableChannel : IDuplexChannel
{
    ValueTask<ISecureDuplexChannel> UpgradeToTlsAsync(CancellationToken cancellationToken);
}

/// <summary>Host-supplied TLS material for one listener tenant.</summary>
/// <remarks>
/// The access library never generates, loads, or persists a certificate. The Host owns those
/// concerns and passes a validated certificate into this small transport boundary.
/// </remarks>
public sealed record TlsServerOptions
{
    public required X509Certificate2 ServerCertificate { get; init; }

    public bool RequireClientCertificate { get; init; }

    public SslProtocols EnabledProtocols { get; init; } = SslProtocols.Tls12 | SslProtocols.Tls13;
}

/// <summary>Creates a TLS-authenticated duplex channel over an accepted TCP stream.</summary>
public sealed class TlsServerDuplexChannel : ISecureDuplexChannel
{
    private readonly SslStream _stream;

    private TlsServerDuplexChannel(SslStream stream, string description)
    {
        _stream = stream;
        Description = description;
    }

    public Stream Input => _stream;

    public Stream Output => _stream;

    public string Description { get; }

    public bool IsSecure => _stream.IsAuthenticated && _stream.IsEncrypted;

    /// <summary>
    /// Performs the server handshake. A missing or invalid Host certificate is a terminal error;
    /// callers must not continue with the accepted plaintext stream.
    /// </summary>
    public static async ValueTask<TlsServerDuplexChannel> AuthenticateAsync(
        Stream acceptedSocketStream,
        TlsServerOptions options,
        string description,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptedSocketStream);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ServerCertificate);
        ArgumentException.ThrowIfNullOrEmpty(description);

        var stream = new SslStream(acceptedSocketStream, leaveInnerStreamOpen: false);
        try
        {
            await stream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = options.ServerCertificate,
                    ClientCertificateRequired = options.RequireClientCertificate,
                    EnabledSslProtocols = options.EnabledProtocols,
                    CertificateRevocationCheckMode = X509RevocationMode.Online,
                },
                cancellationToken).ConfigureAwait(false);

            if (!stream.IsAuthenticated || !stream.IsEncrypted)
            {
                throw new AuthenticationException("TLS did not authenticate and encrypt the accepted client channel.");
            }

            return new TlsServerDuplexChannel(stream, description);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

/// <summary>Wraps an accepted socket until SMTP requests a one-way STARTTLS upgrade.</summary>
public sealed class StartTlsServerDuplexChannel : IStartTlsCapableChannel
{
    private readonly Stream _acceptedSocketStream;
    private readonly TlsServerOptions _options;
    private readonly string _description;
    private ISecureDuplexChannel? _secure;

    public StartTlsServerDuplexChannel(Stream acceptedSocketStream, TlsServerOptions options, string description)
    {
        _acceptedSocketStream = acceptedSocketStream ?? throw new ArgumentNullException(nameof(acceptedSocketStream));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrEmpty(description);
        _description = description;
    }

    public Stream Input => _secure?.Input ?? _acceptedSocketStream;

    public Stream Output => _secure?.Output ?? _acceptedSocketStream;

    public string Description => _description;

    public async ValueTask<ISecureDuplexChannel> UpgradeToTlsAsync(CancellationToken cancellationToken)
    {
        if (_secure is not null)
        {
            throw new InvalidOperationException("STARTTLS was already completed for this channel.");
        }

        _secure = await TlsServerDuplexChannel
            .AuthenticateAsync(_acceptedSocketStream, _options, _description, cancellationToken)
            .ConfigureAwait(false);
        return _secure;
    }

    public ValueTask DisposeAsync()
        => _secure is null ? _acceptedSocketStream.DisposeAsync() : _secure.DisposeAsync();
}
