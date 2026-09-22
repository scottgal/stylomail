namespace StyloMail.Transport.Smtp;

/// <summary>
/// A session could not be established: the upstream refused the connection, would not negotiate
/// TLS, would not authenticate, or would not complete a handshake.
/// </summary>
/// <remarks>
/// <b>Distinct from a per-recipient outcome.</b> This is thrown while setting up the conversation,
/// before any message is on the table, so it says nothing about any particular recipient and
/// nothing has been committed. Each recipient of the item is then reported as a transport failure
/// by the caller, rather than this exception being attributed to whichever recipient happened to be
/// first.
/// </remarks>
internal sealed class SmtpSessionException : Exception
{
    internal SmtpSessionException(SmtpDeliveryStage stage, bool isPermanent, string message)
        : base(message)
    {
        Stage = stage;
        IsPermanent = isPermanent;
    }

    internal SmtpSessionException(SmtpDeliveryStage stage, bool isPermanent, string message, Exception innerException)
        : base(message, innerException)
    {
        Stage = stage;
        IsPermanent = isPermanent;
    }

    /// <summary>Where in the handshake this failed.</summary>
    internal SmtpDeliveryStage Stage { get; }

    /// <summary>
    /// True when retrying cannot help without a configuration change, a refusal that will repeat.
    /// </summary>
    /// <remarks>
    /// A TLS downgrade is deliberately <b>not</b> permanent: an active attacker stripping
    /// <c>STARTTLS</c> produces exactly that symptom, and a retry that succeeds is the correct
    /// outcome. Burying the message as a permanent failure would let an attacker silence mail
    /// simply by being present.
    /// </remarks>
    internal bool IsPermanent { get; }
}
