namespace StyloMail.Policy;

/// <summary>What an effective-binding read found for one scope.</summary>
public enum EffectiveBindingState
{
    /// <summary>At least one binding is selected for the scope.</summary>
    Bound = 0,

    /// <summary>A pack resolved, but no binding applies to this scope.</summary>
    NoBinding = 1,

    /// <summary>No effective pack resolved, so no binding can be described.</summary>
    NoPack = 2,

    /// <summary>The store could not be read, so the answer is unknown rather than empty.</summary>
    Unavailable = 3,
}

/// <summary>
/// A read-only description of the bindings that the evaluator would select for a profile scope.
/// </summary>
/// <remarks>
/// This is a view, not a decision. It never changes a level, writes a binding, or admits a message.
/// The binding list is produced by the same resolver the assessment path uses, including inherited
/// scopes and an expired <see cref="BindingOnExpiry.HoldForReview"/> binding.
/// </remarks>
public sealed record EffectiveBindingDescriptor
{
    public required string TenantId { get; init; }

    public required EffectiveBindingState State { get; init; }

    /// <summary>Stable reason when the state is not <see cref="EffectiveBindingState.Bound"/>.</summary>
    public string? Reason { get; init; }

    public string? PackId { get; init; }

    public string? PackVersion { get; init; }

    /// <summary>The addressed scope chain, narrowest first.</summary>
    public required IReadOnlyList<string> AddressedChain { get; init; }

    /// <summary>Selected bindings, ordered by category then binding id.</summary>
    public required IReadOnlyList<EffectiveBindingView> Bindings { get; init; }
}

/// <summary>One selected binding with the provenance the console header needs.</summary>
public sealed record EffectiveBindingView
{
    public required string BindingId { get; init; }

    public string? CategoryId { get; init; }

    public required BindingAction Action { get; init; }

    public ToleranceLevel? TargetLevel { get; init; }

    public required BindingScope ScopeKind { get; init; }

    public required string ScopeValue { get; init; }

    /// <summary>Narrower scopes have a higher rank; the resolver uses this order.</summary>
    public required int Precedence { get; init; }

    /// <summary>direct, inherited or tenant, describing how the binding reached this scope.</summary>
    public required string Source { get; init; }

    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset ReviewBy { get; init; }

    public required BindingOnExpiry OnExpiry { get; init; }

    /// <summary>True when the resolver selected this binding as the category target or the expiry hold.</summary>
    public required bool Selected { get; init; }

    public required bool Expired { get; init; }

    /// <summary>True when an expired HoldForReview binding still withholds the category.</summary>
    public required bool Withheld { get; init; }

    public string? PackId { get; init; }

    public string? PackVersion { get; init; }

    public required string Author { get; init; }

    public string? ApprovedBy { get; init; }
}
