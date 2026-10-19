using System.Security.Claims;
using StyloMail.Host.Auth;
using StyloMail.Host.Profiles;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// <c>GET /v1/entities</c>: the bounded directory over the profiles the Host already keeps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read on <c>Review</c>.</b> A directory of who this tenant deals with is reviewer-level
/// visibility, and the route has no write. The tenant is the principal's and there is no parameter
/// to name another.
/// </para>
/// <para>
/// <b>It is a directory, not a feed.</b> The response carries no address, subject, filename or
/// content, and the evidence members are explicitly not produced here: per-entity evidence is the
/// profile route's job, computed over a stated window for one entity at a time.
/// </para>
/// </remarks>
internal static class EntityEndpoints
{
    internal const int MaxCursorLength = 1024;

    internal static IResult List(
        string? kind,
        string? query,
        int? limit,
        string? after,
        ClaimsPrincipal user,
        EntityDirectoryStore store,
        CancellationToken cancellationToken)
    {
        if (!EntityDirectoryStore.TryParseKind(kind, out var parsed))
        {
            return EndpointResults.Invalid(
                "unknown_kind",
                $"'{kind}' is not an entity kind. Supported: sender, recipient, domain, company.");
        }

        var effectiveLimit = limit ?? EntityDirectoryStore.DefaultLimit;

        if (effectiveLimit < 1)
        {
            return EndpointResults.Invalid(
                "invalid_limit",
                $"A limit must be at least 1. The maximum is {EntityDirectoryStore.MaxLimit}.");
        }

        if (query is { Length: > EntityDirectoryStore.MaxQueryLength })
        {
            return EndpointResults.Invalid(
                "query_too_long",
                $"A search term may be at most {EntityDirectoryStore.MaxQueryLength} characters. "
                + "Truncating it silently would search for something the caller did not ask for.");
        }

        if (after is not null && after.Length > MaxCursorLength)
        {
            return EndpointResults.Invalid(
                "invalid_cursor",
                "The cursor is longer than any this route issues. Echo NextCursor unchanged.");
        }

        try
        {
            return Results.Ok(
                store.Read(
                    user.TenantId()!,
                    parsed,
                    query,
                    effectiveLimit,
                    after,
                    cancellationToken));
        }
        catch (ArgumentException)
        {
            // The store's only ArgumentException on this path is a cursor that is malformed or was
            // minted for another kind. Naming it is kinder than silently restarting from page one,
            // which would look to a reader like the first page repeating forever.
            return EndpointResults.Invalid(
                "invalid_cursor",
                "The cursor is not one this directory issued. Echo NextCursor unchanged, or omit it "
                + "to start from the first page.");
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The entity directory could not be read, so no directory is being given.");
        }
    }
}
