using System.Net;
using System.Security.Cryptography.X509Certificates;
using StyloMail.Transport.Cloudflare;
using StyloMail.Transport.Ingress;
using StyloMail.Transport.Smtp;

namespace StyloMail.Host.Hosting;

/// <summary>
/// Bound from the <c>StyloMail:Transport</c> configuration section: what this deployment ingests
/// from, and where it delivers to.
/// </summary>
/// <remarks>
/// <para>
/// A plain configuration record rather than a binding straight onto the transport's own option
/// types. <see cref="SmtpIngressOptions"/> and <see cref="CloudflareIngressOptions"/> carry a
/// certificate, an <see cref="IPAddress"/> and a <see cref="RecipientDomainPolicy"/>, none of which
/// a configuration binder can produce honestly, a policy that arrived by binder magic would have no
/// single place where "no domains means no inbound" is visible. Everything is spelled out here so
/// the translation to the transport's types is one readable hop.
/// </para>
/// <para>
/// <b>Nothing in here is enabled by default.</b> An unconfigured deployment runs the HTTP host and
/// the CLI and opens no SMTP port and dials no upstream. That is the safe default for a component
/// whose whole job is to sit on a mail boundary.
/// </para>
/// </remarks>
public sealed class HostTransportOptions
{
    public const string SectionName = "StyloMail:Transport";

    public SmtpIngressSettings SmtpIngress { get; set; } = new();

    public CloudflareSettings CloudflareIngress { get; set; } = new();

    public UpstreamSettings Upstream { get; set; } = new();

    /// <summary>The SMTP submission listener's settings.</summary>
    public sealed class SmtpIngressSettings
    {
        /// <summary>Whether to open the listener at all. Off by default.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Address to bind. Loopback by default, so enabling the listener without also deciding to
        /// expose it does not put a mail port on the network.
        /// </summary>
        public string BindAddress { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 2525;

        public string ServerName { get; set; } = "stylomail";

        /// <summary>PKCS#12 file for <c>STARTTLS</c>. A path is not a secret.</summary>
        public string? CertificatePath { get; set; }

        /// <summary>
        /// Password for <see cref="CertificatePath"/>.
        /// </summary>
        /// <remarks>
        /// Supplied by configuration, in a real deployment that means an environment variable or a
        /// secret store, never a literal in this repository, exactly as
        /// <see cref="Auth.HostPrincipalOptions.Key"/> is. It is never logged and never placed in an
        /// exception message.
        /// </remarks>
        public string? CertificatePassword { get; set; }

        /// <summary>
        /// Whether mail may be accepted before the connection is encrypted.
        /// </summary>
        /// <remarks>
        /// <b>Defaults to true, and it is not a bug if that makes the listener refuse to start.</b>
        /// With encryption required and no certificate there is no <c>STARTTLS</c> and therefore no
        /// <c>AUTH</c>, so no submission could ever be accepted, and the listener refuses to be
        /// constructed rather than answering rejection to every client. Turning this off is a
        /// separate, explicit decision for a loopback relay, never something a missing certificate
        /// quietly implies.
        /// </remarks>
        public bool RequireEncryption { get; set; } = true;

        /// <summary>Whether an unauthenticated connection may hand us mail at all.</summary>
        public bool AllowUnauthenticatedInbound { get; set; } = true;

        /// <summary>
        /// Domains this deployment accepts inbound mail for. <b>Empty means no inbound</b>, a
        /// missing configuration must not read as a universal relay.
        /// </summary>
        public List<string> RecipientDomains { get; set; } = [];

        /// <summary>The tenant an unauthenticated inbound message is attributed to.</summary>
        public string InboundTenantId { get; set; } = "inbound";

        /// <summary>
        /// Names this system is known by, for the inbound loop guard. Must include
        /// <see cref="ServerName"/>, which is what our own hop marker is written under, the
        /// listener refuses to start if the two disagree.
        /// </summary>
        public List<string> LocalHostIdentities { get; set; } = [];

        public long MaxMessageBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>Whether an authenticated principal is required before mail is accepted.</summary>
        public bool IsConfigured => Enabled;

        public SmtpIngressOptions Build()
        {
            if (!IPAddress.TryParse(BindAddress, out var bind))
            {
                throw new InvalidOperationException(
                    $"StyloMail:Transport:SmtpIngress:BindAddress '{BindAddress}' is not a valid IP " +
                    "address. Refusing to guess an interface to listen on.");
            }

            return new SmtpIngressOptions
            {
                BindAddress = bind,
                Port = Port,
                ServerName = ServerName,
                Certificate = LoadCertificate(),
                RequireEncryption = RequireEncryption,
                AllowUnauthenticatedInbound = AllowUnauthenticatedInbound,
                RecipientDomains = new RecipientDomainPolicy(RecipientDomains),
                InboundTenantId = InboundTenantId,
                LocalHostIdentities = LocalHostIdentities,
                MaxMessageBytes = MaxMessageBytes,
            };
        }

        /// <summary>Loads the configured certificate, or null when none was configured.</summary>
        /// <remarks>
        /// <see cref="X509CertificateLoader"/> rather than the obsolete constructor: the older form
        /// is a build error under this repository's analyzer settings, and rightly so.
        /// <c>DefaultKeySet</c> rather than <c>EphemeralKeySet</c>: an ephemeral private key cannot
        /// be used by <c>SslStream</c> for server authentication on every platform.
        /// </remarks>
        private X509Certificate2? LoadCertificate()
        {
            if (string.IsNullOrWhiteSpace(CertificatePath))
            {
                return null;
            }

            if (!File.Exists(CertificatePath))
            {
                throw new InvalidOperationException(
                    $"StyloMail:Transport:SmtpIngress:CertificatePath '{CertificatePath}' does not " +
                    "exist. Refusing to start a listener that could not offer STARTTLS.");
            }

            return X509CertificateLoader.LoadPkcs12FromFile(
                CertificatePath,
                CertificatePassword,
                X509KeyStorageFlags.DefaultKeySet);
        }
    }

    /// <summary>The Cloudflare Email Routing inbound connector's settings.</summary>
    public sealed class CloudflareSettings
    {
        /// <summary>Whether the connector is constructed at all. Off by default.</summary>
        public bool Enabled { get; set; }

        public List<string> RecipientDomains { get; set; } = [];

        public string InboundTenantId { get; set; } = "inbound";

        public string ConnectorId { get; set; } = "cloudflare-email-routing";

        public List<string> LocalHostIdentities { get; set; } = [];

        /// <summary>The name written into our hop marker's <c>by</c> clause.</summary>
        public string? ByHost { get; set; }

        public long MaxMessageBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>
        /// Builds the connector's options.
        /// </summary>
        /// <param name="sharedSecret">
        /// The Worker's secret, read from the environment, <b>not</b> from this configuration object.
        /// There is deliberately no <c>SharedSecret</c> property here: a secret with a config key is
        /// a secret that ends up in an appsettings file and then in a repository, and the whole point
        /// of <see cref="HostCredentials.CloudflareIngressSecretEnvironmentVariable"/> is that the
        /// name is defined once and the value is never written down.
        /// </param>
        public CloudflareIngressOptions Build(string? sharedSecret) => new()
        {
            SharedSecret = sharedSecret,
            RecipientDomains = new RecipientDomainPolicy(RecipientDomains),
            InboundTenantId = InboundTenantId,
            ConnectorId = ConnectorId,
            LocalHostIdentities = LocalHostIdentities,
            ByHost = ByHost,
            MaxMessageBytes = MaxMessageBytes,
        };
    }

    /// <summary>Where accepted mail is delivered.</summary>
    /// <remarks>
    /// Exactly one target and no routing table, which is the spec's "behind an established MTA"
    /// constraint made physical: MX selection, retry policy, DSN generation and reputation stay with
    /// the MTA that already owns them.
    /// </remarks>
    public sealed class UpstreamSettings
    {
        /// <summary>The upstream host. <b>Unset means this deployment does not deliver.</b></summary>
        public string? Host { get; set; }

        public int Port { get; set; } = 587;

        /// <summary>Required, Opportunistic or None.</summary>
        public string Tls { get; set; } = "Required";

        /// <summary>True for the SMTPS convention (port 465): encrypted before the greeting.</summary>
        public bool ImplicitTls { get; set; }

        /// <summary>Username for <c>AUTH</c>, or null for an upstream that needs none.</summary>
        public string? Username { get; set; }

        /// <summary>
        /// Password for <c>AUTH</c>. Supplied by configuration, from the environment or a secret
        /// store in a real deployment, never a literal here, never logged.
        /// </summary>
        public string? Password { get; set; }

        /// <summary>The name announced in <c>EHLO</c>.</summary>
        public string HeloName { get; set; } = "localhost";

        /// <summary>Whether a delivery target is configured at all.</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

        public SmtpUpstream Build()
        {
            if (!IsConfigured)
            {
                throw new InvalidOperationException(
                    "No upstream is configured, so no delivery port can be built. Check " +
                    "HostTransportOptions.Upstream.IsConfigured before calling this.");
            }

            if (!Enum.TryParse<SmtpTlsMode>(Tls, ignoreCase: true, out var tls))
            {
                throw new InvalidOperationException(
                    $"StyloMail:Transport:Upstream:Tls '{Tls}' is not one of Required, Opportunistic " +
                    "or None. Refusing to guess how much transport security was meant.");
            }

            var hasCredentials = !string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Password);

            return new SmtpUpstream
            {
                Host = Host!,
                Port = Port,
                Tls = tls,
                ImplicitTls = ImplicitTls,
                HeloName = HeloName,

                // Both halves or neither. A username with no password is a configuration mistake
                // that would otherwise present as an authentication failure at the upstream, long
                // after the change that caused it.
                Credentials = hasCredentials
                    ? new SmtpCredentials
                    {
                        Username = Username
                            ?? throw new InvalidOperationException(
                                "StyloMail:Transport:Upstream:Password is set but Username is not."),
                        Password = Password
                            ?? throw new InvalidOperationException(
                                "StyloMail:Transport:Upstream:Username is set but Password is not."),
                    }
                    : null,
            };
        }
    }
}
