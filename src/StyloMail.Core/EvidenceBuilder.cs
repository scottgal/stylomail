namespace StyloMail.Core;

/// <summary>
/// How much detail one signal may carry, so a single message cannot inflate the ledger.
/// </summary>
/// <remarks>
/// A bound rather than a shared limits type, so this lives in Core without Core having to know what
/// a MIME parser needs. Each producer maps its own configured limits onto it.
/// </remarks>
public sealed record EvidenceAttributeLimits
{
    public int MaxEntries { get; init; } = 8;

    public int MaxValueLength { get; init; } = 128;
}

/// <summary>
/// Small helper that stamps every deterministic signal a producer emits with the same origin,
/// producer version and observation time.
/// </summary>
/// <remarks>
/// <para>
/// Centralising it means <see cref="EvidenceOrigin.Deterministic"/> cannot be forgotten on one
/// signal and quietly turn a reproducible fact into something a policy might treat as a model
/// opinion. Attributes are capped and truncated here so no single message can inflate the ledger.
/// </para>
/// <para>
/// <b>This lives in Core because two channels now produce deterministic evidence.</b> A second
/// builder would mean the convention is enforced by two producers remembering it rather than by one
/// type, and the copy that drifts would be the one nobody re-reads. The producer version is a
/// parameter rather than a constant, which is what lets each channel stamp its own rules while
/// sharing the stamping.
/// </para>
/// </remarks>
public sealed class EvidenceBuilder(
    DateTimeOffset observedAt,
    string sourceVersion,
    EvidenceAttributeLimits limits)
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
            SourceVersion = sourceVersion,
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

        if (attributes.Count <= limits.MaxEntries &&
            attributes.All(a => a.Value.Length <= limits.MaxValueLength))
        {
            return attributes;
        }

        var capped = new List<EvidenceAttribute>(Math.Min(attributes.Count, limits.MaxEntries));
        foreach (var attribute in attributes)
        {
            if (capped.Count >= limits.MaxEntries)
            {
                break;
            }

            capped.Add(attribute with { Value = Truncate(attribute.Value, limits.MaxValueLength) });
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
