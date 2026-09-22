namespace StyloMail.Adaptive.Learning;

/// <summary>Tuning for a time-aware exponential moving average.</summary>
public sealed record EwmaOptions
{
    /// <summary>Time constant: the elapsed time over which the weight reaches ~63%.</summary>
    public required TimeSpan Tau { get; init; }

    /// <summary>
    /// Largest weight a single observation may have, regardless of how long it has been.
    /// </summary>
    /// <remarks>
    /// Without this, an average that has not been updated for a week is replaced wholesale by
    /// the next message, and a single message is exactly what an attacker controls.
    /// </remarks>
    public double MaxEventContribution { get; init; } = 0.25;
}

/// <summary>
/// A time-aware exponential moving average.
/// </summary>
public static class Ewma
{
    /// <summary>
    /// Weight for an observation that arrives <paramref name="elapsed"/> after the last one.
    /// </summary>
    /// <remarks>
    /// Elapsed time is the weight, so a burst of messages in one second does not advance the
    /// average the way the same messages spread over an hour would: rate is a property of
    /// time, not of how many times the code ran.
    /// </remarks>
    public static double Alpha(TimeSpan elapsed, TimeSpan tau)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tau, TimeSpan.Zero);

        if (elapsed <= TimeSpan.Zero)
        {
            return 0.0;
        }

        return 1.0 - Math.Exp(-elapsed.TotalSeconds / tau.TotalSeconds);
    }
}

/// <summary>One EWMA's value and the time it was last advanced to.</summary>
public sealed record EwmaState
{
    public static EwmaState Empty { get; } = new() { Value = null, LastUpdatedAt = null, Updates = 0 };

    public double? Value { get; init; }

    public DateTimeOffset? LastUpdatedAt { get; init; }

    public int Updates { get; init; }

    public bool HasValue => Value is not null;

    public EwmaState Observe(double value, DateTimeOffset at, EwmaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (Value is null || LastUpdatedAt is null)
        {
            return new EwmaState { Value = value, LastUpdatedAt = at, Updates = 1 };
        }

        var alpha = Math.Min(
            Ewma.Alpha(at - LastUpdatedAt.Value, options.Tau),
            options.MaxEventContribution);

        return new EwmaState
        {
            Value = Value.Value + (alpha * (value - Value.Value)),
            LastUpdatedAt = at,
            Updates = Updates + 1,
        };
    }
}
