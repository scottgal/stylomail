namespace StyloMail.Profiles.Kpis;

/// <summary>Whether a higher value is good, bad, or neither.</summary>
/// <remarks>
/// <b><see cref="Neutral"/> is the honest answer for a descriptive measure.</b> Pressure
/// prevalence, sensitive-request prevalence and handling rate describe what was observed; they are
/// not scores, and attaching "higher is worse" to them would be the first step toward the
/// red/amber/green judgement the spec forbids.
/// </remarks>
public enum KpiDirection
{
    Neutral = 0,
    HigherIsBetter = 1,
    LowerIsBetter = 2,
}

/// <summary>What a KPI's value is measured in.</summary>
public enum KpiUnit
{
    /// <summary>A 0..1 share.</summary>
    Ratio = 0,

    /// <summary>A dimensionless multiple, which may exceed 1 (recent volume against prior volume).</summary>
    Multiple = 1,

    /// <summary>A duration in milliseconds.</summary>
    Milliseconds = 2,

    /// <summary>A duration in hours.</summary>
    Hours = 3,

    /// <summary>An unbounded count.</summary>
    Count = 4,
}

/// <summary>Whether a KPI produced a value, and if not why.</summary>
public enum KpiState
{
    Produced = 0,

    /// <summary>Some evidence exists but below the definition's minimum sample. Not a number.</summary>
    NotEnoughHistory = 1,

    /// <summary>Nothing could be measured. Always carries a named reason.</summary>
    NotProduced = 2,
}

/// <summary>Whether a prior-window comparison produced a movement, and if not why.</summary>
/// <remarks>
/// <b>This is not <see cref="KpiDefinition.Direction"/>, which is a preference, not an
/// observation.</b> A definition saying "higher is better" says nothing about whether the number
/// moved. The trend is a second measurement over an equal-length window immediately before this
/// one, and it is absent unless both windows produced a value under the same definition version.
/// </remarks>
public enum KpiTrendState
{
    Produced = 0,

    /// <summary>The prior window was empty or below the definition's minimum sample.</summary>
    NotEnoughHistory = 1,

    /// <summary>One of the windows did not produce a value, or the definition version moved.</summary>
    NotProduced = 2,

    /// <summary>The KPI cannot be compared across windows for this scope (reserved; none today).</summary>
    NotApplicable = 3,
}

/// <summary>Which way an observed value moved. Never a judgement, and never inferred from absence.</summary>
public enum KpiTrendMovement
{
    Up = 0,
    Down = 1,

    /// <summary>Exactly equal. Deliberately not a tolerance band: a threshold would be invented.</summary>
    Flat = 2,
}

/// <summary>A prior-window comparison served beside a KPI value.</summary>
/// <remarks>
/// The prior window is the same length and immediately precedes the current one. A movement is
/// only reported when both windows are Produced, the definition version is unchanged, and neither
/// value is absent. <see cref="PriorTruncated"/> says the prior window hit the row cap, so a reader
/// can see that its value rests on the most recent messages of that window.
/// </remarks>
public sealed record KpiTrend
{
    public required KpiTrendState State { get; init; }

    public double? PriorValue { get; init; }

    /// <summary>Current minus prior. Null unless <see cref="State"/> is Produced.</summary>
    public double? Delta { get; init; }

    /// <summary>
    /// Delta relative to the prior value's magnitude. Null when the prior value is zero, where a
    /// relative change has no denominator; the absolute <see cref="Delta"/> is still reported.
    /// </summary>
    public double? RelativeDelta { get; init; }

    public KpiTrendMovement? Movement { get; init; }

    public required DateTimeOffset PriorWindowStart { get; init; }

    public required DateTimeOffset PriorWindowEnd { get; init; }

    public required bool PriorTruncated { get; init; }

    public string? Reason { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// One versioned KPI definition, served beside every value so the console and the manual cannot
/// drift from the computation.
/// </summary>
/// <remarks>
/// <para>
/// <b>A definition is code, not configuration.</b> These are claims about what a number means, and
/// a tenant that could redefine one in a database would be able to change the meaning of a chart
/// without changing the computation. The version moves when the computation or the wording moves;
/// a stored or cached value names the version it was produced under.
/// </para>
/// <para>
/// <b><see cref="AvailabilityReasons"/> is part of the definition, not an afterthought.</b> It is
/// the closed set of reasons this KPI may return instead of a value. A consumer that receives a
/// reason outside its definition has found a contract breach rather than a new state.
/// </para>
/// </remarks>
public sealed record KpiDefinition
{
    public required string Id { get; init; }

    /// <summary>The console's grouping, e.g. <c>Responsiveness</c>. Several definitions may share one.</summary>
    public required string Group { get; init; }

    public required string Label { get; init; }

    /// <summary>The question an operator is asking, in plain words.</summary>
    public required string Question { get; init; }

    /// <summary>What the numerator counts. Prose, exact enough to audit.</summary>
    public required string Numerator { get; init; }

    /// <summary>What the denominator counts. Prose, exact enough to audit.</summary>
    public required string Denominator { get; init; }

    /// <summary>Which window, and any asymmetry such as looking past the window for a reply.</summary>
    public required string Window { get; init; }

    /// <summary>Below this many analysed observations the KPI is NotEnoughHistory, never a number.</summary>
    public required int MinimumSample { get; init; }

    public required KpiDirection Direction { get; init; }

    public required KpiUnit Unit { get; init; }

    /// <summary>The closed set of reasons this definition may return instead of a value.</summary>
    public required IReadOnlyList<string> AvailabilityReasons { get; init; }

    public required string Version { get; init; }
}

/// <summary>How much of the population a value was computed from.</summary>
public sealed record KpiCoverage
{
    /// <summary>Observations that contributed to the numerator and denominator.</summary>
    public required int Analysed { get; init; }

    /// <summary>Attributable messages in the profile window, whether or not they carried the signal.</summary>
    public required int Total { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>The row cap was reached, so the value rests on the most recent messages only.</summary>
    public required bool Truncated { get; init; }

    /// <summary>Why coverage is thin, when a KPI is not produced or is below its minimum.</summary>
    public string? Reason { get; init; }
}

/// <summary>A secondary observation served with a KPI, such as the median reply gap.</summary>
/// <remarks>
/// Secondary measurements carry their own availability so a missing median cannot be read as a
/// zero-second reply. <see cref="Value"/> null plus <see cref="Reason"/> is the honest absence.
/// </remarks>
public sealed record KpiMeasurement
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required KpiUnit Unit { get; init; }

    public double? Value { get; init; }

    public string? Reason { get; init; }
}

/// <summary>One KPI's definition, value and coverage, together.</summary>
/// <remarks>
/// <b>Definition, coverage and value are one object.</b> Serving a number without the definition
/// that produced it, or without how much of the population it saw, is how a KPI becomes an
/// unfalsifiable score.
/// </remarks>
public sealed record KpiValue
{
    public required KpiDefinition Definition { get; init; }

    public required KpiState State { get; init; }

    public double? Value { get; init; }

    public int? Numerator { get; init; }

    public int? Denominator { get; init; }

    public required KpiCoverage Coverage { get; init; }

    /// <summary>A reason from <see cref="KpiDefinition.AvailabilityReasons"/> when not produced.</summary>
    public string? Reason { get; init; }

    /// <summary>A plain sentence explaining the state. Never replaces <see cref="Reason"/>.</summary>
    public string? Detail { get; init; }

    public IReadOnlyList<KpiMeasurement> Measurements { get; init; } = [];

    /// <summary>
    /// The prior-window comparison, when a profile read supplied one. Null on the bare catalogue,
    /// where there is no window to compare.
    /// </summary>
    public KpiTrend? Trend { get; init; }
}

/// <summary>The stable names a KPI may use in <see cref="KpiValue.Reason"/>.</summary>
public static class KpiReasons
{
    public const string NoAttributableMessages = "no_attributable_messages";
    public const string NotEnoughHistory = "not_enough_history";
    public const string SignalNotProduced = "signal_not_produced";

    /// <summary>The scope has no envelope addresses to match, e.g. a chat conversation.</summary>
    public const string NoAddressReciprocityInScope = "no_address_reciprocity_in_scope";

    /// <summary>There is no tenant profile key, so addresses cannot be matched pseudonymously.</summary>
    public const string NoProfileKey = "no_profile_key";

    /// <summary>The recipient side is not attributable (domain scope, v1).</summary>
    public const string NoOutboundRecipientIdentity = "no_outbound_recipient_identity";

    /// <summary>True In-Reply-To linkage is not recorded; the definition is an address proxy.</summary>
    public const string NoReplyLinkage = "no_reply_linkage";

    /// <summary>No thread identity exists for these messages (ordinary email).</summary>
    public const string NoThreadIdentity = "no_thread_identity";

    /// <summary>The responsiveness proxy saw replies but none matched, so no median exists.</summary>
    public const string NoMatchedPairs = "no_matched_pairs";

    /// <summary>No stored human label exists for this scope; the UI shows "Entity &lt;opaque&gt;".</summary>
    public const string NoStoredLabel = "no_stored_label";

    /// <summary>The prior window had no attributable messages at all.</summary>
    public const string PriorWindowEmpty = "prior_window_empty";

    /// <summary>The prior window had messages but below the definition's minimum sample.</summary>
    public const string PriorWindowNotEnoughHistory = "prior_window_not_enough_history";

    /// <summary>The prior window produced no value (e.g. the signal was not produced there).</summary>
    public const string PriorWindowNotProduced = "prior_window_not_produced";

    /// <summary>The current window produced no value, so there is nothing to compare.</summary>
    public const string CurrentWindowNotProduced = "current_window_not_produced";

    /// <summary>The current window is below the definition's minimum sample.</summary>
    public const string CurrentWindowNotEnoughHistory = "current_window_not_enough_history";

    /// <summary>The definition version changed between the two windows; the values are not comparable.</summary>
    public const string DefinitionVersionChanged = "definition_version_changed";

    public const string NoIdentityCaptured = "no_identity_captured";
}
