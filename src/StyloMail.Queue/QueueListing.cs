namespace StyloMail.Queue;

/// <summary>Which items a listing returns.</summary>
public enum QueueListingFilter
{
    /// <summary>
    /// Items with at least one recipient awaiting a human decision — held or quarantined.
    /// </summary>
    AwaitingDecision = 0,

    /// <summary>Items with at least one recipient currently held.</summary>
    Held = 1,

    /// <summary>Items with at least one recipient currently quarantined.</summary>
    Quarantined = 2,
}

/// <summary>
/// A bounded, tenant-scoped request for queue items.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tenant is not optional here, unlike <see cref="QueueStore.GetItemAsync"/>.</b> A read by
/// queue id is scoped or it is nothing; a <em>listing</em> without a tenant would be an enumeration
/// across every tenant, which is a far worse thing to get wrong than a single-item read oracle
/// over ids nobody can guess.
/// </para>
/// <para>
/// A query for a tenant that owns nothing returns an empty page. There is no "forbidden" answer to
/// distinguish from "absent", deliberately: a caller cannot learn whether a tenant exists by asking.
/// </para>
/// </remarks>
public sealed record QueueListingQuery
{
    public required string TenantId { get; init; }

    public QueueListingFilter Filter { get; init; } = QueueListingFilter.AwaitingDecision;

    /// <summary>
    /// Maximum items to return. Clamped to <see cref="QueueListingLimits.MaxPageSize"/> rather than
    /// rejected, so an over-large request degrades into paging instead of an error.
    /// </summary>
    public int Limit { get; init; } = QueueListingLimits.DefaultPageSize;

    /// <summary>
    /// Opaque cursor from a previous page's <see cref="QueueListingPage.NextCursor"/>. Echo it back
    /// unchanged. It is not a capability and carries no tenant: the query's own
    /// <see cref="TenantId"/> always governs what is returned, so a tampered cursor can only shift
    /// which page you see, never whose items.
    /// </summary>
    public string? After { get; init; }
}

/// <summary>Bounds for queue listings.</summary>
public static class QueueListingLimits
{
    public const int DefaultPageSize = 50;

    /// <summary>Hard ceiling on one page, so no caller can ask for the whole queue.</summary>
    public const int MaxPageSize = 200;
}

/// <summary>One page of queue items.</summary>
public sealed record QueueListingPage
{
    /// <summary>
    /// The items, newest first. Each carries its per-recipient states, so a caller can see exactly
    /// which recipients a decision applies to without a second lookup.
    /// </summary>
    public required IReadOnlyList<QueueItem> Items { get; init; }

    /// <summary>
    /// Pass as <see cref="QueueListingQuery.After"/> for the next page; null when this is the last.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>Whether more items exist beyond this page.</summary>
    public bool HasMore => NextCursor is not null;
}
