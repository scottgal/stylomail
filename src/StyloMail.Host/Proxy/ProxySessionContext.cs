using StyloMail.AccessProxy.Hosting;

namespace StyloMail.Host.Proxy;

/// <summary>The connection a protocol session is running for, on its own async flow.</summary>
internal sealed record ProxySessionBinding(
    string SessionId,
    string TenantId,
    ProxyListenerProtocol Protocol)
{
    public ProxyBoundReason BoundReason { get; set; }
}

/// <summary>
/// Ambient binding for the session currently executing.
/// </summary>
/// <remarks>
/// Access's authenticated-account sink supplies the account id and instant but not the connection
/// they belong to. An <see cref="AsyncLocal{T}"/> carries that binding down the session's own async
/// flow, so a shared sink can update the right row without mutable state shared between
/// concurrent sessions.
/// </remarks>
internal static class ProxySessionContext
{
    public static readonly AsyncLocal<ProxySessionBinding?> Current = new();
}
