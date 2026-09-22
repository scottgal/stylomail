namespace StyloMail.Desktop.Api.Contracts;

/// <summary>
/// Which disposition a message listing asks for.
/// </summary>
/// <remarks>
/// <b>A closed type rather than a string, and the reason is a route decision
/// rather than a style preference.</b> <c>GET /v1/messages</c> enumerates only
/// the dispositions that are waiting for attention: messages in normal delivery
/// have no filter and are not listed. The Host refuses an unsupported state by
/// name, deliberately, because filtering a page after it had been cut would
/// return short pages with a wrong <c>hasMore</c>, so a console paging through
/// it would watch mail disappear.
///
/// <para>
/// A client that passed an arbitrary string could ask for one of those and get
/// a 400 at runtime. A client that can only name these three cannot express the
/// request at all, which puts the mistake at the call site instead of against a
/// live Host.
/// </para>
/// </remarks>
public enum MessageListState
{
    /// <summary>Durably accepted and not yet decided on. The route's own default.</summary>
    AwaitingDecision,

    /// <summary>Held for a bounded re-evaluation window. An observation, not a soft reject.</summary>
    Held,

    /// <summary>Retained for authenticated review and not delivered.</summary>
    Quarantined,
}

/// <summary>
/// <c>GET /v1/senders</c>: the principals this tenant can send as, with their controls.
/// </summary>
/// <remarks>
/// Deliberately not the configured principal record. The Host projects each
/// field by name so that adding one to its configuration cannot publish the API
/// key that authenticates that principal by default. The console cannot leak
/// what it is not sent, and this type mirrors exactly what it is sent.
/// </remarks>
public sealed record SenderListingResponse
{
    /// <summary>The tenant the listing was taken for. Read-only confirmation of scope.</summary>
    public required string TenantId { get; init; }

    public required IReadOnlyList<SenderResponse> Senders { get; init; }
}

/// <summary>One sending principal and its control state.</summary>
public sealed record SenderResponse
{
    public required string PrincipalId { get; init; }

    public required SenderControlResponse Control { get; init; }

    /// <summary>
    /// The operator's name for this sender, or null when nobody has described it.
    /// </summary>
    /// <remarks>
    /// On the row so the sidebar groups without a settings call per sender,
    /// which on a tenant with a few hundred would be a request storm.
    /// </remarks>
    public string? Label { get; init; }

    /// <summary>
    /// Where this principal's authority came from: <c>store</c> or <c>environment</c>.
    /// </summary>
    /// <remarks>
    /// <b>Required rather than nullable, and that is the point.</b> The Host has
    /// two sources of identity and the difference is not cosmetic: a
    /// <c>store</c> principal was minted here and can be revoked with
    /// <c>stylomail key revoke</c>, while an <c>environment</c> one is a
    /// configuration entry this host does not own and never revokes. A console
    /// that could not tell them apart could not refuse to offer the wrong
    /// thing, and the Host marks the field required so a client cannot silently
    /// assume one.
    /// </remarks>
    public required string Source { get; init; }

    /// <summary>
    /// Which company it belongs to, or null for "nobody has said".
    /// </summary>
    /// <remarks>
    /// <b>Null rather than empty, and the distinction is carried.</b> "Nobody
    /// described this sender" and "described as belonging to no company" are
    /// different facts, and the console groups them differently.
    /// </remarks>
    public string? CompanyId { get; init; }
}

/// <summary>
/// Whether a principal's outbound delivery is paused, and who decided.
/// </summary>
/// <remarks>
/// <b>The pause fields survive a resume, and the console must keep them.</b>
/// "Why was this account stopped for six hours?" is asked after the pause has
/// been lifted, so a view that cleared the reason on resume would answer only
/// the question nobody has.
/// </remarks>
public sealed record SenderControlResponse
{
    public required bool Paused { get; init; }

    public DateTimeOffset? PausedAt { get; init; }

    public string? Reason { get; init; }

    public DateTimeOffset? ResumedAt { get; init; }

    public string? ResumedBy { get; init; }

    public string? ResumeReason { get; init; }

    /// <summary>Whoever acted last, the pause or the resume.</summary>
    public string? UpdatedBy { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// Whether this principal has ever been touched by a control action.
    /// </summary>
    /// <remarks>
    /// Derived rather than sent. A principal with no control record and a
    /// principal that was paused and resumed both arrive with <c>paused</c>
    /// false, and they are not the same history: only the second has an audit
    /// trail, and only the first has nothing to show.
    /// </remarks>
    public bool HasControlHistory => UpdatedAt is not null;
}

/// <summary>
/// <c>GET /v1/messages</c>: a page of messages and their per-recipient progress.
/// </summary>
/// <remarks>
/// Every row is the same projection <c>GET /v1/submissions/{id}</c> serves, so
/// the console's list pane and its detail pane render from one shape rather
/// than two that can drift into disagreeing about the same message.
/// </remarks>
public sealed record MessageListingResponse
{
    public required string TenantId { get; init; }

    /// <summary>Which disposition was asked for, echoed so pages can be told apart.</summary>
    public required string State { get; init; }

    public required IReadOnlyList<SubmissionStatusResponse> Messages { get; init; }

    /// <summary>
    /// Opaque, and echoed back unchanged to fetch the next page.
    /// </summary>
    /// <remarks>
    /// It carries no tenant and is not a capability, so the console treats it
    /// as a token and nothing else: never parsed, re-encoded, or reconstructed
    /// from a row's fields. Deriving it client-side is how a paging bug gets
    /// invented on the wrong side of the wire.
    /// </remarks>
    public string? NextCursor { get; init; }

    public required bool HasMore { get; init; }
}
