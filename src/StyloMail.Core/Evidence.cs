namespace StyloMail.Core;

/// <summary>
/// A single observed signal about a message, its sender, its recipient, or their relationship.
/// </summary>
/// <remarks>
/// Evidence is what probabilistic components produce. It is deliberately <em>not</em> a
/// verdict: the final policy score is a separate field on <see cref="MailAssessment"/>,
/// and the two are never the same number.
/// </remarks>
public sealed record Evidence
{
    /// <summary>Stable identifier for this signal, e.g. <c>semantic.credential_request</c>.</summary>
    public required string SignalId { get; init; }

    public required EvidenceOrigin Origin { get; init; }

    public required EvidenceAvailability Availability { get; init; }

    /// <summary>
    /// The signal's numeric value where one exists — a Noul probability (0..1), a Score
    /// position across its levels (which may land between levels), or a deterministic
    /// count or ratio. <see langword="null"/> when the signal is non-numeric or unavailable.
    /// </summary>
    public double? Value { get; init; }

    /// <summary>
    /// Confidence reported by the classifier, 0..1: a summary of how peaked the answer
    /// distribution is.
    /// </summary>
    /// <remarks>
    /// <b>This is always <see langword="null"/> for Noul signals.</b> The TypeSafe API
    /// returns no confidence field for Noul — only the probability. Absence is meaningful
    /// and must not be defaulted to 0 or 1. For Noul, a value near 0.5 means genuinely
    /// balanced yes/no, which is not the same as "moderate" on any scale.
    /// </remarks>
    public double? Confidence { get; init; }

    /// <summary>
    /// How many observations or samples stand behind this signal, when the source reports it.
    /// Behavioural evidence (drift, velocity) requires sufficient support before it may be
    /// treated as meaningful; below that it stays masked.
    /// </summary>
    public int? SampleSupport { get; init; }

    /// <summary>Identifier of the producer — model id, rule set, or algorithm version.</summary>
    public required string SourceVersion { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>The scope this was observed over, e.g. <c>tenant</c>, <c>relationship</c>, <c>recipient</c>.</summary>
    public string? ObservedScope { get; init; }

    /// <summary>
    /// Signal-specific detail. Never carries message content.
    /// </summary>
    /// <remarks>
    /// <b>A list of pairs, not a map.</b> A dictionary silently collapses repeated keys, and
    /// evidence is frequently multi-valued — several reduced-coverage reasons, several IDN hosts,
    /// several homograph candidates. Under a map, "five reasons" became "one reason" with no error
    /// anywhere. Ordering is preserved and duplicates are meaningful.
    /// </remarks>
    public IReadOnlyList<EvidenceAttribute>? Attributes { get; init; }
}

/// <summary>One named detail on a piece of evidence.</summary>
public sealed record EvidenceAttribute
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}
