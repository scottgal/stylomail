using System.Security.Claims;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Profiles;
using StyloMail.Host.Storage;
using StyloMail.Profiles.Kpis;
using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// The evidence drawer's read surface: what a KPI was computed from, and what a stored evidence
/// reference points at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Review-only, tenant-scoped, and content-free.</b> No response here carries a message body, an
/// address, a filename or a subject. Source rows are opaque assessment ids with a timestamp, a
/// direction and an action; a reference resolves to property rows and whether the ledger row still
/// exists. The drawer explains the measurement; it does not open the mail.
/// </para>
/// <para>
/// <b>Never a bare id.</b> A reference that is missing returns a named <c>evidence_not_found</c>;
/// one whose retention has passed returns <c>Expired</c>; a withheld or unevaluated reference
/// reports that outcome rather than implying access.
/// </para>
/// </remarks>
internal static class EvidenceEndpoints
{
    private const int MaxIdLength = 256;

    /// <summary>
    /// <c>GET /v1/evidence/kpi/{definitionId}/{kind}/{id}</c>, the evidence behind one KPI.
    /// </summary>
    internal static async Task<IResult> GetKpiEvidenceAsync(
        string definitionId,
        string kind,
        string id,
        int? windowDays,
        ClaimsPrincipal user,
        ProfileQueryStore profiles,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!KpiDefinitions.TryGet(definitionId, out var definition))
        {
            return EndpointResults.Invalid(
                "unknown_kpi",
                $"'{definitionId}' is not a KPI this build defines.");
        }

        if (int.TryParse(kind, out _)
            || !Enum.TryParse<PropertyScopeKind>(kind, ignoreCase: true, out var parsedScope)
            || !Enum.IsDefined(parsedScope))
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
        if (parsedScope == PropertyScopeKind.Domain)
        {
            identity = identity.ToLowerInvariant();
        }

        if (identity.Length > MaxIdLength)
        {
            return EndpointResults.Invalid(
                "scope_too_long",
                $"A scope identifier may be at most {MaxIdLength} characters.");
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
            var scope = new PropertyScope(parsedScope, identity);
            var evidence = await profiles
                .ReadKpiEvidenceAsync(
                    user.TenantId()!,
                    scope,
                    definition.Id,
                    days,
                    clock.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(KpiEvidenceResponse.From(
                user.TenantId()!,
                PropertyScopeResponse.From(scope),
                clock.GetUtcNow(),
                evidence));
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The KPI evidence could not be read, so no answer is being given.");
        }
    }

    /// <summary>
    /// <c>GET /v1/evidence/reference?kind=Message&amp;id=...</c>, one stored evidence reference.
    /// </summary>
    internal static async Task<IResult> GetReferenceAsync(
        string? kind,
        string? id,
        ClaimsPrincipal user,
        ProfileQueryStore profiles,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return EndpointResults.Invalid(
                "kind_required",
                "An evidence kind is required, e.g. Message, ConversationTurn, Import or ModelTrace.");
        }

        // Numeric spellings are refused: an enum bound from "99" is an undefined member that only
        // reads as valid because of declaration order, and an evidence kind decides which backend
        // resolves the reference.
        if (int.TryParse(kind, out _)
            || !Enum.TryParse<EvidenceKind>(kind, ignoreCase: true, out var parsedKind)
            || !Enum.IsDefined(parsedKind))
        {
            return EndpointResults.Invalid(
                "unknown_evidence_kind",
                $"'{kind}' is not an evidence kind this build understands.");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return EndpointResults.Invalid("reference_required", "An evidence reference id is required.");
        }

        var referenceId = id.Trim();
        if (referenceId.Length > MaxIdLength)
        {
            return EndpointResults.Invalid(
                "reference_too_long",
                $"An evidence reference id may be at most {MaxIdLength} characters.");
        }

        try
        {
            var evidence = await profiles
                .ReadReferenceAsync(
                    user.TenantId()!,
                    parsedKind,
                    referenceId,
                    clock.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);

            if (evidence is null)
            {
                // No citation and no ledger row in this tenant. Not a bare id: the refusal names what
                // was looked for and where the answer would have come from.
                return EndpointResults.NotFound(
                    "evidence_not_found",
                    "No property cites that reference and no ledger row for this tenant carries it.");
            }

            return Results.Ok(ReferenceEvidenceResponse.From(user.TenantId()!, evidence));
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The evidence reference could not be read, so no answer is being given.");
        }
    }
}
