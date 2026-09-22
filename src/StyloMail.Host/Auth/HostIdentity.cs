using System.Security.Claims;

namespace StyloMail.Host.Auth;

/// <summary>Claim types carried by an authenticated host principal.</summary>
public static class HostClaims
{
    public const string TenantId = "stylomail:tenant";
    public const string PrincipalId = "stylomail:principal";
    public const string Privilege = "stylomail:privilege";

    /// <summary>
    /// How the credential was presented, <c>header</c> or <c>cookie</c>. Recorded because the
    /// CSRF exposure differs between the two: a cookie is attached by the browser automatically.
    /// </summary>
    public const string Channel = "stylomail:channel";

    public const string ChannelHeader = "header";
    public const string ChannelCookie = "cookie";
}

/// <summary>
/// Accessors for the authenticated caller's authority.
/// </summary>
/// <remarks>
/// <b>Tenant authority is read from here and nowhere else.</b> The host never takes a tenant from a
/// request body, query string or message content. A caller may name a tenant in a payload; if it
/// disagrees with the authenticated principal's tenant the request is rejected rather than
/// honoured, because accepting it would turn a JSON field into an authority grant.
/// </remarks>
public static class HostIdentityExtensions
{
    public static string? TenantId(this ClaimsPrincipal principal)
        => principal.FindFirst(HostClaims.TenantId)?.Value;

    public static string? PrincipalId(this ClaimsPrincipal principal)
        => principal.FindFirst(HostClaims.PrincipalId)?.Value;

    public static bool WasPresentedAsCookie(this ClaimsPrincipal principal)
        => string.Equals(
            principal.FindFirst(HostClaims.Channel)?.Value,
            HostClaims.ChannelCookie,
            StringComparison.Ordinal);

    public static HostPrivilege Privileges(this ClaimsPrincipal principal)
    {
        var privileges = HostPrivilege.None;

        foreach (var claim in principal.FindAll(HostClaims.Privilege))
        {
            if (Enum.TryParse<HostPrivilege>(claim.Value, ignoreCase: true, out var parsed))
            {
                privileges |= parsed;
            }
        }

        return privileges;
    }

    public static bool HasPrivilege(this ClaimsPrincipal principal, HostPrivilege required)
        => (principal.Privileges() & required) == required;

    public static ClaimsIdentity BuildIdentity(
        string principalId,
        string tenantId,
        HostPrivilege privileges,
        string channel,
        string authenticationType)
    {
        var claims = new List<Claim>
        {
            new(HostClaims.PrincipalId, principalId),
            new(HostClaims.TenantId, tenantId),
            new(HostClaims.Channel, channel),

            // Anti-forgery tokens are bound to the identity that minted them, using the
            // NameIdentifier claim to tell one authenticated principal from another. Emitting it
            // is what makes a token unusable by a different principal; without it every
            // authenticated user shares one anonymous-looking identity for that binding.
            new(ClaimTypes.NameIdentifier, principalId),
        };

        foreach (var value in Enum.GetValues<HostPrivilege>())
        {
            if (value != HostPrivilege.None && privileges.HasFlag(value))
            {
                claims.Add(new Claim(HostClaims.Privilege, value.ToString()));
            }
        }

        return new ClaimsIdentity(claims, authenticationType);
    }
}
