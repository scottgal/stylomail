using StyloMail.Core;

namespace StyloMail.Host.Decisions;

/// <summary>
/// A bounded, tenant-scoped request for ledger entries.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tenant is not optional, unlike <see cref="IDecisionLedger.FindAsync"/>'s argument being
/// supplied per call.</b> A read by id is scoped or it is nothing; a <em>listing</em> without a tenant
/// would be an enumeration across every tenant on the host, which is a far worse thing to get wrong
/// than an oracle over ids nobody can guess.
/// </para>
/// <para>
/// A query for a tenant with no decisions returns an empty page. There is no "forbidden" answer to
/// distinguish from "absent", deliberately: a caller cannot learn whether a tenant exists by asking.
/// </para>
/// </remarks>
public sealed record DecisionListingQuery
{
    public required string TenantId { get; init; }

    /// <summary>
    /// Restrict to one action, or null for every action.
    /// </summary>
    /// <remarks>
    /// Served by an equality on the ledger's own indexed `action` column, so it is a filter the query
    /// genuinely honours rather than one applied to a page after it was cut. That distinction is the
    /// whole reason this is a closed <see cref="MailAction"/> rather than a free string: anything the
    /// ledger cannot answer: "decisions whose message was later delivered", say, which needs queue
    /// state it does not have: is refused rather than approximated.
    /// </remarks>
    public MailAction? Action { get; init; }

    /// <summary>
    /// Restrict to one message, or null for every message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The link from a listed message to its decision.</b> The ledger stores the message id
    /// alongside the assessment id, and the message listings now expose it, so a reviewer can go from
    /// a quarantined message to the explanation for it without either component changing what it
    /// stores. A queue row carrying an assessment id would have meant a schema change on both sides
    /// for the same answer.
    /// </para>
    /// <para>
    /// It returns a <em>list</em> rather than one decision because a message can be assessed more than
    /// once, a re-assessment after a policy change is a legitimate thing to have on the record, and
    /// returning only the newest would hide that. A caller wanting one takes the first.
    /// </para>
    /// </remarks>
    public string? InternalMessageId { get; init; }

    /// <summary>
    /// Maximum entries to return. Clamped to <see cref="DecisionListingLimits.MaxPageSize"/> rather
    /// than rejected, so an over-large request degrades into paging instead of an error.
    /// </summary>
    public int Limit { get; init; } = DecisionListingLimits.DefaultPageSize;

    /// <summary>
    /// Opaque cursor from a previous page's <see cref="DecisionListingPage.NextCursor"/>. Echo it back
    /// unchanged. It is not a capability and carries no tenant: the query's own
    /// <see cref="TenantId"/> always governs what is returned, so a tampered cursor can only shift
    /// which page you see, never whose decisions.
    /// </summary>
    public string? After { get; init; }
}

/// <summary>A bounded read of assessed Slack events for one workspace channel.</summary>
public sealed record ConversationHistoryQuery
{
    public required string TenantId { get; init; }

    public required string WorkspaceId { get; init; }

    public required string ChannelId { get; init; }

    /// <summary>When absent, include all threads in the channel.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Inclusive UTC lower bound on assessment time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive UTC upper bound on assessment time.</summary>
    public DateTimeOffset? To { get; init; }

    public int Limit { get; init; } = DecisionListingLimits.DefaultPageSize;

    /// <summary>Opaque cursor from the previous page, valid only for the same tenant and filters.</summary>
    public string? After { get; init; }
}

/// <summary>Bounds for ledger listings.</summary>
public static class DecisionListingLimits
{
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Hard ceiling on one page.
    /// </summary>
    /// <remarks>
    /// Lower than the queue's 200 because a ledger entry is a whole decision rather than a queue
    /// row, each one carries the evidence, reasons, dimensions and coverage for a message, so the
    /// same page size is a much larger response here.
    /// </remarks>
    public const int MaxPageSize = 100;
}

/// <summary>One page of ledger entries, newest first.</summary>
public sealed record DecisionListingPage
{
    public required IReadOnlyList<MailAssessment> Items { get; init; }

    /// <summary>
    /// Pass as <see cref="DecisionListingQuery.After"/> for the next page; null when this is the last.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>
    /// How many rows the page held that this build could not read back, and which are therefore not in
    /// <see cref="Items"/>.
    /// </summary>
    /// <remarks>
    /// <b>A page that drops rows silently is a page that reports itself complete when it is not.</b>
    /// The rows are skipped rather than allowed to fail the page (one unreadable payload should not
    /// take a whole listing down), but a skip that is only a log line is invisible to the caller that
    /// is reading the response. Counted here so the number travels with the page, and defaults to 0
    /// because "nothing was skipped" is the true answer for every row that read back, including all
    /// rows written since the arithmetic was recorded.
    /// </remarks>
    public int SkippedCount { get; init; }

    public bool HasMore => NextCursor is not null;
}
