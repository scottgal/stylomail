using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using StyloMail.Host.Auth;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// <c>POST /v1/session</c> — exchanges an API key for a browser session cookie.
/// </summary>
/// <remarks>
/// Mapped only when the browser channel is enabled. The exchange requires the key in a request
/// header, which is what stops this endpoint itself being a CSRF vector: a hostile page cannot set
/// that header, so it cannot silently sign a visitor into an account of the attacker's choosing.
///
/// <para>
/// The session cookie carries a protected ticket, not the API key. Putting the key in the cookie
/// would mean a long-lived credential sitting in browser storage where script and extensions can
/// reach it, and it would make sign-out meaningless — the credential would still be valid.
/// </para>
/// </remarks>
public static class SessionEndpoints
{
    public static void Map(IEndpointRouteBuilder app, PrincipalDirectory directory)
    {
        if (!directory.BrowserChannelEnabled)
        {
            // Not mapped at all rather than mapped-and-refusing: a route that exists but always
            // fails invites a configuration change to "fix" it. Its absence says plainly that this
            // deployment has no browser surface.
            return;
        }

        app.MapPost("/v1/session", CreateAsync).AllowAnonymous();
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        PrincipalDirectory directory,
        IAntiforgery antiforgery)
    {
        if (!context.Request.Headers.TryGetValue(ApiKeyAuthenticationHandler.ApiKeyHeaderName, out var values))
        {
            return Results.Unauthorized();
        }

        var principal = directory.Resolve(values.ToString());
        if (principal is null)
        {
            return Results.Unauthorized();
        }

        var identity = HostIdentityExtensions.BuildIdentity(
            principal.PrincipalId,
            principal.TenantId,
            principal.ResolvePrivileges(),
            // The channel is recorded in the ticket, because every later request will present only
            // the cookie and the handler will have no other way to know how it got here.
            HostClaims.ChannelCookie,
            HostAuthenticationExtensions.CookieScheme);

        var signedIn = new System.Security.Claims.ClaimsPrincipal(identity);

        await context.SignInAsync(HostAuthenticationExtensions.CookieScheme, signedIn);

        // Adopt the identity for the remainder of this request before minting the token.
        //
        // SignInAsync writes the session cookie; it does not change who this request is. An
        // anti-forgery token is bound to the authenticated identity that generated it, so a token
        // minted while the request still looked anonymous would be rejected by every later request
        // that is correctly authenticated — protection that refuses the legitimate caller.
        context.User = signedIn;

        // GetAndStoreTokens both issues the anti-forgery cookie and returns the token the page must
        // echo back in the header. One is useless without the other, which is the point of the
        // double-submit.
        var tokens = antiforgery.GetAndStoreTokens(context);

        return Results.Ok(new
        {
            principalId = principal.PrincipalId,
            tenantId = principal.TenantId,
            csrfToken = tokens.RequestToken,
        });
    }
}
