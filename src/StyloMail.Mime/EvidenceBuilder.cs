using StyloMail.Core;

namespace StyloMail.Mime;

/// <summary>Shorthand for building an <see cref="EvidenceAttribute"/>. See <see cref="Attr"/>.</summary>
internal static class Attr
{
    public static EvidenceAttribute Of(string name, string value) => new() { Name = name, Value = value };
}

/// <summary>
/// Small helper that stamps every deterministic signal this adapter emits with the same origin,
/// producer version and observation time.
/// </summary>
/// <remarks>
/// Centralising it means <see cref="EvidenceOrigin.Deterministic"/> cannot be forgotten on one
/// signal and quietly turn a reproducible fact into something a policy might treat as a model
/// opinion. Attributes are capped and truncated here so no single message can inflate the ledger.
/// </remarks>
internal sealed class EvidenceBuilder(DateTimeOffset observedAt, MimeParseLimits limits)
{
    public const string MessageScope = "message";

    public Evidence Build(
        string signalId,
        EvidenceAvailability availability,
        double? value = null,
        string? scope = null,
        IReadOnlyList<EvidenceAttribute>? attributes = null,
        int? sampleSupport = null)
    {
        return new Evidence
        {
            SignalId = signalId,
            Origin = EvidenceOrigin.Deterministic,
            Availability = availability,
            Value = value,
            Confidence = null,
            SampleSupport = sampleSupport,
            SourceVersion = MimeSignals.SourceVersion,
            ObservedAt = observedAt,
            ObservedScope = scope ?? MessageScope,
            Attributes = CapAttributes(attributes),
        };
    }

    /// <summary>
    /// Caps the number of attributes and the length of each value.
    /// </summary>
    /// <remarks>
    /// Repeated names are kept as-is. Several signals are genuinely multi-valued, coverage
    /// reasons, IDN hosts, homograph lists, and a reader that wants one of them is looking for a
    /// named entry among several, not for a unique key. Collapsing them would silently drop
    /// evidence, which is the failure this list shape exists to prevent.
    /// </remarks>
    public IReadOnlyList<EvidenceAttribute>? CapAttributes(IReadOnlyList<EvidenceAttribute>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
        {
            return null;
        }

        if (attributes.Count <= limits.MaxAttributeEntries &&
            attributes.All(a => a.Value.Length <= limits.MaxAttributeValueLength))
        {
            return attributes;
        }

        var capped = new List<EvidenceAttribute>(Math.Min(attributes.Count, limits.MaxAttributeEntries));
        foreach (var attribute in attributes)
        {
            if (capped.Count >= limits.MaxAttributeEntries)
            {
                break;
            }

            capped.Add(attribute with { Value = Truncate(attribute.Value, limits.MaxAttributeValueLength) });
        }

        return capped;
    }

    public static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, max), "…");
    }
}
