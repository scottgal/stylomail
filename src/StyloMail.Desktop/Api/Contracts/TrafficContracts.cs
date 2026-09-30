namespace StyloMail.Desktop.Api.Contracts;

/// <summary>
/// What kind of thing changed, as the Host announces it.
/// </summary>
/// <remarks>
/// Mirrors <c>StyloMail.Host.Traffic.TrafficEventKind</c>. The Host sends the
/// name, never the number, so reordering the members there cannot silently
/// repoint a live screen here.
/// </remarks>
public enum TrafficNoticeKind
{
    /// <summary>An assessment reached the decision ledger.</summary>
    DecisionRecorded,

    /// <summary>A message's delivery state moved.</summary>
    MessageStateChanged,

    /// <summary>A sending principal was paused or resumed.</summary>
    SenderControlChanged,

    /// <summary>The Host's readiness answer changed.</summary>
    ReadinessChanged,
}

/// <summary>
/// A hint that something changed, and enough to go and look.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>StyloMail.Host.Traffic.TrafficNotice</c>. <b>This is never
/// state.</b> It says "this row moved"; what the row now says is read back over
/// HTTP. A pushed payload rendered directly would make a dropped, duplicated or
/// reordered event a permanently wrong screen, and a console showing a stale
/// verdict as current is worse than one showing nothing.
/// </para>
/// <para>
/// <b><see cref="RawKind"/> is a string and stays one, deliberately, where
/// every other contract here binds a closed enum.</b> The difference is what
/// happens when this build does not recognise a value. On the HTTP surface an
/// unknown enum member is an error, because falling back to a default would
/// render a verdict the Host never gave. Here the fallback is a full re-read of
/// the visible surface, which cannot produce a wrong answer: the re-read is the
/// truth and the notice was only ever a nudge toward it. Binding an enum would
/// instead turn one unreadable hint from a newer Host into a fault that ends
/// the whole feed, which loses the hints this build <em>can</em> read and
/// leaves the operator with a dropped connection and no reason for it.
/// </para>
/// </remarks>
public sealed record TrafficNotice
{
    /// <summary>The kind exactly as it arrived, so an unknown one is not lost.</summary>
    public string? Kind { get; init; }

    /// <summary>
    /// The identifier to re-read, or null when the change needs no identifier.
    /// </summary>
    /// <remarks>
    /// One identifier per change, chosen so that a holder can fetch the thing
    /// that changed: an assessment id resolves through the decision ledger, a
    /// queue id through the submission route, a principal id through the sender
    /// listing. Resolving it into a row is this console's job.
    /// </remarks>
    public string? SubjectId { get; init; }

    /// <summary>When the change happened, by the Host's clock.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// The kind this build understands, or null when it does not understand this one.
    /// </summary>
    /// <remarks>
    /// Null is not an error and is not dropped. See the remarks on this type:
    /// the caller treats it as a change it cannot pin down and re-reads the
    /// whole visible surface, which is always correct and merely less precise.
    /// </remarks>
    public TrafficNoticeKind? Recognised => Kind switch
    {
        nameof(TrafficNoticeKind.DecisionRecorded) => TrafficNoticeKind.DecisionRecorded,
        nameof(TrafficNoticeKind.MessageStateChanged) => TrafficNoticeKind.MessageStateChanged,
        nameof(TrafficNoticeKind.SenderControlChanged) => TrafficNoticeKind.SenderControlChanged,
        nameof(TrafficNoticeKind.ReadinessChanged) => TrafficNoticeKind.ReadinessChanged,
        _ => null,
    };
}
