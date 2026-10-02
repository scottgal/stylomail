namespace StyloMail.Cascade;

/// <summary>
/// Where the local model is trusted, stated as a table rather than scattered through comparisons.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trust set is empty, and that is a claim rather than an omission.</b> There is no
/// per-dimension accuracy evidence for the local model on this system today: the corpus manifest
/// plants the semantic tier's AVAILABILITY and never a value, because its own generator rules that
/// "the value is the model's judgement and quoting it would be a detection claim the corpus cannot
/// make". So a table naming dimensions to distrust would be an assertion wearing a table's clothes.
/// What fills this table is per-dimension agreement against the hosted arm, with its population, and
/// an entry here cites that population.
/// </para>
/// <para>
/// <b>The band is where the mission's "confidence band" lives, and it is a band on the VALUE.</b> The
/// provider port reports no confidence for a Noul row at all (<c>Evidence.Confidence</c> is null by
/// the port's own ruling, because the API returns no such field), so a threshold on confidence would
/// be a threshold on a field that never holds a number. What a Noul row does carry is a probability
/// whose 0.5 means "genuinely balanced yes/no", so the indecisive region is a band around 0.5 and
/// that is what this type states.
/// </para>
/// <para>
/// <b>Half a width rather than two bounds, because the band is symmetric by construction.</b> Storing
/// <c>(low, high)</c> would allow a table entry whose midpoint is not 0.5, and a per-dimension
/// asymmetry is a thing nobody has measured. The bounds are derived and published by
/// <see cref="BandFor"/> so a reader never has to re-derive them.
/// </para>
/// </remarks>
public sealed class CascadeTrust
{
    /// <summary>
    /// How far from 0.5 a value may sit before it stops being indecisive, for every dimension without
    /// its own entry.
    /// </summary>
    /// <remarks>
    /// <b>STATED, not measured.</b> 0.15 puts the band at <c>[0.35, 0.65]</c>. The honest ceiling on
    /// this constant is that the local model's distribution in the middle of the range has NOT been
    /// measured: the survey produced one answer at 0.9995, which is one point near an end and says
    /// nothing about the middle. The measurement that would revise it is the escalation rate and the
    /// per-dimension agreement it produces: a band that escalates most of a batch is too wide, and a
    /// band that lets mid-range answers through unchanged is too narrow.
    /// </remarks>
    public const double DefaultIndecisiveHalfWidth = 0.15;

    private static readonly Dictionary<string, double> NoOverrides = new(StringComparer.Ordinal);

    private static readonly HashSet<string> NoAlwaysEscalate = new(StringComparer.Ordinal);

    /// <summary>The table as shipped: one band, and nothing distrusted by name.</summary>
    public static CascadeTrust Default { get; } = new();

    /// <summary>The half-width used for a dimension with no entry of its own.</summary>
    public double IndecisiveHalfWidth { get; init; } = DefaultIndecisiveHalfWidth;

    /// <summary>Dimensions whose local answer is never taken, with the population that justified each.</summary>
    public IReadOnlyCollection<string> AlwaysEscalateIds { get; init; } = NoAlwaysEscalate;

    /// <summary>Per-dimension overrides of <see cref="IndecisiveHalfWidth"/>.</summary>
    public IReadOnlyDictionary<string, double> PerDimensionHalfWidth { get; init; } = NoOverrides;

    /// <summary>True when the local answer for a dimension is never taken, whatever it says.</summary>
    public bool AlwaysEscalates(string dimensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimensionId);
        return AlwaysEscalateIds.Contains(dimensionId);
    }

    /// <summary>The inclusive band outside which a value counts as a decision for this dimension.</summary>
    public (double Low, double High) BandFor(string dimensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimensionId);

        var halfWidth = PerDimensionHalfWidth.TryGetValue(dimensionId, out var overrideValue)
            ? overrideValue
            : IndecisiveHalfWidth;

        return (0.5 - halfWidth, 0.5 + halfWidth);
    }

    /// <summary>True when the value is inside the band, so the model is not reporting a decision.</summary>
    public bool IsIndecisive(string dimensionId, double value)
    {
        var (low, high) = BandFor(dimensionId);
        return value >= low && value <= high;
    }

    /// <summary>Refuses a table that could not be the one anybody meant.</summary>
    internal void Validate()
    {
        if (IndecisiveHalfWidth < 0 || IndecisiveHalfWidth > 0.5)
        {
            // Above 0.5 the band swallows the whole range and every answer escalates; below 0 it is
            // empty and the condition can never fire. Both are configuration mistakes that would look
            // like a working rule, so they are refused rather than clamped.
            throw new InvalidOperationException(
                $"CascadeTrust.IndecisiveHalfWidth must be within [0, 0.5] so the band lies inside the "
                + $"unit interval, and it is {IndecisiveHalfWidth}.");
        }

        foreach (var (dimensionId, halfWidth) in PerDimensionHalfWidth)
        {
            if (halfWidth < 0 || halfWidth > 0.5)
            {
                throw new InvalidOperationException(
                    $"CascadeTrust.PerDimensionHalfWidth['{dimensionId}'] must be within [0, 0.5], and "
                    + $"it is {halfWidth}.");
            }
        }

        foreach (var dimensionId in AlwaysEscalateIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dimensionId);
        }
    }
}
