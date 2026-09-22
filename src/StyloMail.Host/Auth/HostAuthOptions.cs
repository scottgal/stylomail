namespace StyloMail.Host.Auth;

/// <summary>Bound from the <c>StyloMail:Auth</c> configuration section.</summary>
public sealed class HostAuthOptions
{
    public const string SectionName = "StyloMail:Auth";

    /// <summary>
    /// The principals this host will authenticate. A principal's tenant is a property of the
    /// configuration entry, never of the request.
    /// </summary>
    public List<HostPrincipalOptions> Principals { get; set; } = [];

    /// <summary>Name of the cookie used by the browser operator surface.</summary>
    public string CookieName { get; set; } = "stylomail.operator";

    /// <summary>
    /// Whether the browser cookie channel is accepted at all. Off by default: an API-only
    /// deployment has no CSRF surface, and the safest way to have no CSRF surface is to have
    /// no browser credential.
    /// </summary>
    public bool EnableBrowserCookieChannel { get; set; }

    /// <summary>
    /// How long a browser operator session lasts. Bounded rather than sliding: a session that
    /// renews on every request never ends, and an unattended workstation would keep an operator
    /// credential alive indefinitely.
    /// </summary>
    public TimeSpan CookieLifetime { get; set; } = TimeSpan.FromHours(8);
}

/// <summary>
/// One configured principal.
/// </summary>
/// <remarks>
/// <see cref="Key"/> is supplied by configuration — in a real deployment that means an environment
/// variable or a secret store, never a literal in this repository. It is compared by digest rather
/// than by string equality so that comparison time does not reveal how much of a key was correct.
/// </remarks>
public sealed class HostPrincipalOptions
{
    public string PrincipalId { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public List<string> Privileges { get; set; } = [];

    /// <summary>
    /// Sender identities this principal may use in SMTP <c>MAIL FROM</c>.
    /// </summary>
    /// <remarks>
    /// <b>Only consulted by the SMTP submission listener</b> — the HTTP surface takes the sender
    /// from the message it is handed, so there is nothing there for this to constrain. It exists
    /// because authenticating proves who you are, not that you may claim any sender: without it
    /// every valid account is a forgery primitive.
    ///
    /// <para>
    /// <b>Empty authorises nothing</b> beyond the null sender, which is always permitted because it
    /// is what bounces and DSNs use. That is deliberate rather than a default anyone should relax:
    /// "no restriction configured" and "may send as anyone" must not be the same value, or a
    /// forgotten configuration entry becomes a universal relay permission.
    /// </para>
    /// </remarks>
    public List<string> ApprovedSenderIdentities { get; set; } = [];

    public HostPrivilege ResolvePrivileges()
    {
        var privileges = HostPrivilege.None;

        foreach (var name in Privileges)
        {
            if (Enum.TryParse<HostPrivilege>(name, ignoreCase: true, out var parsed))
            {
                privileges |= parsed;
            }
        }

        return privileges;
    }
}
