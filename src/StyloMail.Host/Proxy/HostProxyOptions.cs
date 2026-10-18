using StyloMail.AccessProxy.Hosting;

namespace StyloMail.Host.Proxy;

/// <summary>Deployment configuration for the access-proxy management backend.</summary>
/// <remarks>
/// <para>
/// The only thing a caller can change through the HTTP surface is an account or a listener's
/// <em>enabled</em> state. Bind address, port, TLS policy, certificate reference and tenant binding
/// stay here, in deployment configuration, because a live rebinding can strand connected clients and
/// cannot be undone through the API.
/// </para>
/// <para>
/// The two key variables are named here rather than read from the general configuration tree. A
/// login pseudonym key or a credential-protection key that could be supplied in
/// <c>appsettings.json</c> is a key that ends up in a repository, so the value is read from the
/// process environment only and never persisted beside the rows it protects.
/// </para>
/// </remarks>
public sealed class HostProxyOptions
{
    public const string SectionName = "StyloMail:Proxy";

    /// <summary>Whether this deployment composes the access proxy at all.</summary>
    /// <remarks>
    /// When false the management reads still answer with honest empty/not-produced facts, and the
    /// enrolment write refuses rather than inventing an unkeyed account store.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>Environment variable holding the HMAC key for tenant-keyed login pseudonyms.</summary>
    public string LoginKeyEnvironmentVariable { get; set; } = "STYLOMAIL_PROXY_LOGIN_KEY";

    /// <summary>Environment variable holding the 32-byte AES-256 credential-protection key.</summary>
    public string ProtectionKeyEnvironmentVariable { get; set; } = "STYLOMAIL_PROXY_PROTECTION_KEY";

    /// <summary>The key id recorded on credentials protected under the current key.</summary>
    public string ProtectionKeyId { get; set; } = "proxy-1";

    /// <summary>
    /// Optional deployment password variable for a password-protected certificate file.
    /// </summary>
    public string? CertificatePasswordEnvironmentVariable { get; set; }

    /// <summary>
    /// How long a disabled listener may take to drain before its remaining sessions are cut.
    /// </summary>
    /// <remarks>
    /// Disable itself never waits for this: the route returns as soon as the accept loop has been
    /// asked to stop, and sessions continue to their own bound. This is only the ceiling on a drain
    /// that outlives every session bound.
    /// </remarks>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long an abandoned staged submission may survive a crash before the sweep removes it.</summary>
    public TimeSpan SubmissionStagingTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long image observation rows are retained before expiry deletion. Bounded 1..365 days.
    /// </summary>
    public int ImageObservationRetentionDays { get; set; } = 30;

    /// <summary>Per-tenant cap on image outcomes. Volume cap in addition to time retention.</summary>
    public int ImageObservationMaxOutcomesPerTenant { get; set; } = 250_000;

    /// <summary>Per-tenant cap on serialized observation bytes. Payload plus fixed overhead.</summary>
    public long ImageObservationMaxUsedBytesPerTenant { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// Backend endpoints by provider id, e.g. <c>gmail</c>. Deployment configuration only.
    /// </summary>
    /// <remarks>
    /// The proxy never discovers a provider's host from a credential or a request: an endpoint that
    /// a client could influence is an endpoint an attacker could redirect a session to. A provider
    /// with no entry here fails closed before a backend socket opens.
    /// </remarks>
    public Dictionary<string, HostProxyProviderOptions> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<HostProxyListenerOptions> Listeners { get; set; } = [];
}

/// <summary>One backend provider's endpoints. Deployment configuration, never emitted by a route.</summary>
public sealed class HostProxyProviderOptions
{
    public string? ImapHost { get; set; }

    public int ImapPort { get; set; } = 993;

    /// <summary>Implicit TLS. False is accepted only for a loopback harness endpoint.</summary>
    public bool ImapTls { get; set; } = true;

    public string? Pop3Host { get; set; }

    public int Pop3Port { get; set; } = 995;

    public bool Pop3Tls { get; set; } = true;

    public string? SmtpHost { get; set; }

    public int SmtpPort { get; set; } = 465;

    /// <summary>
    /// SMTP submission requires TLS; this is true for every non-loopback endpoint.
    /// </summary>
    public bool SmtpTls { get; set; } = true;

    /// <summary>
    /// Opt-in root certificate for this provider's backend TLS only.
    /// </summary>
    /// <remarks>
    /// When set, the backend chain is validated against exactly this anchor instead of the system
    /// roots. Hostname and validity checks remain, there is no error override, and the anchor never
    /// reaches listener TLS or another provider. An unreadable, empty or expired file is a startup
    /// fault rather than a fallback to the public roots.
    /// </remarks>
    public string? CaCertificatePath { get; set; }
}

/// <summary>One tenant-bound listener from deployment configuration.</summary>
public sealed class HostProxyListenerOptions
{
    /// <summary>Stable identifier used by the management routes.</summary>
    public string ListenerId { get; set; } = string.Empty;

    /// <summary>The tenant every connection this listener accepts belongs to.</summary>
    public string TenantId { get; set; } = string.Empty;

    public ProxyListenerProtocol Protocol { get; set; } = ProxyListenerProtocol.Imap;

    /// <summary>Bind address. Deployment configuration, never mutable through the API.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Configured port. Zero asks the operating system for an ephemeral port.</summary>
    public int Port { get; set; }

    public ProxyTlsPolicy TlsPolicy { get; set; } = ProxyTlsPolicy.Required;

    /// <summary>Opaque deployment reference to the certificate; never emitted by a route.</summary>
    public string? CertificateReference { get; set; }

    /// <summary>Whether the listener starts with the host.</summary>
    public bool Enabled { get; set; }
}
