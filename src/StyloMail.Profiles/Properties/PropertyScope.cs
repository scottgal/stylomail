namespace StyloMail.Profiles.Properties;

/// <summary>Where a property value is bound.</summary>
/// <remarks>
/// <para>
/// The four scopes exist because a deployment knows things about a whole company that it does not
/// know about one sender, and because a conversation may legitimately narrow a binding without
/// redefining the entity. They are ordered from widest to narrowest by
/// <see cref="PropertyScopeKinds.Specificity"/>, which is what makes an override an override rather
/// than a second opinion.
/// </para>
/// <para>
/// <b>A scope identifier is opaque to this library.</b> An entity id is whatever the host calls a
/// principal, a domain is a domain name, a company is the company identifier the host already keeps,
/// and a conversation is the host's thread or conversation id. Nothing here joins them: the caller
/// supplies the chain it means, which is also what keeps the property store from inventing linkage
/// between people (spec §9.12, invariant 4).
/// </para>
/// </remarks>
public enum PropertyScopeKind
{
    /// <summary>A whole tenant-visible organisation. Widest.</summary>
    Company = 0,

    /// <summary>A domain, which may carry several entities.</summary>
    Domain = 1,

    /// <summary>One sender or recipient principal.</summary>
    Entity = 2,

    /// <summary>One conversation. Narrowest, and never visible from a wider read.</summary>
    Conversation = 3,
}

/// <summary>One scope binding: a kind and an opaque identifier within the tenant.</summary>
public readonly record struct PropertyScope(PropertyScopeKind Kind, string Id)
{
    public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>The ordering rules for scope kinds.</summary>
public static class PropertyScopeKinds
{
    /// <summary>
    /// How narrow a scope is. Higher is narrower, so a narrower scope breaks a precedence tie.
    /// </summary>
    /// <remarks>
    /// <b>Explicit rather than the enum's ordinal.</b> The enum is ordered widest-first for
    /// readability, and a future member inserted in the middle would silently reorder every
    /// precedence decision if the ordinal were the rank. The same hazard the host's JSON notes for
    /// stored enum names, applied to a value that is compared rather than written.
    /// </remarks>
    public static int Specificity(PropertyScopeKind kind) => kind switch
    {
        PropertyScopeKind.Company => 1,
        PropertyScopeKind.Domain => 2,
        PropertyScopeKind.Entity => 3,
        PropertyScopeKind.Conversation => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown scope kind."),
    };

    /// <summary>Whether <paramref name="candidate"/> is a legal ancestor of <paramref name="addressed"/>.</summary>
    /// <remarks>
    /// An entity read may consult its domain and company. A company read may consult nothing wider,
    /// and no wider read may ever consult a conversation: that is the rule that stops a conversation
    /// override leaking upward into the entity's default binding.
    /// </remarks>
    public static bool CanBeAncestorOf(PropertyScopeKind candidate, PropertyScopeKind addressed)
        => candidate != addressed && Specificity(candidate) < Specificity(addressed);
}

/// <summary>
/// The scopes a read resolves over, narrowest first.
/// </summary>
/// <remarks>
/// <para>
/// Built from the scope the caller addressed plus whichever wider scopes it supplied. The addressed
/// scope is always first, so resolution can never start from an ancestor the caller did not ask
/// about.
/// </para>
/// <para>
/// <b>A conversation scope is only ever present when a conversation was addressed.</b> The chain
/// constructor refuses it as an ancestor of anything wider, so "a conversation override must not
/// leak to the entity" is a property of the type rather than a rule every call site has to remember.
/// </para>
/// </remarks>
public sealed class PropertyScopeChain
{
    private PropertyScopeChain(IReadOnlyList<PropertyScope> scopes) => Scopes = scopes;

    /// <summary>The scopes to consult, narrowest first.</summary>
    public IReadOnlyList<PropertyScope> Scopes { get; }

    public PropertyScope Addressed => Scopes[0];

    /// <summary>
    /// Builds a chain for <paramref name="addressed"/> from the wider scopes the caller supplied.
    /// </summary>
    /// <param name="addressed">The scope being read, written or confirmed.</param>
    /// <param name="ancestors">
    /// Wider scopes the caller knows about. An entry whose kind is not wider than the addressed
    /// scope is refused rather than ignored: a caller that passed an entity as an ancestor of a
    /// domain read has misunderstood the model, and silently dropping it would produce an answer
    /// that looks complete.
    /// </param>
    public static PropertyScopeChain Create(PropertyScope addressed, params PropertyScope[] ancestors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressed.Id, nameof(addressed));

        var scopes = new List<PropertyScope> { addressed };

        foreach (var ancestor in ancestors)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ancestor.Id, nameof(ancestors));

            // A caller that names the addressed scope again is not confused, just redundant: the
            // console often has every identifier in one view and should not have to filter them.
            if (ancestor == addressed)
            {
                continue;
            }

            if (!PropertyScopeKinds.CanBeAncestorOf(ancestor.Kind, addressed.Kind))
            {
                throw new ArgumentException(
                    $"A {ancestor.Kind} scope cannot be an ancestor of a {addressed.Kind} scope. "
                    + "Only wider scopes are consulted, and a conversation is never consulted from above.",
                    nameof(ancestors));
            }

            scopes.Add(ancestor);
        }

        return new PropertyScopeChain(
            [.. scopes.OrderByDescending(scope => PropertyScopeKinds.Specificity(scope.Kind))]);
    }
}
