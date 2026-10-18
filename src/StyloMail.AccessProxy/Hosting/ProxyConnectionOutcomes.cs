namespace StyloMail.AccessProxy.Hosting;

/// <summary>Pre-protocol connection facts; never an account or a protocol-session identity.</summary>
public enum ProxyConnectionReason { TlsHandshakeTimeout, GlobalSessionLimitReached }

/// <summary>
/// A nonblocking, nonthrowing counter hook. Implementations must perform no I/O, wait or per-attempt
/// queuing here. Hosts own bounded aggregation, durable flushing and loss accounting.
/// </summary>
public interface IProxyConnectionOutcomeSink
{
    void Count(string tenantId, ProxyListenerProtocol protocol, ProxyConnectionReason reason);
}
