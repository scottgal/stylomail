namespace StyloMail.Host.Traffic;

/// <summary>What kind of thing changed.</summary>
/// <remarks>
/// <b>Travels as a name, never as a number.</b> The console switches on this value, so a numeric
/// payload would make its meaning depend on declaration order here: reordering the members would
/// silently repoint every live screen at the wrong kind of change, and nothing would fail. The same
/// rule the HTTP surface applies to <c>MailAction</c> for the same reason.
/// </remarks>
public enum TrafficEventKind
{
    /// <summary>An assessment was recorded in the ledger.</summary>
    DecisionRecorded = 0,

    /// <summary>A message's delivery state moved: released, attempted, settled.</summary>
    MessageStateChanged = 1,

    /// <summary>A sending principal was paused or resumed.</summary>
    SenderControlChanged = 2,

    /// <summary>The host's readiness answer changed.</summary>
    ReadinessChanged = 3,
}

/// <summary>
/// Something changed, named so a reader can go and look.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a hint, never state.</b> It says "this row moved"; the console re-reads the row over
/// HTTP. A pushed payload rendered directly would make a dropped, duplicated or reordered event a
/// permanently wrong screen, and a console showing a stale verdict as current is worse than one
/// showing nothing at all. That is why there is no field here for what the change <em>was</em>:
/// only for which thing changed and when.
/// </para>
/// <para>
/// <see cref="TenantId"/> is the routing key rather than content, and nothing here refuses to build
/// an event that lacks one. That is deliberate: a guard on this type would run inside an assessment
/// or a delivery, which is exactly where nothing about this seam is allowed to fail. The question
/// "can this be addressed" is answered where the address is chosen, see <see cref="IsHostScoped"/>,
/// and the answer there is to drop rather than to throw.
/// </para>
/// </remarks>
public sealed record TrafficEvent
{
    public required TrafficEventKind Kind { get; init; }

    /// <summary>The tenant this belongs to, or null for a fact about the host itself.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The identifier to re-read, or null when the change needs no identifier.
    /// </summary>
    /// <remarks>
    /// One identifier per change, chosen so that a client holding it can fetch the thing that
    /// changed on its own: an assessment id resolves through the decision ledger, a queue id
    /// through the submission route, a principal id through the sender listing. A change that
    /// carried two would be inviting a client to join them, which is the console's job and not a
    /// claim this seam should be making.
    /// </remarks>
    public string? SubjectId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>An assessment reached the ledger.</summary>
    /// <remarks>
    /// The ledger write <em>is</em> the completion boundary: before it there is no decision to look
    /// up, and after it there is. Emitting here rather than from the pipeline means the event cannot
    /// announce a decision that was never recorded.
    /// </remarks>
    public static TrafficEvent DecisionRecorded(string tenantId, string assessmentId, DateTimeOffset occurredAt)
        => new()
        {
            Kind = TrafficEventKind.DecisionRecorded,
            TenantId = tenantId,
            SubjectId = assessmentId,
            OccurredAt = occurredAt,
        };

    /// <summary>A message's delivery state moved.</summary>
    public static TrafficEvent MessageStateChanged(string tenantId, string queueId, DateTimeOffset occurredAt)
        => new()
        {
            Kind = TrafficEventKind.MessageStateChanged,
            TenantId = tenantId,
            SubjectId = queueId,
            OccurredAt = occurredAt,
        };

    /// <summary>A sending principal was paused or resumed.</summary>
    /// <remarks>
    /// One kind for both directions, deliberately. Which way it moved is state, and state on this
    /// wire is what the first rule forbids: the console re-reads the sender and renders what it
    /// finds. A direction here would be a second source of truth for something already durable.
    /// </remarks>
    public static TrafficEvent SenderControlChanged(string tenantId, string principalId, DateTimeOffset occurredAt)
        => new()
        {
            Kind = TrafficEventKind.SenderControlChanged,
            TenantId = tenantId,
            SubjectId = principalId,
            OccurredAt = occurredAt,
        };

    /// <summary>The host's readiness answer changed.</summary>
    /// <remarks>
    /// No tenant and no identifier: readiness is a property of the host, and the answer itself is
    /// read back from <c>/health/ready</c>. Carrying "ready" or "not ready" here would be the state
    /// the first rule forbids, and it would be the one field a console might be tempted to render.
    /// </remarks>
    public static TrafficEvent ReadinessChanged(DateTimeOffset occurredAt)
        => new()
        {
            Kind = TrafficEventKind.ReadinessChanged,
            OccurredAt = occurredAt,
        };

    /// <summary>
    /// The event as it travels: the hint, and nothing that identifies who is being told.
    /// </summary>
    /// <remarks>
    /// <b>Written out rather than serialising this record.</b> The tenant is the routing key, so
    /// publishing it would restate what the group already decided, and a field added here later for
    /// internal use would be published to every subscriber by default. The console's contract is
    /// these three fields, and this is where that contract is visible.
    /// </remarks>
    public TrafficNotice ToNotice() => new()
    {
        Kind = Kind,
        SubjectId = SubjectId,
        OccurredAt = OccurredAt,
    };

    /// <summary>
    /// Whether this is a fact about the host rather than about a tenant.
    /// </summary>
    /// <remarks>
    /// <b>The two destinations are a tenant's group and every connection, and this is what chooses
    /// between them.</b> Anything that is not host-scoped is addressed to its tenant, and a change
    /// that is not host-scoped and has no tenant is addressed to nobody: it is dropped rather than
    /// broadcast, because the only other option would put one tenant's activity in front of every
    /// other tenant's console. A kind added later and not named here therefore fails closed.
    /// </remarks>
    public bool IsHostScoped => Kind == TrafficEventKind.ReadinessChanged;
}

/// <summary>What a subscriber receives: which thing changed, and when.</summary>
/// <remarks>
/// Deliberately not <see cref="TrafficEvent"/>. This is the published contract, so adding a field
/// to the event is not a wire change by accident.
/// </remarks>
public sealed record TrafficNotice
{
    public required TrafficEventKind Kind { get; init; }

    public string? SubjectId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
