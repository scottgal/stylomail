namespace StyloMail.AccessProxy.Sessions;

/// <summary>
/// The client violated the access protocol, or exceeded one of the proxy's bounds.
/// </summary>
/// <remarks>
/// Named separately from the transport's SMTP protocol exception because it is a different actor: a
/// client is not a trusted MTA peer, and "the client sent something malformed" is a routine event
/// on an internet-facing listener rather than a signal about an upstream.
///
/// <para>
/// Messages carry no message content and no credential material. A malformed LOGIN line is
/// precisely the input most likely to contain a password typed into the wrong field, so the
/// exception names the violation and never echoes the offending line.
/// </para>
/// </remarks>
public sealed class AccessProxyProtocolException : Exception
{
    public AccessProxyProtocolException(string message)
        : base(message)
    {
    }

    public AccessProxyProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>No bytes arrived within a configured bound.</summary>
/// <remarks>
/// Terminal to the session in every case. A protocol whose stream has stalled cannot be
/// resynchronised by guessing where the next reply starts, so there is no "retry the read" path.
/// </remarks>
public sealed class AccessProxyTimeoutException : Exception
{
    public AccessProxyTimeoutException(string message)
        : base(message)
    {
    }

    public AccessProxyTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
