using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Profiles;
using StyloMail.Host.Storage;
using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// The entity property surface: effective values with their provenance, and the recorded human
/// events that change them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read on <c>Review</c>, mutate on <c>Administer</c>.</b> The split the management surface
/// already draws: reading what policy will think about an entity is reviewer-level visibility, and
/// setting or confirming it changes what policy will do, which is an administrative act. Confirming
/// is on the write side deliberately: it is a recorded human assertion, not a read.
/// </para>
/// <para>
/// <b>Tenant comes from the authenticated principal and there is no tenant parameter.</b> A caller
/// cannot name another tenant's scope at all, so a cross-tenant read is absent rather than refused.
/// </para>
/// <para>
/// <b>Every answer carries provenance.</b> There is no route here that returns a bare value; a bare
/// value would make "no evidence" indistinguishable from "not measured", which is the distinction
/// the whole property model exists to keep.
/// </para>
/// </remarks>
internal static class PropertyEndpoints
{
    /// <summary>
    /// How long an opaque scope identifier may be. A bound rather than a convention: an unbounded
    /// identifier becomes a stored primary key of whatever size a caller chose.
    /// </summary>
    private const int MaxScopeIdLength = 256;

    /// <summary><c>GET /v1/properties/{kind}/{id}</c>, the effective binding at one scope.</summary>
    internal static async Task<IResult> GetEffectiveAsync(
        string kind,
        string id,
        string? keys,
        string? entity,
        string? domain,
        string? company,
        ClaimsPrincipal user,
        IPropertyStore properties,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (TryReadScope(kind, id, out var scope) is { } scopeError)
        {
            return scopeError;
        }

        if (TryBuildChain(scope, entity, domain, company, out var chain) is { } chainError)
        {
            return chainError;
        }

        if (TryReadKeys(keys, out var requested) is { } keyError)
        {
            return keyError;
        }

        try
        {
            var effective = await properties
                .GetEffectiveAsync(user.TenantId()!, chain, requested, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new PropertyReadResponse
            {
                TenantId = user.TenantId()!,
                Scope = PropertyScopeResponse.From(scope),
                ResolvedAt = clock.GetUtcNow(),
                Properties = [.. effective.Select(PropertyResponse.From)],
            });
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The entity properties could not be read, so no answer is being given.");
        }
    }

    /// <summary><c>PUT /v1/properties/{kind}/{id}/{key}</c>, set a value with its provenance.</summary>
    internal static async Task<IResult> SetAsync(
        string kind,
        string id,
        string key,
        PropertySetRequest? request,
        ClaimsPrincipal user,
        IPropertyStore properties,
        IOptions<PropertyOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (TryReadScope(kind, id, out var scope) is { } scopeError)
        {
            return scopeError;
        }

        if (TryReadKey(key, out var definition) is { } keyError)
        {
            return keyError;
        }

        if (request is null)
        {
            return EndpointResults.Invalid("body_required", "A property body is required.");
        }

        if (!TryReadSource(request.Source, out var source))
        {
            return EndpointResults.Invalid(
                "unknown_source",
                $"'{request.Source}' is not a property source. Supported: "
                + string.Join(", ", Enum.GetNames<PropertySource>().Select(name => name.ToLowerInvariant()))
                + ".");
        }

        var now = clock.GetUtcNow();

        var write = new PropertyWrite
        {
            Scope = scope,
            Key = definition.Key,
            Value = request.Value ?? string.Empty,
            Source = source,

            // From the principal, never from the body. An audit stamp a caller can set is not an
            // audit stamp.
            Actor = user.PrincipalId() ?? "unknown",
            Confidence = request.Confidence,
            Model = request.Model is null
                ? null
                : new ModelContribution
                {
                    Id = request.Model.Id?.Trim() ?? string.Empty,
                    Version = request.Model.Version?.Trim() ?? string.Empty,
                    PromptContract = request.Model.PromptContract?.Trim() ?? string.Empty,
                    Spans = request.Model.Spans ?? [],
                },
            EvidenceRefs = [.. (request.EvidenceRefs ?? []).Select(reference => new EvidenceReference
            {
                Id = reference.Id?.Trim() ?? string.Empty,
                Kind = reference.Kind,
                Label = reference.Label?.Trim(),
                ObservedAt = reference.ObservedAt,
                Retention = reference.Retention,
                RetentionUntil = reference.RetentionUntil,
                AccessOutcome = reference.AccessOutcome,
            })],
            Coverage = request.Coverage is null
                ? null
                : new PropertyCoverage
                {
                    Analysed = request.Coverage.Analysed,
                    Total = request.Coverage.Total,
                    WindowStart = request.Coverage.WindowStart,
                    WindowEnd = request.Coverage.WindowEnd,
                    UnavailableReason = request.Coverage.UnavailableReason?.Trim(),
                },
            ExpiresAt = request.ExpiresAt,
            Reason = request.Reason,
        };

        var check = PropertyWriteRules.Check(write, options.Value.AllowInferredWrites, now);
        if (!check.Accepted)
        {
            return EndpointResults.Invalid(check.ErrorCode!, check.Detail!);
        }

        var checkedWrite = write with { Value = check.NormalisedValue! };

        try
        {
            var stored = await properties
                .SetAsync(user.TenantId()!, checkedWrite, now, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(PropertyWriteResponse.From(
                stored,
                PropertyResolver.Resolve(definition, [stored], now)));
        }
        catch (ArgumentException ex)
        {
            // Defence in depth: the write rules reject these first, but a caller that reaches the
            // store with an unusable reference (two claims for one identity, an empty id) must get
            // a named refusal rather than a 500 from the exception.
            return EndpointResults.Invalid("invalid_property_write", ex.Message);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The property could not be durably stored, so it was not accepted.");
        }
    }

    /// <summary>
    /// <c>POST /v1/properties/{kind}/{id}/{key}/confirm</c>, a recorded human confirmation.
    /// </summary>
    internal static async Task<IResult> ConfirmAsync(
        string kind,
        string id,
        string key,
        PropertyConfirmRequest? request,
        ClaimsPrincipal user,
        IPropertyStore properties,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (TryReadScope(kind, id, out var scope) is { } scopeError)
        {
            return scopeError;
        }

        if (TryReadKey(key, out var definition) is { } keyError)
        {
            return keyError;
        }

        try
        {
            var result = await properties
                .ConfirmAsync(
                    user.TenantId()!,
                    scope,
                    definition.Key,
                    user.PrincipalId() ?? "unknown",
                    request?.Reason,
                    clock.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Applied)
            {
                return EndpointResults.NotFound(
                    "property_not_set",
                    "Nothing is set for that property at that scope, so there is nothing to confirm. "
                    + "A confirmation is never inferred from an inherited value.");
            }

            return Results.Ok(PropertyWriteResponse.From(result.Stored, result.Effective));
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The confirmation could not be durably recorded, so it was not applied.");
        }
    }

    /// <summary><c>DELETE /v1/properties/{kind}/{id}/{key}</c>, remove one scope's binding.</summary>
    internal static async Task<IResult> ClearAsync(
        string kind,
        string id,
        string key,
        [FromBody] PropertyClearRequest? request,
        ClaimsPrincipal user,
        IPropertyStore properties,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (TryReadScope(kind, id, out var scope) is { } scopeError)
        {
            return scopeError;
        }

        if (TryReadKey(key, out var definition) is { } keyError)
        {
            return keyError;
        }

        try
        {
            var result = await properties
                .ClearAsync(
                    user.TenantId()!,
                    scope,
                    definition.Key,
                    user.PrincipalId() ?? "unknown",
                    request?.Reason,
                    clock.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Applied)
            {
                return EndpointResults.NotFound(
                    "property_not_set",
                    "Nothing is set for that property at that scope, so there is nothing to clear. "
                    + "Clearing a wider binding is a different, explicit act.");
            }

            return Results.Ok(PropertyWriteResponse.From(result.Stored, result.Effective));
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The clear could not be durably recorded, so it was not applied.");
        }
    }

    /// <summary><c>GET /v1/properties/{kind}/{id}/{key}/history</c>, newest first.</summary>
    internal static async Task<IResult> GetHistoryAsync(
        string kind,
        string id,
        string key,
        int? limit,
        ClaimsPrincipal user,
        IPropertyStore properties,
        CancellationToken cancellationToken)
    {
        if (TryReadScope(kind, id, out var scope) is { } scopeError)
        {
            return scopeError;
        }

        if (TryReadKey(key, out var definition) is { } keyError)
        {
            return keyError;
        }

        try
        {
            var events = await properties
                .GetHistoryAsync(user.TenantId()!, scope, definition.Key, limit ?? 50, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new PropertyHistoryResponse
            {
                TenantId = user.TenantId()!,
                Scope = PropertyScopeResponse.From(scope),
                Key = definition.Key,
                Events = [.. events.Select(PropertyAuditEventResponse.From)],
            });
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The property history could not be read, so no answer is being given.");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------------------------------------

    private static IResult? TryReadScope(string kind, string id, out PropertyScope scope)
    {
        scope = default;

        // Numeric spellings are refused: an enum parsed from "2" depends on declaration order, and
        // an order-dependent scope name is the same trap the host refuses for stored enum values.
        if (int.TryParse(kind, out _)
            || !Enum.TryParse<PropertyScopeKind>(kind, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            return EndpointResults.Invalid(
                "unknown_scope",
                $"'{kind}' is not a property scope. Supported: "
                + string.Join(", ", Enum.GetNames<PropertyScopeKind>().Select(name => name.ToLowerInvariant()))
                + ".");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return EndpointResults.Invalid("scope_required", "A scope identifier is required.");
        }

        var identity = NormaliseScopeIdentity(parsed, id);

        if (identity.Length > MaxScopeIdLength)
        {
            return EndpointResults.Invalid(
                "scope_too_long",
                $"A scope identifier may be at most {MaxScopeIdLength} characters.");
        }

        scope = new PropertyScope(parsed, identity);
        return null;
    }

    private static string NormaliseScopeIdentity(PropertyScopeKind kind, string id)
    {
        var identity = id.Trim();

        // A domain name is case-insensitive, so storing "Example.COM" beside "example.com" would be
        // two bindings for one domain and an override that silently fails to apply. The other scope
        // identifiers are opaque and are preserved exactly as the host issued them.
        return kind == PropertyScopeKind.Domain ? identity.ToLowerInvariant() : identity;
    }

    private static IResult? TryReadKey(string key, out PropertyDefinition definition)
    {
        if (!PropertyKeys.TryGet(key, out definition))
        {
            return EndpointResults.Invalid(
                "unknown_property_key",
                $"'{key}' is not a property key this build understands. Supported: "
                + string.Join(", ", PropertyKeys.All) + ".");
        }

        return null;
    }

    private static IResult? TryReadKeys(string? keys, out IReadOnlyList<string> requested)
    {
        if (string.IsNullOrWhiteSpace(keys))
        {
            requested = PropertyKeys.All;
            return null;
        }

        var parsed = new List<string>();

        foreach (var raw in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!PropertyKeys.TryGet(raw, out var definition))
            {
                requested = [];
                return EndpointResults.Invalid(
                    "unknown_property_key",
                    $"'{raw}' is not a property key this build understands. Supported: "
                    + string.Join(", ", PropertyKeys.All) + ".");
            }

            if (!parsed.Contains(definition.Key, StringComparer.Ordinal))
            {
                parsed.Add(definition.Key);
            }
        }

        if (parsed.Count == 0)
        {
            requested = [];
            return EndpointResults.Invalid("keys_required", "At least one property key is required.");
        }

        requested = parsed;
        return null;
    }

    private static bool TryReadSource(string? raw, out PropertySource source)
    {
        source = PropertySource.Declared;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        return !int.TryParse(raw, out _)
            && Enum.TryParse(raw, ignoreCase: true, out source)
            && Enum.IsDefined(source);
    }

    private static IResult? TryBuildChain(
        PropertyScope scope,
        string? entity,
        string? domain,
        string? company,
        out PropertyScopeChain chain)
    {
        chain = PropertyScopeChain.Create(scope);

        var ancestors = new List<PropertyScope>();

        foreach (var (name, value, kind) in new[]
                 {
                     ("entity", entity, PropertyScopeKind.Entity),
                     ("domain", domain, PropertyScopeKind.Domain),
                     ("company", company, PropertyScopeKind.Company),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var ancestor = new PropertyScope(kind, NormaliseScopeIdentity(kind, value));

            if (ancestor.Id.Length > MaxScopeIdLength)
            {
                return EndpointResults.Invalid(
                    "scope_too_long",
                    $"A scope identifier may be at most {MaxScopeIdLength} characters.");
            }

            if (ancestor == scope)
            {
                continue;
            }

            if (!PropertyScopeKinds.CanBeAncestorOf(kind, scope.Kind))
            {
                return EndpointResults.Invalid(
                    "scope_not_ancestor",
                    $"A {kind.ToString().ToLowerInvariant()} scope cannot be consulted from a "
                    + $"{scope.Kind.ToString().ToLowerInvariant()} read. Only wider scopes are "
                    + "consulted, and a conversation is never consulted from above.");
            }

            ancestors.Add(ancestor);
        }

        chain = PropertyScopeChain.Create(scope, [.. ancestors]);
        return null;
    }
}
