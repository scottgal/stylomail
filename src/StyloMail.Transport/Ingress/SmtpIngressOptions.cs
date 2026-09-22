using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace StyloMail.Transport.Ingress;

/// <summary>
/// Configuration and bounds for the SMTP submission listener.
/// </summary>
/// <remarks>
/// <b>This is not a public MX service.</b> The spec is explicit that StyloMail sits behind an
/// established MTA and does not take on internet-facing protocol complexity. What this listener
/// provides is the restricted handoff the spec does allow: a client that already holds credentials
/// submits through us, or a trusted internal relay hands us mail for domains we serve. Everything
/// below is what keeps those two cases from becoming a third, an open relay.
/// </remarks>
public sealed record SmtpIngressOptions
{
    /// <summary>
    /// Address to bind. Defaults to loopback.
    /// </summary>
    /// <remarks>
    /// Loopback by default so that an unconfigured deployment is not reachable from the network at
    /// all. Exposing this is a deliberate act, and it should be paired with
    /// <see cref="RequireEncryption"/> staying on.
    /// </remarks>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    public int Port { get; init; } = 2525;

    /// <summary>The name announced in the greeting and in <c>EHLO</c> responses.</summary>
    public string ServerName { get; init; } = "stylomail";

    /// <summary>
    /// The certificate used for <c>STARTTLS</c>. Null disables <c>STARTTLS</c> entirely.
    /// </summary>
    /// <remarks>
    /// With no certificate there is no <c>STARTTLS</c>, and therefore no <c>AUTH</c>, because
    /// authentication is never accepted on an unencrypted connection. A deployment that wants
    /// authenticated submission must supply one.
    /// </remarks>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>
    /// Whether mail may be accepted at all before the connection is encrypted.
    /// </summary>
    /// <remarks>
    /// <b>On by default, and the reason is the whole threat model.</b> Submission credentials travel
    /// inside the SMTP stream, so a plaintext session hands them to anyone on the path. Turning this
    /// off is defensible only for a loopback-to-loopback relay on a single host, and it is a
    /// separate, explicit decision rather than something a missing certificate quietly implies.
    /// </remarks>
    public bool RequireEncryption { get; init; } = true;

    /// <summary>
    /// Whether an unauthenticated connection may hand us mail at all.
    /// </summary>
    /// <remarks>
    /// On by default: this is the trusted-MTA handoff case, and the recipient-domain check below is
    /// what constrains it. With it off, every message needs credentials, which is the stricter
    /// posture and the right one for a deployment with no internal relay.
    /// </remarks>
    public bool AllowUnauthenticatedInbound { get; init; } = true;

    /// <summary>
    /// Domains we accept inbound mail for.
    /// </summary>
    /// <remarks>
    /// The entire inbound authorisation model. With no domains configured, unauthenticated inbound
    /// is refused outright, an empty configuration must not read as permission.
    /// </remarks>
    public RecipientDomainPolicy RecipientDomains { get; init; } = RecipientDomainPolicy.None;

    /// <summary>
    /// The tenant an unauthenticated inbound message is attributed to when the sink does not remap it.
    /// </summary>
    /// <remarks>
    /// A single placeholder rather than a guess. An inbound message arrives with no principal, and
    /// which tenant it belongs to is a routing question that depends on the recipient domain, a
    /// mapping the composition root owns, not the transport. This value is supplied so the submission
    /// can be constructed at all, and a sink that routes by domain is expected to replace it.
    /// </remarks>
    public string InboundTenantId { get; init; } = "inbound";

    /// <summary>
    /// Names this system is known by, used to spot a message that has already been through us.
    /// </summary>
    /// <remarks>
    /// Include every name an upstream MTA might write in a <c>Received</c> header's <c>by</c> clause
    /// when handing mail back to us, the public hostname, any relay alias. A name missing here is a
    /// loop that the hop limit still catches, but later and after more traffic.
    /// </remarks>
    public IReadOnlyList<string> LocalHostIdentities { get; init; } = [];

    /// <summary>Maximum hops already on a message before we refuse to add another.</summary>
    public int MaxHops { get; init; } = 20;

    public long MaxMessageBytes { get; init; } = 64L * 1024 * 1024;

    public int MaxRecipientsPerTransaction { get; init; } = 100;

    /// <summary>Concurrent connections accepted. Bounds file descriptors and per-connection state.</summary>
    public int MaxConcurrentConnections { get; init; } = 32;

    /// <summary>Commands allowed on one connection, so a client cannot hold it open with chatter.</summary>
    public int MaxCommandsPerSession { get; init; } = 200;

    /// <summary>Failed <c>AUTH</c> attempts before the connection is closed.</summary>
    public int MaxAuthAttempts { get; init; } = 3;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan DataTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan GreetingTimeout { get; init; } = TimeSpan.FromMinutes(1);

    public TransportHeaderLimits HeaderLimits { get; init; } = new();

    /// <summary>Validates the configuration. Called by the listener's constructor.</summary>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(BindAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(ServerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(InboundTenantId);
        ArgumentNullException.ThrowIfNull(RecipientDomains);
        ArgumentNullException.ThrowIfNull(LocalHostIdentities);
        ArgumentNullException.ThrowIfNull(HeaderLimits);

        if (Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port must be in 0-65535.");
        }

        // The hop marker this listener writes names ServerName in its `by` clause, and the loop guard
        // matches incoming `by` clauses against LocalHostIdentities. If the two disagree, a message
        // that loops back through this system is not recognised and only the hop limit stops it,         // a silent failure of the mechanism, discovered as a mail loop. Caught here instead.
        if (LocalHostIdentities.Count > 0
            && !LocalHostIdentities.Any(identity => string.Equals(identity, ServerName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"ServerName '{ServerName}' is not listed in LocalHostIdentities " +
                $"({string.Join(", ", LocalHostIdentities)}). This listener writes ServerName into the " +
                "`by` clause of the hop marker it adds, so a mismatch means a message looping back " +
                "through this system would not be recognised as a loop.");
        }

        if (RequireEncryption && Certificate is null)
        {
            // Refused at construction rather than at the first connection: a listener that would
            // answer 530 to every MAIL FROM is a configuration mistake, and finding out at startup
            // is better than finding out from the mail that bounced.
            throw new InvalidOperationException(
                "RequireEncryption is set but no Certificate was supplied, so STARTTLS cannot be " +
                "offered and no mail could ever be accepted. Supply a certificate, or set " +
                "RequireEncryption to false deliberately for a loopback-only relay.");
        }

        Positive(CommandTimeout, nameof(CommandTimeout));
        Positive(DataTimeout, nameof(DataTimeout));
        Positive(GreetingTimeout, nameof(GreetingTimeout));
        AtLeastOne(MaxHops, nameof(MaxHops));
        AtLeastOne(MaxRecipientsPerTransaction, nameof(MaxRecipientsPerTransaction));
        AtLeastOne(MaxConcurrentConnections, nameof(MaxConcurrentConnections));
        AtLeastOne(MaxCommandsPerSession, nameof(MaxCommandsPerSession));
        AtLeastOne(MaxAuthAttempts, nameof(MaxAuthAttempts));

        if (MaxMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes), MaxMessageBytes, "Must be positive.");
        }
    }

    private static void Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be positive.");
        }
    }

    private static void AtLeastOne(int value, string name)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be at least 1.");
        }
    }
}
