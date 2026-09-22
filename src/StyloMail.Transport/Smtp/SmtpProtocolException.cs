namespace StyloMail.Transport.Smtp;

/// <summary>
/// The peer violated the SMTP protocol, or exceeded a bound while talking to us.
/// </summary>
/// <remarks>
/// <b>Not a delivery outcome.</b> This says the conversation was malformed or over budget — a
/// different fact from "the upstream refused the message". The caller maps it to a transient
/// failure <em>and abandons the connection</em>, because a stream that has been abandoned
/// mid-reply cannot be resynchronised by guessing where the next reply starts.
/// </remarks>
public sealed class SmtpProtocolException : Exception
{
    public SmtpProtocolException(string message)
        : base(message)
    {
    }

    public SmtpProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The peer closed the connection, or the read failed, while a reply was outstanding.</summary>
/// <remarks>
/// Distinct from <see cref="SmtpProtocolException"/> because of one case that matters enormously:
/// a connection lost <em>after</em> we have written the end-of-data terminator. At that point the
/// upstream may have accepted the message and we simply never heard so, which is
/// <c>DeliveryAttemptOutcome.InDoubt</c> rather than a plain transient failure. The caller decides
/// that from how far the protocol got; this exception only reports the loss.
/// </remarks>
public sealed class SmtpConnectionLostException : Exception
{
    public SmtpConnectionLostException(string message)
        : base(message)
    {
    }

    public SmtpConnectionLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The peer did not answer within the configured bound.</summary>
/// <remarks>
/// A timeout is always transient, and always fatal to the session: we do not reuse a connection on
/// which a reply might still be in flight.
/// </remarks>
public sealed class SmtpTimeoutException : Exception
{
    public SmtpTimeoutException(string message)
        : base(message)
    {
    }

    public SmtpTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
