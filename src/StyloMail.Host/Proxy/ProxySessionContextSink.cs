using Microsoft.Extensions.Logging;
using StyloMail.AccessProxy.Hosting;

namespace StyloMail.Host.Proxy;

/// <summary>
/// The per-connection sink access calls once client and backend authentication both succeeded.
/// </summary>
/// <remarks>
/// <para>
/// The sink reads the ambient <see cref="ProxySessionContext"/> for the session this async flow
/// belongs to, then updates that session's existing opened row in place. It never opens a second
/// session event and never guesses an account id from the connection: the protocol session is the
/// only owner of the legitimate id.
/// </para>
/// <para>
/// A failed telemetry write is swallowed with a bounded counter. Access's boundary also swallows,
/// but the Host's own persistence must not be the reason a client's relay stops.
/// </para>
/// </remarks>
internal sealed class ProxySessionContextSink : IProxySessionContextSink, IProxySessionBoundSink
{
    private readonly IProxySessionAuthenticator _authenticator;
    private readonly ILogger _logger;
    private int _failures;

    public ProxySessionContextSink(IProxySessionAuthenticator authenticator, ILogger logger)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask OnAuthenticatedAsync(
        string accountId,
        DateTimeOffset authenticatedAt,
        CancellationToken cancellationToken)
    {
        if (ProxySessionContext.Current.Value is not { } binding)
        {
            return;
        }

        try
        {
            // Idempotent by construction: the update sets the same account and instant on the one
            // live row for this SessionId.
            await _authenticator.MarkAuthenticatedAsync(
                binding.TenantId,
                binding.SessionId,
                accountId,
                authenticatedAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            var failures = Interlocked.Increment(ref _failures);
            if (failures == 1 || failures % 100 == 0)
            {
                _logger.LogWarning(
                    "A proxy session authentication record could not be written ({Failures} total).",
                    failures);
            }
        }
    }

    public void OnBound(ProxyBoundReason reason)
    {
        if (ProxySessionContext.Current.Value is { } binding) binding.BoundReason = reason;
    }
}
