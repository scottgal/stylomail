using StyloMail.Profiles.Properties;

namespace StyloMail.Profiles.Kpis;

/// <summary>What the profile query could see, and what it could not.</summary>
/// <remarks>
/// <b><see cref="TenantMessages"/> is the denominator the attribution gap is read against.</b>
/// <see cref="Messages"/> counts messages attributable to this scope through the queue envelope;
/// a tenant message with no envelope (assessment-only traffic) cannot be attributed to a person or
/// domain and is not silently dropped. The difference between the two is the honest price of
/// building an entity profile out of a ledger that does not carry the envelope.
/// </remarks>
public sealed record ProfileCoverage
{
    /// <summary>Messages attributable to this scope in the window.</summary>
    public required int Messages { get; init; }

    /// <summary>All tenant messages in the window, attributable or not.</summary>
    public required int TenantMessages { get; init; }

    /// <summary>The row cap was reached; the profile covers the most recent messages only.</summary>
    public required bool Truncated { get; init; }

    /// <summary>Rows that could not be read as an assessment. Never silently treated as clean.</summary>
    public required int Unparsable { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    public DateTimeOffset? FirstMessageAt { get; init; }

    public DateTimeOffset? LastMessageAt { get; init; }

    /// <summary>Why the coverage is thin, when it is.</summary>
    public string? Reason { get; init; }
}

/// <summary>One identity observed around the scope, with no content and no address.</summary>
/// <remarks>
/// Only domains are reported. Display names and signatures are not captured in the ledger, and
/// deriving them from message content is out of scope; the profile's not-measured list says so.
/// </remarks>
public sealed record ProfileIdentity
{
    /// <summary><c>correspondent_domain</c> or <c>domain</c>.</summary>
    public required string Kind { get; init; }

    public required string Value { get; init; }

    public required int Messages { get; init; }

    public required DateTimeOffset FirstSeenAt { get; init; }

    public required DateTimeOffset LastSeenAt { get; init; }
}

/// <summary>The relationship summary: what was seen, when, and how much of it.</summary>
/// <remarks>
/// No score, no colour, no verdict. Counts and instants only, with a named reason wherever a
/// count could not be produced.
/// </remarks>
public sealed record ProfileRelationship
{
    public DateTimeOffset? FirstSeenAt { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>Inbound messages, or null when the channel carries no envelope direction.</summary>
    public int? InboundMessages { get; init; }

    public int? OutboundMessages { get; init; }

    public string? DirectionReason { get; init; }

    /// <summary>
    /// Distinct counterparty addresses seen, counted through their tenant-keyed pseudonym.
    /// Null when no profile key exists to compute it.
    /// </summary>
    public int? DistinctCounterparties { get; init; }

    public string? DistinctCounterpartiesReason { get; init; }

    /// <summary>Distinct thread ids among the messages, or null when none carry one.</summary>
    public int? Conversations { get; init; }

    public string? ConversationsReason { get; init; }
}

/// <summary>Something a reader might expect a profile to show, and why it does not.</summary>
public sealed record ProfileNotMeasured
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string Reason { get; init; }
}

/// <summary>One profile read: scope, window, coverage, identities, relationship and KPIs.</summary>
public sealed record ProfileSnapshot
{
    public required PropertyScope Scope { get; init; }

    /// <summary>
    /// A human label that was already stored, or null. Never derived from content or a hash.
    /// </summary>
    public string? DisplayLabel { get; init; }

    /// <summary>False when no stored label exists; the UI shows "Entity &lt;opaque&gt;".</summary>
    public required bool DisplayLabelAvailable { get; init; }

    /// <summary>
    /// Where the label came from: <c>sender_profile</c>, <c>company</c> or <c>domain</c>.
    /// Null when unavailable.
    /// </summary>
    public string? DisplayLabelSource { get; init; }

    /// <summary>A named reason when no label is available (<c>no_stored_label</c>).</summary>
    public string? DisplayLabelReason { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    public required ProfileCoverage Coverage { get; init; }

    public IReadOnlyList<ProfileIdentity> Identities { get; init; } = [];

    public required ProfileRelationship Relationship { get; init; }

    public IReadOnlyList<KpiValue> Kpis { get; init; } = [];

    /// <summary>
    /// The explicit absences. A profile that omitted these would let a reader assume the tiles
    /// cover tone, friendliness, intent or drift.
    /// </summary>
    public IReadOnlyList<ProfileNotMeasured> NotMeasured { get; init; } = [];
}
