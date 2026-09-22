namespace StyloMail.Core;

/// <summary>
/// A bounded encoding of how a sender has been behaving, for the semantic classifier to judge a
/// message against.
/// </summary>
/// <remarks>
/// <para>
/// A message judged in isolation loses most of what makes it suspicious. A credential request from an
/// account with six months of transactional receipts is not the same event as the same words from an
/// account created yesterday that is fanning out to strangers. The text is identical; the
/// relationship is not.
/// </para>
/// <para>
/// <b>This carries observations and their support. Never verdicts.</b> Counts, rates, windows and
/// baselines only. There is deliberately no severity, no score and no suspicious flag: deterministic
/// policy authorises actions, and a profile arriving pre-judged would make the classifier's answers a
/// restatement of our own flags rather than an independent judgement about the message.
/// </para>
/// <para>
/// <b>Bounded by construction.</b> A fixed field set with capped cardinality rather than a serialised
/// profile, because the classifier's state budget is shared with the questions and an unbounded trend
/// list spends it on the messages least worth spending it on.
/// </para>
/// </remarks>
public sealed record BehaviouralProfile
{
    public required MailDirection Direction { get; init; }

    /// <summary>How long we have known this sender, in days. Null when the profile is cold.</summary>
    public int? FirstSeenDaysAgo { get; init; }

    /// <summary>Messages observed from this sender, including rejected traffic.</summary>
    public int? MessagesObserved { get; init; }

    /// <summary>
    /// Approved baseline samples only. Distinct from <see cref="MessagesObserved"/> and never
    /// conflated with it: a sent or unreported message is not automatically trusted history.
    /// </summary>
    public int? TrustedSamples { get; init; }

    /// <summary>Behavioural regime. A change suppresses derivative evidence and is reported, not hidden.</summary>
    public string? Regime { get; init; }

    /// <summary>Distinct recipients this sender has addressed in the last hour.</summary>
    public int? DistinctRecipientsLastHour { get; init; }

    /// <summary>Distinct recipients this sender has addressed in the last 30 days.</summary>
    public int? DistinctRecipientsLast30Days { get; init; }

    /// <summary>
    /// True when <see cref="DistinctRecipientsLast30Days"/> is a floor rather than a measurement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recipient history is bounded, so once it saturates the count can only ever under-state.
    /// It is still emitted rather than nulled, and the asymmetry is the reason: <b>a count's error
    /// runs downwards, and under-stating cannot manufacture alarm, whereas novelty's error runs
    /// upwards.</b> The direction of the error decides the encoding, so a truncated count is a floor
    /// that says it is a floor, and a truncated novelty is unknown.
    /// </para>
    /// <para>
    /// Added 2026-09-22. Until it existed the floor was emitted as though it were a measurement.
    /// </para>
    /// </remarks>
    public bool RecipientDistinctnessIsFloor { get; init; }

    /// <summary>Recipients on this message that this sender has never addressed before.</summary>
    public int? RecipientsNovelToSender { get; init; }

    public int? MessagesLastHour { get; init; }

    public int? MessagesLast24Hours { get; init; }

    /// <summary>Established rate, for comparison against the recent windows above.</summary>
    public double? BaselineMessagesPerHour { get; init; }

    public int? FanoutLastHour { get; init; }

    public double? BaselineFanoutPerHour { get; init; }

    /// <summary>
    /// Reason-shaped description of what is moving, for example "recipient fan-out rising while
    /// payment-redirection evidence also rises".
    /// </summary>
    /// <remarks>
    /// A narrative rather than a scalar: an unexplained acceleration number tells a classifier
    /// nothing it can reason with, and tells a human reading the ledger less.
    /// </remarks>
    public string? TrendNarrative { get; init; }

    /// <summary>
    /// The dimensions currently moving, capped. Each is an observation with a direction and
    /// magnitude, never a judgement about whether the movement matters.
    /// </summary>
    public IReadOnlyList<DimensionMovement>? Movements { get; init; }

    /// <summary>How many dimensions had enough support to be compared. Support, not conclusion.</summary>
    public int? DimensionsWithSupport { get; init; }

    /// <summary>
    /// Whether a profile existed at all.
    /// </summary>
    /// <remarks>
    /// <b>False is a distinct state, not a normal-looking profile.</b> "We do not know this sender"
    /// is a different statement from "this sender looks ordinary", and collapsing the two is the same
    /// error as treating an unavailable signal as a zero score.
    /// </remarks>
    public required bool ProfileAvailable { get; init; }

    /// <summary>True when the profile exists but has too little support to compare against.</summary>
    public required bool ColdStart { get; init; }

    /// <summary>Maximum movements carried, so the trend section cannot grow without bound.</summary>
    public const int MaxMovements = 6;
}

/// <summary>One dimension's recent movement. An observation, not a judgement.</summary>
public sealed record DimensionMovement
{
    public required string DimensionId { get; init; }

    /// <summary>Which way it moved. Reported, never scored.</summary>
    public required string Direction { get; init; }

    public required double Magnitude { get; init; }
}
