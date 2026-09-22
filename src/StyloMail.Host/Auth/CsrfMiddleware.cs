using Microsoft.AspNetCore.Antiforgery;

namespace StyloMail.Host.Auth;

/// <summary>
/// Requires an anti-forgery token on any state-changing request that was authenticated by cookie.
/// </summary>
/// <remarks>
/// The check is conditional on the <em>channel</em>, not on the route. Requiring a token from every
/// caller would burden API clients that are not exposed to CSRF at all, they authenticate with a
/// header a browser will not attach on a hostile page's behalf, while doing nothing extra for
/// them. Requiring it only where an ambient credential was used puts the cost exactly where the
/// risk is.
///
/// <para>
/// This runs after authentication, because the channel is a property of the authenticated
/// principal rather than of the raw request.
/// </para>
/// </remarks>
public sealed class CsrfMiddleware
{
    private static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS", "TRACE"];

    private readonly RequestDelegate _next;

    public CsrfMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (RequiresValidation(context) && !await IsValidAsync(context, antiforgery))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "csrf_token_invalid",
                detail = "A cookie-authenticated request that changes state must carry a valid " +
                         $"anti-forgery token in the {HostAuthenticationExtensions.AntiforgeryHeader} header.",
            });
            return;
        }

        await _next(context);
    }

    private static bool RequiresValidation(HttpContext context)
        => !SafeMethods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase)
           && context.User.Identity?.IsAuthenticated == true
           && context.User.WasPresentedAsCookie();

    private static async Task<bool> IsValidAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
