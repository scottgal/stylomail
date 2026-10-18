using System.Security.Claims;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Policy;
using StyloMail.Host.Storage;
using StyloMail.Policy;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// The policy administration surface: pack lifecycle, bindings and the action capability catalogue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read on <c>Review</c>, mutate on <c>Administer</c>.</b> Reading which pack is effective is
/// reviewer-level visibility into what policy will do. Drafting, approving, activating, binding and
/// revoking change that, so they are administrative acts.
/// </para>
/// <para>
/// <b>Tenant and actor come from the authenticated principal, never from the body.</b> A caller
/// cannot author a pack for another tenant, and an audit stamp a caller can set is not an audit
/// stamp. The second approver is a principal id that must resolve, in this tenant, to a distinct
/// principal holding <c>Administer</c>.
/// </para>
/// <para>
/// <b>No route here calls the evaluator.</b> Phase 1 stores and serves policy; it does not apply it.
/// The capability endpoint is static and tenant-independent, and says so.
/// </para>
/// </remarks>
internal static class PolicyEndpoints
{
    // ---------------------------------------------------------------------------------------------
    // Packs
    // ---------------------------------------------------------------------------------------------

    /// <summary><c>GET /v1/policy/packs</c>, every version for the caller's tenant.</summary>
    internal static async Task<IResult> ListPacksAsync(
        ClaimsPrincipal user,
        SqlitePolicyPackStore packs,
        CancellationToken cancellationToken)
    {
        var tenant = user.TenantId();
        if (string.IsNullOrWhiteSpace(tenant))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        try
        {
            var summaries = await packs.ListAsync(tenant, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new PolicyPackListResponse
            {
                TenantId = tenant,
                Packs = [.. summaries.Select(PolicyPackSummaryResponse.From)],
            });
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The policy packs could not be read, so no answer is being given.");
        }
    }

    /// <summary><c>GET /v1/policy/packs/{id}</c>, the named version or the newest one.</summary>
    internal static async Task<IResult> GetPackAsync(
        string id,
        string? version,
        ClaimsPrincipal user,
        SqlitePolicyPackStore packs,
        CancellationToken cancellationToken)
    {
        var tenant = user.TenantId();
        if (string.IsNullOrWhiteSpace(tenant))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        try
        {
            var summaries = await packs.ListAsync(tenant, cancellationToken).ConfigureAwait(false);
            var summary = string.IsNullOrWhiteSpace(version)
                ? summaries.FirstOrDefault(entry => string.Equals(entry.PackId, id, StringComparison.Ordinal))
                : summaries.FirstOrDefault(entry =>
                    string.Equals(entry.PackId, id, StringComparison.Ordinal)
                    && string.Equals(entry.Version, version, StringComparison.Ordinal));

            if (summary is null)
            {
                return EndpointResults.NotFound("pack_not_found", "No such pack version for this tenant.");
            }

            var pack = await packs
                .GetVersionAsync(tenant, summary.PackId, summary.Version, cancellationToken)
                .ConfigureAwait(false);

            return pack is null
                ? EndpointResults.NotFound("pack_not_found", "No such pack version for this tenant.")
                : Results.Ok(PolicyPackResponse.From(pack, summary));
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The policy pack could not be read, so no answer is being given.");
        }
    }

    /// <summary><c>POST /v1/policy/packs</c>, create or edit a draft.</summary>
    internal static async Task<IResult> DraftPackAsync(
        PolicyPackDraftRequest? request,
        ClaimsPrincipal user,
        SqlitePolicyPackStore packs,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return EndpointResults.Invalid("body_required", "A pack body is required.");
        }

        var tenant = user.TenantId();
        var actor = user.PrincipalId();
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(actor))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        var pack = new PolicyPack
        {
            TenantId = tenant,
            Id = request.Id,
            Version = request.Version,

            // Author is the authenticated principal; a pack cannot be authored in someone else's name.
            Author = actor,

            // A draft has no approver yet. The approval step supplies one and validates the whole
            // pack against the contract before the row can leave the draft state.
            Approver = string.Empty,
            Context = request.Context,
            CategoryLevels = request.CategoryLevels,
            ActionCeiling = request.ActionCeiling,
            CaptureText = request.CaptureText,
            EvidenceRetentionDays = request.EvidenceRetentionDays,
            RuleSetVersion = request.RuleSetVersion,
            ScorerVersion = request.ScorerVersion,
            EffectiveFrom = request.EffectiveFrom,
            ReviewDue = request.ReviewDue,
            Supersedes = request.Supersedes,
        };

        try
        {
            var summary = await packs.SaveDraftAsync(tenant, pack, actor, cancellationToken).ConfigureAwait(false);
            return Results.Ok(PolicyPackResponse.From(pack, summary));
        }
        catch (PolicyValidationException ex)
        {
            return EndpointResults.Invalid(ex.Code, ex.Message);
        }
        catch (PolicyLifecycleException ex)
        {
            return EndpointResults.Conflict(ex.Code, ex.Message);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The pack draft could not be durably stored, so it was not accepted.");
        }
    }

    /// <summary><c>POST /v1/policy/packs/{id}/{version}/approve</c>.</summary>
    internal static async Task<IResult> ApprovePackAsync(
        string id,
        string version,
        PolicyPackApprovalRequest? request,
        ClaimsPrincipal user,
        SqlitePolicyPackStore packs,
        PrincipalDirectory principals,
        CancellationToken cancellationToken)
    {
        var tenant = user.TenantId();
        var approver = user.PrincipalId();
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(approver))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        if (!string.IsNullOrWhiteSpace(request?.SecondApprover))
        {
            var check = CheckAdministrator(tenant, request.SecondApprover, principals);
            if (check is not null)
            {
                return check;
            }
        }

        try
        {
            var summary = await packs
                .ApproveAsync(tenant, id, version, approver, request?.SecondApprover, cancellationToken)
                .ConfigureAwait(false);

            var pack = await packs.GetVersionAsync(tenant, id, version, cancellationToken).ConfigureAwait(false);
            return pack is null
                ? EndpointResults.NotFound("pack_not_found", "No such pack version for this tenant.")
                : Results.Ok(PolicyPackResponse.From(pack, summary));
        }
        catch (PolicyValidationException ex)
        {
            return EndpointResults.Invalid(ex.Code, ex.Message);
        }
        catch (PolicyLifecycleException ex)
        {
            return EndpointResults.Conflict(ex.Code, ex.Message);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The approval could not be durably stored, so it was not applied.");
        }
    }

    /// <summary><c>POST /v1/policy/packs/{id}/{version}/activate</c>.</summary>
    internal static async Task<IResult> ActivatePackAsync(
        string id,
        string version,
        ClaimsPrincipal user,
        SqlitePolicyPackStore packs,
        CancellationToken cancellationToken)
    {
        var tenant = user.TenantId();
        var actor = user.PrincipalId();
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(actor))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        try
        {
            var summary = await packs
                .ActivateAsync(tenant, id, version, actor, cancellationToken)
                .ConfigureAwait(false);

            var pack = await packs.GetVersionAsync(tenant, id, version, cancellationToken).ConfigureAwait(false);
            return pack is null
                ? EndpointResults.NotFound("pack_not_found", "No such pack version for this tenant.")
                : Results.Ok(PolicyPackResponse.From(pack, summary));
        }
        catch (PolicyValidationException ex)
        {
            return EndpointResults.Invalid(ex.Code, ex.Message);
        }
        catch (PolicyLifecycleException ex)
        {
            return EndpointResults.Conflict(ex.Code, ex.Message);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The activation could not be durably stored, so it was not applied.");
        }
    }

    /// <summary><c>POST /v1/policy/packs/{id}/{version}/withdraw</c>.</summary>
    internal static async Task<IResult> WithdrawPackAsync(
        string id,
        string version,
        PolicyPackTransitionRequest? request,
        ClaimsPrincipal user,
        SqlitePolicyPackStore packs,
        CancellationToken cancellationToken)
    {
        var tenant = user.TenantId();
        var actor = user.PrincipalId();
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(actor))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        try
        {
            var summary = await packs
                .WithdrawAsync(tenant, id, version, actor, request?.Reason, cancellationToken)
                .ConfigureAwait(false);
            var pack = await packs.GetVersionAsync(tenant, id, version, cancellationToken).ConfigureAwait(false);

            return pack is null
                ? EndpointResults.NotFound("pack_not_found", "No such pack version for this tenant.")
                : Results.Ok(PolicyPackResponse.From(pack, summary));
        }
        catch (PolicyLifecycleException ex)
        {
            return EndpointResults.Conflict(ex.Code, ex.Message);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The withdrawal could not be durably stored, so it was not applied.");
        }
    }

    /// <summary><c>POST /v1/policy/packs/{id}/{version}/deactivate</c>, returning to the legacy path.</summary>
    internal static async Task<IResult> DeactivatePackAsync(
        string id,
        string version,
        PolicyPackTransitionRequest? request,
        ClaimsPrincipal user,
        SqlitePolicyPackStore packs,
        CancellationToken cancellationToken)
    {
        var tenant = user.TenantId();
        var actor = user.PrincipalId();
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(actor))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        try
        {
            var summary = await packs
                .DeactivateAsync(tenant, id, version, actor, request?.Reason, cancellationToken)
                .ConfigureAwait(false);
            var pack = await packs.GetVersionAsync(tenant, id, version, cancellationToken).ConfigureAwait(false);

            return pack is null
                ? EndpointResults.NotFound("pack_not_found", "No such pack version for this tenant.")
                : Results.Ok(PolicyPackResponse.From(pack, summary));
        }
        catch (PolicyLifecycleException ex)
        {
            return EndpointResults.Conflict(ex.Code, ex.Message);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The deactivation could not be durably stored, so it was not applied.");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Bindings
    // ---------------------------------------------------------------------------------------------

    /// <summary><c>GET /v1/policy/bindings</c>, newest first, active by default.</summary>
    internal static async Task<IResult> ListBindingsAsync(
        BindingScope? scopeKind,
        string? scopeId,
        bool? activeOnly,
        ClaimsPrincipal user,
        SqlitePolicyBindingStore bindings,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenant = user.TenantId();
        if (string.IsNullOrWhiteSpace(tenant))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        try
        {
            var records = await bindings
                .ListAsync(tenant, scopeKind, scopeId, activeOnly ?? true, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new PolicyBindingListResponse
            {
                TenantId = tenant,
                Bindings = [.. records.Select(PolicyBindingResponse.From)],
            });
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The policy bindings could not be read, so no answer is being given.");
        }
    }

    /// <summary><c>POST /v1/policy/bindings</c>, a scoped, bounded manual exception.</summary>
    internal static async Task<IResult> CreateBindingAsync(
        PolicyBindingRequest? request,
        ClaimsPrincipal user,
        SqlitePolicyBindingStore bindings,
        PrincipalDirectory principals,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return EndpointResults.Invalid("body_required", "A binding body is required.");
        }

        var tenant = user.TenantId();
        var author = user.PrincipalId();
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(author))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        if (!string.IsNullOrWhiteSpace(request.ApprovedBy))
        {
            var check = CheckAdministrator(tenant, request.ApprovedBy, principals);
            if (check is not null)
            {
                return check;
            }
        }

        var binding = new PolicyBinding
        {
            Id = $"bnd_{Guid.NewGuid():N}",
            TenantId = tenant,
            ScopeKind = request.ScopeKind,
            ScopeValue = request.ScopeValue,
            Action = request.Action,
            CategoryId = request.CategoryId,
            TargetLevel = request.TargetLevel,
            PackId = request.PackId,
            PackVersion = request.PackVersion,
            Author = author,
            ApprovedBy = request.ApprovedBy,
            Reason = request.Reason,
            StartsAt = request.StartsAt,
            ExpiresAt = request.ExpiresAt,
            ReviewBy = request.ReviewBy,
            OnExpiry = request.OnExpiry,
        };

        try
        {
            var record = await bindings.CreateAsync(tenant, binding, author, cancellationToken).ConfigureAwait(false);
            return Results.Ok(PolicyBindingResponse.From(record));
        }
        catch (PolicyValidationException ex)
        {
            return EndpointResults.Invalid(ex.Code, ex.Message);
        }
        catch (PolicyLifecycleException ex)
        {
            return EndpointResults.Conflict(ex.Code, ex.Message);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The binding could not be durably stored, so it was not accepted.");
        }
    }

    /// <summary><c>POST /v1/policy/bindings/{id}/revoke</c>.</summary>
    internal static async Task<IResult> RevokeBindingAsync(
        string id,
        PolicyPackTransitionRequest? request,
        ClaimsPrincipal user,
        SqlitePolicyBindingStore bindings,
        CancellationToken cancellationToken)
    {
        var tenant = user.TenantId();
        var actor = user.PrincipalId();
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(actor))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        try
        {
            var record = await bindings
                .RevokeAsync(tenant, id, actor, request?.Reason, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(PolicyBindingResponse.From(record));
        }
        catch (PolicyLifecycleException ex)
        {
            return EndpointResults.Conflict(ex.Code, ex.Message);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The revocation could not be durably stored, so it was not applied.");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Capabilities
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>GET /v1/policy/capabilities</c>, the static catalogue. No tenant, pack or decision is read.
    /// </summary>
    internal static IResult Capabilities() => Results.Ok(new PolicyCapabilitiesResponse
    {
        Capabilities = PolicyCapabilityCatalog.All,
    });

    private static IResult? CheckAdministrator(string tenantId, string principalId, PrincipalDirectory principals)
    {
        var entry = principals.Inventory().FirstOrDefault(candidate =>
            string.Equals(candidate.PrincipalId, principalId, StringComparison.Ordinal));

        if (entry is null
            || !string.Equals(entry.TenantId, tenantId, StringComparison.Ordinal)
            || !entry.CanAuthenticate)
        {
            return EndpointResults.Invalid(
                "principal_not_found",
                $"'{principalId}' is not an authenticating principal in this tenant.");
        }

        if (!entry.Privileges.Contains("Administer", StringComparer.Ordinal))
        {
            return EndpointResults.Invalid(
                "principal_not_administrator",
                $"'{principalId}' does not hold Administer, so it cannot countersign a policy change.");
        }

        return null;
    }
}
