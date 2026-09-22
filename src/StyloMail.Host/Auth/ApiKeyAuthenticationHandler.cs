using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace StyloMail.Host.Auth;

/// <summary>
/// Authenticates a caller by API key presented in a header.
/// </summary>
/// <remarks>
/// A header, not a cookie, is the default channel on purpose. Browsers attach cookies to
/// cross-site requests automatically, so a cookie credential is reachable by a hostile page
/// unless it is paired with anti-forgery protection. A custom header is not attached
/// automatically, which makes it unusable as a cross-site credential.
/// </remarks>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "StyloMailApiKey";

    /// <summary>Header carrying the caller's API key.</summary>
    public const string ApiKeyHeaderName = "X-StyloMail-Key";

    private readonly PrincipalDirectory _directory;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        PrincipalDirectory directory)
        : base(options, logger, encoder)
    {
        _directory = directory;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var headerValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = _directory.Resolve(headerValues.ToString());
        if (principal is null)
        {
            // No detail about which part was wrong, and no tenant hint.
            return Task.FromResult(AuthenticateResult.Fail("Unrecognised API key."));
        }

        // The resolved principal carries its privileges already parsed, from whichever source
        // resolved it. Nothing here reads configuration, so this channel and the SMTP one cannot
        // disagree about what a key may do.
        var identity = HostIdentityExtensions.BuildIdentity(
            principal.PrincipalId,
            principal.TenantId,
            principal.Privileges,
            HostClaims.ChannelHeader,
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(identity), SchemeName)));
    }
}
