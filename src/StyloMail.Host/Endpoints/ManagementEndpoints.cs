using System.Security.Claims;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Controls;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// The operator metadata routes: a sender's profile and the companies senders are grouped into.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read on <c>Review</c>, write on <c>Administer</c>.</b> That is the split the pause route already
/// draws: describing a sender and being permitted to stop one are different grants, and a reviewer who
/// may read who a sender is has no business renaming them. Nothing here can authorise a side effect on
/// mail: this is metadata about senders, not about messages.
/// </para>
/// <para>
/// Tenant comes from the authenticated principal and there is <b>no tenant parameter</b>, so a
/// cross-tenant read is absent rather than refused: there is nothing for a caller to name.
/// </para>
/// </remarks>
internal static class ManagementEndpoints
{
    // ---------------------------------------------------------------------------------------------
    // Sender settings
    // ---------------------------------------------------------------------------------------------

    internal static async Task<IResult> GetSenderSettingsAsync(
        string id,
        ClaimsPrincipal user,
        ISenderProfileStore profiles,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return EndpointResults.Invalid("principal_required", "A principal identifier is required.");
        }

        var profile = await profiles.GetAsync(user.TenantId()!, id, cancellationToken).ConfigureAwait(false);

        // A principal nobody has described is not a missing resource. The caller asked a sensible
        // question about a sender that exists, and the honest answer is "nothing recorded yet" rather
        // than a 404 that would read as "no such sender".
        return Results.Ok(SenderSettingsResponse.From(profile ?? SenderProfile.Unset(id)));
    }

    internal static async Task<IResult> PutSenderSettingsAsync(
        string id,
        SenderSettingsRequest? request,
        ClaimsPrincipal user,
        ISenderProfileStore profiles,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return EndpointResults.Invalid("principal_required", "A principal identifier is required.");
        }

        if (request is null)
        {
            return EndpointResults.Invalid("body_required", "A settings body is required.");
        }

        if (request.Posture is { Length: > 0 } posture && !SenderPostures.IsKnown(posture))
        {
            // Named values rather than a free string. `posture` is shown as a stance an operator
            // picked, and a typo stored as "trustd" would read as a stance nobody chose.
            return EndpointResults.Invalid(
                "unknown_posture",
                $"'{posture}' is not a posture this host recognises. Supported: "
                + string.Join(", ", SenderPostures.Known) + ".");
        }

        var tenantId = user.TenantId()!;

        await profiles
            .PutAsync(
                tenantId,
                new SenderProfile
                {
                    PrincipalId = id,
                    Label = Clean(request.Label),
                    CompanyId = Clean(request.CompanyId),
                    Notes = request.Notes,
                    ExternalRef = Clean(request.ExternalRef),
                    NotificationTarget = Clean(request.NotificationTarget),
                    Posture = Clean(request.Posture),

                    // Who decided, from the principal rather than the body. An audit stamp a caller
                    // can set is not an audit stamp.
                    UpdatedBy = user.PrincipalId() ?? "unknown",
                    UpdatedAt = clock.GetUtcNow(),
                },
                cancellationToken)
            .ConfigureAwait(false);

        var stored = await profiles.GetAsync(tenantId, id, cancellationToken).ConfigureAwait(false);

        return Results.Ok(SenderSettingsResponse.From(stored ?? SenderProfile.Unset(id)));
    }

    // ---------------------------------------------------------------------------------------------
    // Companies
    // ---------------------------------------------------------------------------------------------

    internal static async Task<IResult> ListCompaniesAsync(
        ClaimsPrincipal user,
        ICompanyStore companies,
        CancellationToken cancellationToken)
    {
        var listed = await companies.ListAsync(user.TenantId()!, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new CompanyListingResponse
        {
            TenantId = user.TenantId()!,
            Companies = [.. listed.Select(CompanyResponse.From)],
        });
    }

    internal static async Task<IResult> CreateCompanyAsync(
        CompanyRequest? request,
        ClaimsPrincipal user,
        ICompanyStore companies,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return EndpointResults.Invalid("name_required", "A company name is required.");
        }

        // Ids are minted here rather than accepted from the caller, so two tenants minting "acme"
        // cannot collide and an id is never something a client chose.
        var companyId = "co_" + Guid.NewGuid().ToString("N");

        return await SaveAsync(companyId, request, user, companies, clock, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static Task<IResult> UpdateCompanyAsync(
        string id,
        CompanyRequest? request,
        ClaimsPrincipal user,
        ICompanyStore companies,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult(
                EndpointResults.Invalid("company_required", "A company identifier is required."));
        }

        return SaveAsync(id, request, user, companies, clock, cancellationToken);
    }

    private static async Task<IResult> SaveAsync(
        string companyId,
        CompanyRequest? request,
        ClaimsPrincipal user,
        ICompanyStore companies,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return EndpointResults.Invalid("name_required", "A company name is required.");
        }

        var tenantId = user.TenantId()!;

        await companies
            .PutAsync(
                tenantId,
                new Company
                {
                    CompanyId = companyId,
                    Name = request.Name.Trim(),
                    Notes = request.Notes,
                    UpdatedBy = user.PrincipalId() ?? "unknown",
                    UpdatedAt = clock.GetUtcNow(),
                },
                cancellationToken)
            .ConfigureAwait(false);

        var stored = await companies.GetAsync(tenantId, companyId, cancellationToken).ConfigureAwait(false);

        return stored is null
            ? EndpointResults.StorageUnavailable("The company could not be read back after writing it.")
            : Results.Ok(CompanyResponse.From(stored));
    }

    /// <summary>Trims an optional field, turning blank into absent.</summary>
    /// <remarks>
    /// A field an operator emptied is absent, not a string of spaces. Storing the difference would
    /// make "cleared" and "never set" two states that render identically and compare differently.
    /// </remarks>
    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The postures an operator may choose.</summary>
/// <remarks>
/// A closed set, validated on write. <b>Nothing reads this yet</b>: see <see cref="SenderProfile"/>,
/// and it is deliberately a vocabulary rather than a free string: a stored stance that nothing
/// recognises is worse than no stance, because it looks like a decision someone made.
/// </remarks>
internal static class SenderPostures
{
    public const string Trusted = "trusted";
    public const string Normal = "normal";
    public const string Watch = "watch";

    public static IReadOnlyList<string> Known { get; } = [Trusted, Normal, Watch];

    public static bool IsKnown(string posture) =>
        Known.Contains(posture.Trim().ToLowerInvariant(), StringComparer.Ordinal);
}
