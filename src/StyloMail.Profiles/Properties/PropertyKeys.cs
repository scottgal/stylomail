namespace StyloMail.Profiles.Properties;

/// <summary>
/// The property keys this build understands, and the rules that make each one safe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Missing evidence is a value, not an absence.</b> A key with nothing recorded resolves to
/// <see cref="PropertySurfaceState.Unknown"/> explicitly, so a caller never has to read a null as
/// "no data" or as "false".
/// </para>
/// <para>
/// <b>Age band is the only key with a protection order, and that is deliberate.</b> The spec (§9.16)
/// makes protective precedence a rule about safety categories, not a general tiebreak: applying an
/// ordinal to a language or a role would invent a ranking those values do not have. Country gets
/// <see cref="PropertyProtectionPolicy.HumanOnly"/> instead, because jurisdiction has no "more
/// protective" country, only an accountable source requirement.
/// </para>
/// </remarks>
public static class PropertyKeys
{
    public const string AgeBand = "age_band";
    public const string Country = "country";
    public const string Role = "role";
    public const string Language = "language";

    /// <summary>Every key v1 serves, in the order the console should present them.</summary>
    public static readonly IReadOnlyList<string> All = [AgeBand, Country, Role, Language];

    private static readonly Dictionary<string, PropertyDefinition> ByKey =
        new(StringComparer.Ordinal)
        {
            [AgeBand] = new PropertyDefinition
            {
                Key = AgeBand,
                Label = "Age band",
                Protection = PropertyProtectionPolicy.MostProtective,
                ValueKind = PropertyValueKind.Enumerated,
                AllowedValues = AgeBands.All,
                AllowedSources =
                [
                    PropertySource.Verified,
                    PropertySource.Declared,
                    PropertySource.Import,
                    PropertySource.Inferred,
                    PropertySource.Default,
                ],
            },
            [Country] = new PropertyDefinition
            {
                Key = Country,
                Label = "Country",

                // Human-only in the sense that matters: no model may supply it. An import writes
                // as declared with the import principal as the actor, so jurisdiction traces to an
                // accountable human or an accountable import rather than to a guess.
                Protection = PropertyProtectionPolicy.HumanOnly,
                ValueKind = PropertyValueKind.CountryCode,
                AllowedSources = [PropertySource.Verified, PropertySource.Declared],
            },
            [Role] = new PropertyDefinition
            {
                Key = Role,
                Label = "Role",
                Protection = PropertyProtectionPolicy.SourceOrder,
                ValueKind = PropertyValueKind.Enumerated,
                AllowedValues = Roles.All,
                AllowedSources =
                [
                    PropertySource.Verified,
                    PropertySource.Declared,
                    PropertySource.Import,
                    PropertySource.Inferred,
                    PropertySource.Default,
                ],
            },
            [Language] = new PropertyDefinition
            {
                Key = Language,
                Label = "Language",
                Protection = PropertyProtectionPolicy.SourceOrder,
                ValueKind = PropertyValueKind.LanguageTag,
                AllowedSources =
                [
                    PropertySource.Verified,
                    PropertySource.Declared,
                    PropertySource.Import,
                    PropertySource.Inferred,
                    PropertySource.Default,
                ],
            },
        };

    /// <summary>Looks a key up, case-sensitively. Keys are a contract, not prose.</summary>
    public static bool TryGet(string? key, out PropertyDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(key) && ByKey.TryGetValue(key, out var found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }

    /// <summary>The definition, or a refusal naming the key the caller sent.</summary>
    public static PropertyDefinition Require(string key)
        => TryGet(key, out var definition)
            ? definition
            : throw new ArgumentException($"'{key}' is not a property key this build understands.", nameof(key));

    /// <summary>
    /// Normalises and validates a value for a key.
    /// </summary>
    /// <remarks>
    /// Normalisation happens before storage so that <c>GB</c> and <c>gb</c> can never be two stored
    /// facts, and so that a surface state computed from a stored row is computed from one spelling.
    /// A refusal names why: "invalid" tells an operator nothing they can act on.
    /// </remarks>
    public static bool TryNormalise(
        PropertyDefinition definition,
        string? raw,
        out string normalised,
        out string? reason)
    {
        normalised = string.Empty;
        reason = null;

        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            reason = $"{definition.Label} needs a value.";
            return false;
        }

        switch (definition.ValueKind)
        {
            case PropertyValueKind.Enumerated:
            {
                var candidate = value.ToLowerInvariant();
                if (!definition.AllowedValues.Contains(candidate, StringComparer.Ordinal))
                {
                    reason = $"'{value}' is not a {definition.Label} this build understands. "
                        + "Supported values: " + string.Join(", ", definition.AllowedValues) + ".";
                    return false;
                }

                normalised = candidate;
                return true;
            }

            case PropertyValueKind.CountryCode:
            {
                if (value.Length != 2 || !value.All(char.IsAsciiLetter))
                {
                    reason = $"'{value}' is not a two-letter ISO 3166-1 country code.";
                    return false;
                }

                normalised = value.ToUpperInvariant();
                return true;
            }

            case PropertyValueKind.LanguageTag:
            {
                if (!IsLanguageTag(value))
                {
                    reason = $"'{value}' is not a BCP-47-shaped language tag, such as 'en' or 'pt-BR'.";
                    return false;
                }

                normalised = value;
                return true;
            }

            default:
                reason = "This build has no value rule for that key.";
                return false;
        }
    }

    /// <summary>
    /// How much protection a value asserts for a key that has a protection order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Age bands only. <c>unknown</c> ranks below every known band because it asserts no protection:
    /// it must never beat a declared or default band in a protective comparison, while a known band
    /// always may.
    /// </para>
    /// <para>
    /// A key without a protection order returns <c>0</c> for every value, which makes the protective
    /// comparison inert and lets the ordinary source order decide. That is the correct answer for a
    /// role or a language: there is nothing to protect.
    /// </para>
    /// </remarks>
    public static int ProtectionRank(string key, string value)
    {
        if (!string.Equals(key, AgeBand, StringComparison.Ordinal))
        {
            return 0;
        }

        return AgeBands.Rank(value);
    }

    /// <summary>Whether a source may ever become the effective value for this definition.</summary>
    public static bool AcceptsSource(PropertyDefinition definition, PropertySource source)
        => definition.AllowedSources.Contains(source);

    // A BCP-47 tag's shape, not a registry check. Validating against the full registry would add a
    // dependency and a version of the world to pin; accepting a syntactically valid tag that no
    // locale uses is a smaller error than rejecting a real one a tenant's system emitted.
    private static bool IsLanguageTag(string value)
    {
        var subtags = value.Split('-');
        if (subtags.Length == 0 || subtags[0].Length is < 2 or > 3)
        {
            return false;
        }

        if (!subtags[0].All(char.IsAsciiLetter))
        {
            return false;
        }

        return subtags.Skip(1).All(subtag =>
            subtag.Length is >= 1 and <= 8 && subtag.All(char.IsAsciiLetterOrDigit));
    }
}

/// <summary>The age bands, their order and their protection rank.</summary>
/// <remarks>
/// <b>The only table in this build where a value asserts a protection.</b> The rank is a total order
/// over the bands, so "most protective" is well defined for every pair. <c>unknown</c> is a real
/// band an operator may declare, and it ranks lowest because it protects nobody.
/// </remarks>
public static class AgeBands
{
    public const string Under13 = "under_13";
    public const string From13To15 = "13_15";
    public const string From16To17 = "16_17";
    public const string From18To24 = "18_24";
    public const string From25 = "25_plus";
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All =
        [Under13, From13To15, From16To17, From18To24, From25, Unknown];

    /// <summary>Higher means more protective, never "more confident".</summary>
    public static int Rank(string value) => value switch
    {
        Under13 => 5,
        From13To15 => 4,
        From16To17 => 3,
        From18To24 => 2,
        From25 => 1,
        Unknown => 0,

        // Not a band. Ranked below unknown rather than above it: an unrecognised value must never
        // win a protective comparison by accident.
        _ => -1,
    };
}

/// <summary>The roles a tenant may bind to an entity.</summary>
public static class Roles
{
    public const string Vendor = "vendor";
    public const string Customer = "customer";
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All = [Vendor, Customer, Unknown];
}
