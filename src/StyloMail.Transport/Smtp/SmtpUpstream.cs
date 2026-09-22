namespace StyloMail.Transport.Smtp;

/// <summary>How much transport security is demanded of an upstream connection.</summary>
public enum SmtpTlsMode
{
    /// <summary>
    /// TLS is mandatory: the connection must be encrypted before any mail is sent.
    /// </summary>
    /// <remarks>
    /// The default, and the only safe setting for an upstream reached over a network. A refusal to
    /// negotiate TLS is treated as a failure to deliver rather than as a reason to continue in the
    /// clear — see <see cref="SmtpSession"/> for why a downgrade is never silently accepted.
    /// </remarks>
    Required = 0,

    /// <summary>
    /// Use TLS when the upstream offers it, and continue in the clear when it does not.
    /// </summary>
    /// <remarks>
    /// <b>Only acceptable when credentials are not configured.</b> Opportunistic TLS is exactly the
    /// setting an active attacker defeats: stripping <c>STARTTLS</c> from the EHLO response
    /// downgrades the session, and the client has been told that is fine. It is offered for a
    /// same-host or same-datacentre relay where the operator has made that call deliberately.
    /// </remarks>
    Opportunistic = 1,

    /// <summary>
    /// Never use TLS.
    /// </summary>
    /// <remarks>
    /// <b>Refused outright when credentials are configured</b> — see
    /// <see cref="SmtpUpstream.Validate"/>. The only defensible use is a loopback relay.
    /// </remarks>
    None = 2,
}

/// <summary>
/// A username and password for SMTP <c>AUTH</c>.
/// </summary>
/// <remarks>
/// <b>This type deliberately cannot be printed.</b> <see cref="ToString"/> is overridden to redact,
/// so an interpolated log line, an exception message or a transcript entry that accidentally
/// captures the record produces <c>[redacted]</c> rather than the password. Record types synthesise
/// a <c>ToString</c> that prints every property, which is precisely the accident this prevents.
///
/// <para>
/// The value itself comes from the deployment's secret store by reference and is never written to
/// configuration files in this repository.
/// </para>
/// </remarks>
public sealed record SmtpCredentials
{
    public required string Username { get; init; }

    public required string Password { get; init; }

    public override string ToString() => $"{Username}:[redacted]";
}

/// <summary>
/// One configured mail submission target.
/// </summary>
/// <remarks>
/// StyloMail delivers to an <b>established upstream</b> and does not resolve MX records itself. That
/// is the spec's "behind an established MTA" constraint: internet-facing protocol complexity —
/// MX selection, retry policy, DSN generation, reputation — stays with the MTA that already owns it.
/// This type therefore names exactly one host and one policy, with no routing table.
/// </remarks>
public sealed record SmtpUpstream
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public SmtpTlsMode Tls { get; init; } = SmtpTlsMode.Required;

    /// <summary>
    /// True for implicit TLS (the SMTPS convention, port 465): the socket is encrypted before the
    /// server says anything at all, rather than upgraded with <c>STARTTLS</c> after the greeting.
    /// </summary>
    public bool ImplicitTls { get; init; }

    /// <summary>Credentials for <c>AUTH</c>, or null for an upstream that does not require them.</summary>
    public SmtpCredentials? Credentials { get; init; }

    /// <summary>
    /// The name we announce in <c>EHLO</c>.
    /// </summary>
    /// <remarks>
    /// Should be a name the upstream recognises as ours. Some relays refuse clients whose EHLO
    /// argument is not a resolvable FQDN, and a stable value makes our sessions identifiable in the
    /// upstream's own logs — which is what an operator needs when asking "what did StyloMail send?".
    /// </remarks>
    public string HeloName { get; init; } = "localhost";

    /// <summary>
    /// Validates the combination of security settings.
    /// </summary>
    /// <remarks>
    /// <b>This is where "require TLS wherever credentials are used" is enforced</b>, and it is
    /// enforced at construction time rather than at send time so that a misconfiguration is a
    /// startup failure, not a mail failure discovered later. A deployment cannot be coaxed into
    /// transmitting a password in the clear by editing one enum value: the object refuses to exist.
    /// </remarks>
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host, nameof(Host));

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port must be in 1-65535.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(HeloName, nameof(HeloName));

        if (Credentials is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Credentials.Username, nameof(Credentials));
            ArgumentException.ThrowIfNullOrWhiteSpace(Credentials.Password, nameof(Credentials));

            if (Tls == SmtpTlsMode.None)
            {
                throw new InvalidOperationException(
                    $"Upstream '{Host}' configures credentials with TLS disabled. Refusing to construct " +
                    "a target that would send a password in the clear.");
            }
        }

        if (ImplicitTls && Tls == SmtpTlsMode.None)
        {
            throw new InvalidOperationException(
                $"Upstream '{Host}' sets ImplicitTls with Tls=None, which is self-contradictory.");
        }
    }
}
