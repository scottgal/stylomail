namespace StyloMail.Profiles.Properties;

/// <summary>How a stored property value came to exist.</summary>
/// <remarks>
/// <para>
/// The vocabulary is the spec's (§9.16): <c>verified</c>, <c>declared</c>, <c>inferred</c>, plus
/// <c>default</c> and <c>import</c> for the two system-shaped values a deployment also needs.
/// </para>
/// <para>
/// <b>Source is evidence, not the pill.</b> The console shows Verified / Declared / Suggested, and
/// those are derived from this plus the human-confirmation marker in
/// <see cref="PropertySurfaceStates"/>. A source word a caller sent is never enough on its own to
/// present a value as human-defined: that is what the confirmation marker is for, and deriving the
/// pill from the source alone is the defect the marker exists to prevent.
/// </para>
/// </remarks>
public enum PropertySource
{
    /// <summary>A deployment default, used only when nothing stronger exists.</summary>
    Default = 0,

    /// <summary>A model or classifier proposed it. Weak evidence by construction.</summary>
    Inferred = 1,

    /// <summary>Transferred from a tenant system of record. Not human-set, so still Suggested.</summary>
    Import = 2,

    /// <summary>A human set it, or a human confirmed a value that came from somewhere else.</summary>
    Declared = 3,

    /// <summary>The deployment's verification mechanism checked it.</summary>
    Verified = 4,
}

/// <summary>
/// How much authority a source carries when two values compete.
/// </summary>
/// <remarks>
/// <b>Explicit rather than the enum's ordinal</b>, for the same reason scope specificity is: the
/// enum happens to be ordered weakest-first today, and a precedence decision that depends on the
/// declaration order is one edit away from being silently wrong. <c>Default</c> is the weakest
/// because it is a fallback, not a statement about the entity.
/// </remarks>
public static class PropertySourceRank
{
    public static int Of(PropertySource source) => source switch
    {
        PropertySource.Default => 0,
        PropertySource.Inferred => 1,
        PropertySource.Import => 2,
        PropertySource.Declared => 3,
        PropertySource.Verified => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown property source."),
    };
}

/// <summary>The vocabulary the console is allowed to show a non-technical operator.</summary>
/// <remarks>
/// <b>Suggested is the umbrella for everything no human defined</b>, including an import and a
/// default, and it always carries a confirm action. The evidence drawer still exposes the source
/// word, so "suggested" never hides that a value came from a model rather than from a spreadsheet.
/// <see cref="Unknown"/> is not a pill: it is the honest answer when no unexpired value exists at
/// any consulted scope, and it exists so that "not measured" can never be rendered as a value.
/// </remarks>
public enum PropertySurfaceState
{
    /// <summary>No unexpired value at any consulted scope. Not a pill, and never a bare zero.</summary>
    Unknown = 0,

    /// <summary>A model, default or import proposed it; no human has confirmed it.</summary>
    Suggested = 1,

    /// <summary>A human set or confirmed it.</summary>
    Declared = 2,

    /// <summary>The deployment's verification mechanism checked it.</summary>
    Verified = 3,
}

/// <summary>
/// Derives the surface state from the stored facts, and from nothing else.
/// </summary>
public static class PropertySurfaceStates
{
    /// <summary>
    /// The pill for a value that exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Confirmation is a recorded event, never an absence.</b> A value is Declared when a human
    /// set it (source <c>declared</c>) or when <paramref name="confirmedBy"/> names the human who
    /// confirmed it. It is <em>not</em> derived from a missing override, a matching default, or the
    /// absence of a lower-scope row: those are the readings that make a system silently claim human
    /// authority it was never given.
    /// </para>
    /// <para>
    /// A verified value stays Verified even after a human confirms it. Confirming a verified fact
    /// records the human event without demoting the mechanism that checked it.
    /// </para>
    /// </remarks>
    public static PropertySurfaceState For(PropertySource source, string? confirmedBy)
    {
        if (source == PropertySource.Verified)
        {
            return PropertySurfaceState.Verified;
        }

        return source == PropertySource.Declared || !string.IsNullOrWhiteSpace(confirmedBy)
            ? PropertySurfaceState.Declared
            : PropertySurfaceState.Suggested;
    }
}
