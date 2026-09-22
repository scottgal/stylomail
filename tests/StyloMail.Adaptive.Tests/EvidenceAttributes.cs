using StyloMail.Core;

namespace StyloMail.Adaptive.Tests;

/// <summary>
/// Reads <see cref="Evidence.Attributes"/>, which is a list of name/value pairs rather than a
/// map — so a repeated name is several entries, not one overwritten value.
/// </summary>
internal static class EvidenceAttributes
{
    /// <summary>The single value for a name that must appear exactly once.</summary>
    public static string Value(this Evidence evidence, string name) =>
        Assert.Single(evidence.Attributes!, attribute => attribute.Name == name).Value;

    /// <summary>Every value recorded under a name, in order. Empty when the name is absent.</summary>
    public static IReadOnlyList<string> Values(this Evidence evidence, string name) =>
        [.. (evidence.Attributes ?? []).Where(attribute => attribute.Name == name).Select(a => a.Value)];

    /// <summary>True when a name appears at least once with this value.</summary>
    public static bool Has(this Evidence evidence, string name, string value) =>
        evidence.Values(name).Contains(value);
}
