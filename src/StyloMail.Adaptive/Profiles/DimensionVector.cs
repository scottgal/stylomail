using StyloMail.Core;

namespace StyloMail.Adaptive.Profiles;

/// <summary>
/// One dimension's contribution to an observation: either a produced value, or an explicit
/// record that it was not produced and why.
/// </summary>
/// <remarks>
/// There is deliberately no way to build a sample that is unavailable <em>and</em> carries a
/// value. Defaulting a missing semantic dimension to <c>0.0</c> fabricates a calm, transactional,
/// no-lure message out of a provider outage, and the type refuses to represent that mistake.
/// </remarks>
public sealed record DimensionSample
{
    public required string DimensionId { get; init; }

    /// <summary><see langword="null"/> exactly when the dimension was not produced.</summary>
    public double? Value { get; init; }

    public required EvidenceAvailability Availability { get; init; }

    /// <summary>Why coverage was reduced, when it was. Never carries message content.</summary>
    public string? Note { get; init; }

    public static DimensionSample Available(string dimensionId, double value) => new()
    {
        DimensionId = dimensionId,
        Value = value,
        Availability = EvidenceAvailability.Available,
    };

    /// <summary>Produced, but over reduced input coverage: the value is real but weaker.</summary>
    public static DimensionSample Reduced(string dimensionId, double value, string note) => new()
    {
        DimensionId = dimensionId,
        Value = value,
        Availability = EvidenceAvailability.ReducedCoverage,
        Note = note,
    };

    /// <summary>
    /// Not produced. Pass <see cref="EvidenceAvailability.Unavailable"/> for a signal that
    /// could have existed, or <see cref="EvidenceAvailability.NotApplicable"/> for a question
    /// that does not apply at all.
    /// </summary>
    public static DimensionSample Missing(string dimensionId, EvidenceAvailability availability)
    {
        if (availability is EvidenceAvailability.Available or EvidenceAvailability.ReducedCoverage)
        {
            throw new ArgumentException(
                $"A missing sample cannot be '{availability}'; use Available or Reduced to supply a value.",
                nameof(availability));
        }

        return new DimensionSample
        {
            DimensionId = dimensionId,
            Value = null,
            Availability = availability,
        };
    }
}

/// <summary>
/// A behavioural vector where every dimension is either measured or explicitly masked.
/// </summary>
/// <remarks>
/// Masked dimensions are excluded from comparison, not filled. <see cref="Coverage"/> is
/// reported alongside so a distance computed over three dimensions is never mistaken for one
/// computed over twelve: the comparison is weaker, and saying so is the point.
/// </remarks>
public sealed record DimensionVector
{
    private readonly Dictionary<string, DimensionSample> _byId;

    private DimensionVector(IReadOnlyList<DimensionSample> dimensions)
    {
        Dimensions = dimensions;
        _byId = dimensions.ToDictionary(sample => sample.DimensionId, StringComparer.Ordinal);
        MaskedDimensionIds = [.. dimensions.Where(s => s.Value is null).Select(s => s.DimensionId)];
        Coverage = dimensions.Count == 0
            ? 0.0
            : (double)dimensions.Count(s => s.Value is not null) / dimensions.Count;
    }

    public IReadOnlyList<DimensionSample> Dimensions { get; }

    public IReadOnlyList<string> MaskedDimensionIds { get; }

    /// <summary>Fraction of dimensions actually produced. Masked dimensions reduce this.</summary>
    public double Coverage { get; }

    public static DimensionVector Create(params DimensionSample[] dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sample in dimensions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sample.DimensionId);

            if (!seen.Add(sample.DimensionId))
            {
                throw new ArgumentException(
                    $"Dimension '{sample.DimensionId}' appears more than once in the vector.",
                    nameof(dimensions));
            }

            var produced = sample.Availability is EvidenceAvailability.Available
                or EvidenceAvailability.ReducedCoverage;

            if (produced && sample.Value is null)
            {
                throw new ArgumentException(
                    $"Dimension '{sample.DimensionId}' is '{sample.Availability}' but carries no value.",
                    nameof(dimensions));
            }

            if (!produced && sample.Value is not null)
            {
                throw new ArgumentException(
                    $"Dimension '{sample.DimensionId}' is '{sample.Availability}' and must not carry a value.",
                    nameof(dimensions));
            }
        }

        return new DimensionVector(dimensions);
    }

    public bool IsMasked(string dimensionId) => ValueOf(dimensionId) is null;

    public bool IsReduced(string dimensionId) =>
        _byId.TryGetValue(dimensionId, out var sample)
        && sample.Availability == EvidenceAvailability.ReducedCoverage;

    public double? ValueOf(string dimensionId) =>
        _byId.TryGetValue(dimensionId, out var sample) ? sample.Value : null;

    public EvidenceAvailability? AvailabilityOf(string dimensionId) =>
        _byId.TryGetValue(dimensionId, out var sample) ? sample.Availability : null;
}
