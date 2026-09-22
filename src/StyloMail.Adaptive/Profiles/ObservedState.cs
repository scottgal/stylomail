using StyloMail.Core;

namespace StyloMail.Adaptive.Profiles;

/// <summary>
/// One attempt seen for a profile, whatever became of it.
/// </summary>
/// <remarks>
/// Observation is unconditional. Rejected traffic, held traffic and traffic that never
/// completed are all observations: a sender whose mail is always blocked is precisely the
/// sender whose rate must be bounded.
/// </remarks>
public sealed record ProfileObservation
{
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>Recipients in this attempt. Quotas count recipients, not messages.</summary>
    public required int RecipientCount { get; init; }

    /// <summary>True when this attempt was declined rather than sent.</summary>
    public required bool WasRejected { get; init; }

    /// <summary>Dimension values carried by this attempt, where any were produced.</summary>
    public DimensionVector? Dimensions { get; init; }

    /// <summary>
    /// Pseudonymised keys of the recipients this message was addressed to.
    /// </summary>
    /// <remarks>
    /// Absent means the caller did not identify them, which leaves novelty unanswerable for this
    /// message rather than making it zero. These are a different measurement from
    /// <see cref="RecipientCount"/>: the count says how many addresses were written, these say how
    /// many separate people, and one message to fifty colleagues is not fan-out.
    /// </remarks>
    public IReadOnlyList<string>? RecipientKeys { get; init; }
}

/// <summary>
/// Everything the profile has seen.
/// </summary>
/// <remarks>
/// This is one of the two stores and it is <b>not</b> a learning store. Its job is abuse
/// detection and throughput bounding, so it counts attempts rather than endorsing them, and
/// nothing here may be read as evidence that any particular behaviour is legitimate.
/// </remarks>
public sealed record ObservedState
{
    public static ObservedState Empty { get; } = new()
    {
        Attempts = 0,
        RejectedAttempts = 0,
        Recipients = 0,
    };

    public required long Attempts { get; init; }

    public required long RejectedAttempts { get; init; }

    public required long Recipients { get; init; }

    public DateTimeOffset? FirstObservedAt { get; init; }

    public DateTimeOffset? LastObservedAt { get; init; }

    public ObservedState With(ProfileObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return this with
        {
            Attempts = Attempts + 1,
            RejectedAttempts = RejectedAttempts + (observation.WasRejected ? 1 : 0),
            Recipients = Recipients + observation.RecipientCount,
            FirstObservedAt = FirstObservedAt is null || observation.ObservedAt < FirstObservedAt
                ? observation.ObservedAt
                : FirstObservedAt,
            LastObservedAt = LastObservedAt is null || observation.ObservedAt > LastObservedAt
                ? observation.ObservedAt
                : LastObservedAt,
        };
    }
}
