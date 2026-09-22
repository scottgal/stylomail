namespace StyloMail.Adaptive.Scoring;

/// <summary>
/// Running count, mean and sum of squared deviations for one dimension.
/// </summary>
/// <remarks>
/// Welford's method, kept in this form because a baseline is a bounded, incrementally-updated
/// summary: storing every approved sample to recompute a variance later would make profile
/// memory grow with account age, which is exactly the unbounded growth this design avoids.
/// </remarks>
public sealed record RunningMoments
{
    public static RunningMoments Empty { get; } = new() { Count = 0, Mean = 0.0, M2 = 0.0 };

    public required int Count { get; init; }

    public required double Mean { get; init; }

    /// <summary>Sum of squared deviations from the mean. Unnormalised; see <see cref="Variance"/>.</summary>
    public required double M2 { get; init; }

    /// <summary>Sample variance. Zero below two observations, where spread is unknowable rather than absent.</summary>
    public double Variance => Count < 2 ? 0.0 : M2 / (Count - 1);

    /// <summary>
    /// Folds in one value.
    /// </summary>
    /// <param name="maxMeanShift">
    /// Largest distance this single value may move the mean. The dimension's spread is the
    /// natural unit: a sample may nudge the belief by a fraction of what is already known
    /// about the dimension's variability, never by an unbounded amount.
    /// </param>
    /// <remarks>
    /// The cap is applied in mean space rather than to the incoming value, because dividing by
    /// the sample count would otherwise shrink the effective limit as support grows — a
    /// baseline that becomes harder to move the more evidence it has is a baseline that can
    /// never follow a genuine, sustained change.
    /// </remarks>
    public RunningMoments Add(double value, double maxMeanShift = double.PositiveInfinity)
    {
        var count = Count + 1;
        var delta = value - Mean;
        var mean = Mean + Math.Clamp(delta / count, -maxMeanShift, maxMeanShift);

        return new RunningMoments
        {
            Count = count,
            Mean = mean,
            M2 = M2 + (delta * (value - mean)),
        };
    }

    public static RunningMoments From(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var moments = Empty;
        foreach (var value in values)
        {
            moments = moments.Add(value);
        }

        return moments;
    }
}
