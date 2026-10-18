namespace StyloMail.Profiles.Properties;

/// <summary>
/// How competing values for one key are reduced to the effective one.
/// </summary>
public enum PropertyProtectionPolicy
{
    /// <summary>
    /// The ordinary case: the strongest source wins, and a narrower scope breaks the tie.
    /// </summary>
    SourceOrder = 0,

    /// <summary>
    /// A safety category with a total order of protection: the most protective value wins regardless
    /// of which source or scope supplied it. Used for age bands, where a declared adult beside an
    /// inferred minor must get minor protections and an inferred adult must never relax a declared
    /// minor.
    /// </summary>
    MostProtective = 1,

    /// <summary>
    /// A value a model may not supply at all, whatever its confidence. Used for country, which is
    /// declared or verified and is never inferred from language or a top-level domain.
    /// </summary>
    HumanOnly = 2,
}

/// <summary>How a key's values are shaped and validated.</summary>
public enum PropertyValueKind
{
    /// <summary>One of a closed list.</summary>
    Enumerated = 0,

    /// <summary>A two-letter ISO 3166-1 alpha-2 code.</summary>
    CountryCode = 1,

    /// <summary>A BCP-47-shaped language tag, e.g. <c>en</c>, <c>pt-BR</c>.</summary>
    LanguageTag = 2,
}

/// <summary>
/// One property key's contract: what it accepts, who may supply it and how it competes.
/// </summary>
/// <remarks>
/// A definition lives in code rather than in a table because these are policy inputs: a tenant that
/// could add a key called <c>age_band</c> with no protection order would be able to disarm the
/// protective rule from the database.
/// </remarks>
public sealed record PropertyDefinition
{
    public required string Key { get; init; }

    /// <summary>The operator-facing name. The evidence drawer shows the key beside it.</summary>
    public required string Label { get; init; }

    public required PropertyProtectionPolicy Protection { get; init; }

    public required PropertyValueKind ValueKind { get; init; }

    /// <summary>The closed set for an <see cref="PropertyValueKind.Enumerated"/> key.</summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = [];

    /// <summary>
    /// Sources permitted to become effective for this key.
    /// </summary>
    /// <remarks>
    /// Narrower than "every source" only where the spec says so. Age bands accept every source
    /// because inference is explicitly allowed as weak evidence; country accepts only verified and
    /// declared, because a jurisdiction rule must trace to an accountable human or an accountable
    /// verification.
    /// </remarks>
    public required IReadOnlyList<PropertySource> AllowedSources { get; init; }
}
