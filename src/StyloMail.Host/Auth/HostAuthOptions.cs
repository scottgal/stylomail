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

    /// <summary>
    /// How long a resolved minted key is held before it is verified again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a backstop, not the mechanism.</b> Verifying a minted key is deliberately expensive,
    /// so a resolution is cached and the cache is invalidated by the store's own change counter,
    /// which every mint and every <c>key revoke</c> increments. Revocation therefore takes effect on
    /// the next request even though the revoking process is not the serving one.
    /// </para>
    /// <para>
    /// What this bounds is the case that counter cannot cover: a change written without bumping it,
    /// which would otherwise be served from a cache indefinitely. Thirty seconds of a credential
    /// that should have stopped is a bounded and visible failure; unbounded is not. Set it to
    /// <see cref="TimeSpan.Zero"/> to verify every minted key on every request, at the cost of one
    /// derivation per request.
    /// </para>
    /// <para>
    /// <b>This is a guard against a future edit, not against the world.</b> The counter is the
    /// mechanism and it is incremented in the same transaction as every change, so a missed bump is
    /// a code defect rather than a runtime condition this value exists to survive. Do not read it as
    /// load-bearing and weaken, skip or batch the counter on the strength of it; that would leave
    /// revocation resting on a timer, which is the defect the counter was introduced to remove.
    /// </para>
    /// </remarks>
    public TimeSpan ResolutionCacheLifetime { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// One configured principal.
/// </summary>
/// <remarks>
/// <see cref="Key"/> is supplied by configuration, in a real deployment that means an environment
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
    /// <b>Only consulted by the SMTP submission listener</b>, the HTTP surface takes the sender
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

    /// <summary>
    /// The flags these names describe.
    /// </summary>
    /// <remarks>
    /// Delegates rather than parsing. A second parser here would let the same name mean one thing
    /// when it arrived from configuration and another when it arrived from the store, which is the
    /// divergence wholesale precedence exists to prevent.
    /// </remarks>
    public HostPrivilege ResolvePrivileges() => HostPrivileges.Parse(Privileges);
}
