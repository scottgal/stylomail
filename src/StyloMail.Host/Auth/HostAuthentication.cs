using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace StyloMail.Host.Auth;

/// <summary>Named authorization policies, one per distinct privilege.</summary>
public static class HostPolicies
{
    public const string Assess = "stylomail.assess";
    public const string Send = "stylomail.send";
    public const string Review = "stylomail.review";
    public const string Feedback = "stylomail.feedback";
    public const string Administer = "stylomail.administer";
}

public static class HostAuthenticationExtensions
{
    /// <summary>Cookie scheme for the browser operator channel. Only active when enabled.</summary>
    public const string CookieScheme = "StyloMailOperator";

    /// <summary>Front scheme that picks the channel the request actually arrived on.</summary>
    public const string FrontScheme = "StyloMailFront";

    /// <summary>Header carrying the anti-forgery token for cookie-authenticated writes.</summary>
    public const string AntiforgeryHeader = "X-StyloMail-CSRF";

    /// <summary>
    /// Registers the host's authentication and authorization. There is no fallback scheme that
    /// authenticates everything: an unrecognised scheme is a configuration error, not an open door.
    /// </summary>
    public static IServiceCollection AddHostAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<HostAuthOptions>(configuration.GetSection(HostAuthOptions.SectionName));
        services.AddSingleton<PrincipalDirectory>();

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeader;
            options.Cookie.Name = "stylomail.csrf";

            // An anti-forgery cookie is not a session credential and must not be readable from
            // script: the token the page needs is served in the response body, not read back out
            // of this cookie.
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        var authentication = services
            .AddAuthentication(FrontScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName,
                _ => { })
            .AddCookie(CookieScheme, _ => { });

        // The cookie scheme is always registered but only ever *used* when the channel is enabled:
        // no session cookie is issued (the route is not mapped) and the selector below never
        // forwards to it. Registering it unconditionally is not a weakening, the schemes are
        // configured from options that are resolved per request, so a configuration change takes
        // effect without the registration decision having been made from a half-built
        // configuration at startup.
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<IOptions<HostAuthOptions>>((cookie, hostOptions) =>
            {
                var host = hostOptions.Value;

                cookie.Cookie.Name = host.CookieName;
                cookie.Cookie.HttpOnly = true;

                // Strict rather than Lax: a top-level navigation from another site must not carry
                // the operator session into a state-changing request.
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.SlidingExpiration = false;

                // Bounded rather than sliding: a session that renews on every request never ends,
                // and an unattended workstation would keep an operator credential alive forever.
                cookie.ExpireTimeSpan = host.CookieLifetime;
            });

        // The channel is chosen by what the request actually presented. A cookie wins when one is
        // present, because a browser that holds a session means to use it, and because that is the
        // case that needs the anti-forgery check downstream.
        authentication.AddPolicyScheme(FrontScheme, FrontScheme, options =>
            options.ForwardDefaultSelector = context =>
            {
                var host = context.RequestServices.GetRequiredService<IOptions<HostAuthOptions>>().Value;

                return host.EnableBrowserCookieChannel
                       && context.Request.Cookies.ContainsKey(host.CookieName)
                    ? CookieScheme
                    : ApiKeyAuthenticationHandler.SchemeName;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(HostPolicies.Assess, policy => policy.RequirePrivilege(HostPrivilege.Assess))
            .AddPolicy(HostPolicies.Send, policy => policy.RequirePrivilege(HostPrivilege.Send))
            .AddPolicy(HostPolicies.Review, policy => policy.RequirePrivilege(HostPrivilege.Review))
            .AddPolicy(HostPolicies.Feedback, policy => policy.RequirePrivilege(HostPrivilege.Feedback))
            .AddPolicy(HostPolicies.Administer, policy => policy.RequirePrivilege(HostPrivilege.Administer));

        return services;
    }

    private static void RequirePrivilege(
        this Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy,
        HostPrivilege privilege)
        => policy.RequireAssertion(context => context.User.HasPrivilege(privilege));
}
