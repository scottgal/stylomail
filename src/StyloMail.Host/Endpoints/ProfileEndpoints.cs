using System.Security.Claims;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Profiles;
using StyloMail.Host.Storage;
using StyloMail.Profiles.Kpis;
using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// The profile surface: the KPI catalogue and one entity, domain, company or conversation profile.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read on <c>Review</c>.</b> A profile is evidence about people, so reading it is reviewer-level
/// visibility; there is no profile write here. Correcting a property is the property surface's act,
/// not a profile edit.
/// </para>
/// <para>
/// <b>Tenant comes from the principal and there is no tenant parameter.</b> A caller cannot name
/// another tenant's scope, so a cross-tenant profile read is absent rather than refused.
/// </para>
/// </remarks>
internal static class ProfileEndpoints
{
    private const int MaxScopeIdLength = 256;

    /// <summary><c>GET /v1/kpis</c>, the versioned definition catalogue.</summary>
    internal static IResult GetKpis()
        => Results.Ok(new KpiCatalogueResponse
        {
            Definitions = [.. KpiDefinitions.All.Select(KpiDefinitionResponse.From)],
        });

    /// <summary><c>GET /v1/profiles/{kind}/{id}</c>, one profile over a stated window.</summary>
    internal static async Task<IResult> GetProfileAsync(
        string kind,
        string id,
        int? windowDays,
        ClaimsPrincipal user,
        ProfileQueryStore profiles,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (int.TryParse(kind, out _)
            || !Enum.TryParse<PropertyScopeKind>(kind, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            return EndpointResults.Invalid(
                "unknown_scope",
                $"'{kind}' is not a profile scope. Supported: "
                + string.Join(", ", Enum.GetNames<PropertyScopeKind>().Select(name => name.ToLowerInvariant()))
                + ".");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return EndpointResults.Invalid("scope_required", "A scope identifier is required.");
        }

        var identity = id.Trim();

        // A domain is case-insensitive; the other identifiers are opaque and preserved exactly.
        if (parsed == PropertyScopeKind.Domain)
        {
            identity = identity.ToLowerInvariant();
        }

        if (identity.Length > MaxScopeIdLength)
        {
            return EndpointResults.Invalid(
                "scope_too_long",
                $"A scope identifier may be at most {MaxScopeIdLength} characters.");
        }

        var days = windowDays ?? ProfileQueryStore.DefaultWindowDays;

        if (days < ProfileQueryStore.MinimumWindowDays || days > ProfileQueryStore.MaximumWindowDays)
        {
            return EndpointResults.Invalid(
                "invalid_window",
                $"The window must be between {ProfileQueryStore.MinimumWindowDays} and "
                + $"{ProfileQueryStore.MaximumWindowDays} days.");
        }

        try
        {
            var snapshot = await profiles
                .ReadAsync(
                    user.TenantId()!,
                    new PropertyScope(parsed, identity),
                    days,
                    clock.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(ProfileResponse.From(user.TenantId()!, snapshot));
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The profile could not be read, so no answer is being given.");
        }
    }
}
