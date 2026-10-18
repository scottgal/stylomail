using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Imap;
using StyloMail.AccessProxy.Pop3;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Smtp;
using StyloMail.AccessProxy.Submission;

namespace StyloMail.AccessProxy.Hosting;

public interface IProxyBackendConnectorFactory
{
    IBackendConnector Create(ProxyListenerProtocol protocol);
}

/// <summary>Host-owned dependencies used to compose an access protocol session runner.</summary>
public sealed record ProxySessionRunnerDependencies
{
    public required IStyloMailAccountStore Accounts { get; init; }
    public required IStyloMailPasswordHasher PasswordHasher { get; init; }
    public required IBackendCredentialStore CredentialStore { get; init; }
    public required BackendCredentialResolver CredentialResolver { get; init; }
    public required IProxyBackendConnectorFactory BackendConnectors { get; init; }
    public required SessionLimiter Limiter { get; init; }
    public required AccessProxyBounds Bounds { get; init; }
    public required TimeProvider TimeProvider { get; init; }
    public required IProxySessionContextSink ContextSink { get; init; }
    public required IProxyWarningEventSink WarningSink { get; init; }
    public required IProxyWarningAnalyzer WarningAnalyzer { get; init; }
    public TlsServerOptions? Tls { get; init; }
    public IProxySubmissionAssessment? SubmissionAssessment { get; init; }
    public Submission.ProxyElementOrchestrator? ElementOrchestrator { get; init; }
    public IEphemeralSubmissionStager? SubmissionStager { get; init; }
    public IRetrievalObserver? RetrievalObserver { get; init; }
    /// <summary>Optional off-relay IMAP FETCH-literal analysis. Null means retrieval analysis is off.</summary>
    public IProxyRetrievalWarningAssemblerFactory? RetrievalWarningAssemblerFactory { get; init; }
}

/// <summary>Builds the listener session delegate from one tenant-bound listener options record.</summary>
public sealed class ProxySessionRunnerFactory
{
    /// <summary>Applies listener TLS policy with a bounded eager handshake and preserves socket admission.</summary>
    public static async Task<SessionOutcome> RunWithTlsAsync(ProxyListenerProtocol protocol,
        TlsServerOptions? tls, AccessProxyBounds bounds, TimeProvider clock,
        Func<string, IDuplexChannel, CancellationToken, Task<SessionOutcome>> sessions,
        string tenantId, IDuplexChannel channel, CancellationToken cancellationToken,
        IProxyConnectionOutcomeSink? outcomes = null)
    {
        if (tls is null) return await sessions(tenantId, channel, cancellationToken).ConfigureAwait(false);
        if (protocol == ProxyListenerProtocol.Smtp)
        {
            // SMTP owns its existing authentication/STARTTLS deadline inside the protocol session.
            await using var startTls = new StartTlsServerDuplexChannel(channel.Input, tls, channel.Description);
            return await sessions(tenantId, AdmittedDuplexChannel.Inherit(channel, startTls), cancellationToken).ConfigureAwait(false);
        }

        TlsServerDuplexChannel secure;
        using (var handshake = new TimeoutScope(bounds.AuthenticationTimeout, clock, cancellationToken))
        {
            try
            {
                secure = await TlsServerDuplexChannel.AuthenticateAsync(channel.Input, tls, channel.Description,
                    handshake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (handshake.DeadlineElapsed && !cancellationToken.IsCancellationRequested)
            {
                try { outcomes?.Count(tenantId, protocol, ProxyConnectionReason.TlsHandshakeTimeout); }
                catch (Exception) { /* Optional counters never change connection outcomes. */ }
                return SessionOutcome.Timeout;
            }
        }
        await using (secure)
            return await sessions(tenantId, AdmittedDuplexChannel.Inherit(channel, secure), cancellationToken).ConfigureAwait(false);
    }

    public static Func<string, IDuplexChannel, CancellationToken, Task<SessionOutcome>> Create(
        ProxyListenerOptions options,
        ProxySessionRunnerDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (options.Protocol == ProxyListenerProtocol.Smtp && dependencies.Tls is null)
            throw new InvalidOperationException("SMTP session composition requires TLS material.");

        return (tenantId, channel, cancellationToken) => options.Protocol switch
        {
            ProxyListenerProtocol.Imap => new ImapAccessProxySession(tenantId, dependencies.Accounts,
                dependencies.PasswordHasher, dependencies.CredentialResolver,
                dependencies.BackendConnectors.Create(options.Protocol), dependencies.Limiter, dependencies.Bounds,
                dependencies.TimeProvider, dependencies.RetrievalObserver, dependencies.ContextSink,
                dependencies.RetrievalWarningAssemblerFactory).RunAsync(channel, cancellationToken),
            ProxyListenerProtocol.Pop3 => new Pop3AccessProxySession(tenantId, dependencies.Accounts,
                dependencies.PasswordHasher, dependencies.CredentialResolver,
                dependencies.BackendConnectors.Create(options.Protocol), dependencies.Limiter, dependencies.Bounds,
                dependencies.TimeProvider, dependencies.RetrievalObserver, dependencies.ContextSink).RunAsync(channel, cancellationToken),
            ProxyListenerProtocol.Smtp => new SmtpAccessProxySession(tenantId, dependencies.Accounts,
                dependencies.PasswordHasher, dependencies.BackendConnectors.Create(options.Protocol), dependencies.Limiter,
                dependencies.Bounds, dependencies.TimeProvider,
                dependencies.SubmissionAssessment ?? throw new InvalidOperationException("SMTP assessment is required."),
                dependencies.SubmissionStager ?? throw new InvalidOperationException("SMTP stager is required."),
                dependencies.ContextSink, dependencies.WarningSink, dependencies.WarningAnalyzer, dependencies.ElementOrchestrator)
                .RunAsync(channel, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options.Protocol)),
        };
    }
}
