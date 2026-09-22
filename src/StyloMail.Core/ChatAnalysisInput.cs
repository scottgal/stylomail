namespace StyloMail.Core;

/// <summary>
/// What the platform asserted about who sent a message, and nothing it did not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded by its shape, not by a stated limit.</b> A fixed set of named facts cannot grow without
/// somebody naming a new one; a key/value map with a stated bound is bounded by discipline, and the
/// first connector that wants a new key is the one that finds out which it was.
/// </para>
/// <para>
/// <b>Context and never a verdict.</b> The platform authenticates the member and not the message, so
/// a compromised account is authenticated exactly as a legitimate one is, and none of this may be
/// read as evidence of good intent. It is what the platform asserted, kept separate from message
/// content for the same reason tagged context is.
/// </para>
/// </remarks>
public sealed record ChatMembershipFacts
{
    /// <summary>The author the platform attributed the message to.</summary>
    public required string AuthorId { get; init; }

    /// <summary>
    /// The platform's id for the bot that posted, when a bot posted it.
    /// </summary>
    /// <remarks>
    /// Carried rather than folded into a boolean so the caller can compare it against the
    /// deployment's own bot identity. That comparison is how "never assess our own output" is
    /// enforced, while another integration's post stays visible.
    /// </remarks>
    public string? BotId { get; init; }

    /// <summary>
    /// True when the platform attributed this to a bot rather than to a person.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored, so a record cannot hold a <see cref="BotId"/> and simultaneously
    /// claim not to be a bot. Normal automation is not inherently abusive, so this is context an
    /// assessment weighs rather than a reason to hide the traffic.
    /// </remarks>
    public bool IsBot => BotId is not null;

    /// <summary>
    /// True when the author is outside the workspace this deployment watches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because the direction cannot be assumed.</b> A workspace member is an
    /// authenticated principal and a stranger is not, and <see cref="Direction"/> is derived from
    /// that difference rather than fixed for the channel. Slack reports it as <c>user_team</c> on
    /// the author: absent, or equal to the event's team, means a member, and present and different
    /// means an author from another workspace, which is the Enterprise Grid and Slack Connect case.
    /// </para>
    /// <para>
    /// Required rather than defaulted, because a default here would silently file a member's traffic
    /// in the stranger pool. If a deployment's platform cannot answer it, that is a gap to report at
    /// the connector rather than a value to guess at here.
    /// </para>
    /// </remarks>
    public required bool IsExternal { get; init; }

    /// <summary>
    /// Which direction this author's traffic counts as, and therefore which profile pool it lands in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Derived, never stored, and never fixed per channel.</b> The direction is not a label: it
    /// selects the pool the observations are counted in, and the adaptive engine keeps inbound and
    /// outbound distinct precisely because an inbound stranger and an outbound authenticated
    /// principal carry different meaning for the same number.
    /// </para>
    /// <para>
    /// <b>A member is outbound.</b> Compromised-account detection is the outbound case by
    /// definition, an authenticated principal fanning out to people it never talks to, and that is
    /// the job this extension gets behavioural evidence for free. Calling every chat message inbound
    /// would hide exactly that.
    /// </para>
    /// </remarks>
    public MailDirection Direction => IsExternal ? MailDirection.Inbound : MailDirection.Outbound;
}

/// <summary>
/// The normalised analysis view of a chat message.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sibling of <see cref="MailAnalysisInput"/>, not a widening of it.</b> Input is per channel
/// and output is shared: a chat message has no envelope and no DKIM result, and a mail record
/// carrying fields that are always null for chat would be a record that misdescribes what it holds.
/// What both channels produce is the same <see cref="MailAssessment"/>.
/// </para>
/// <para>
/// <b>Everything here is untrusted data</b>, including any imperative text in the body. It is
/// supplied to the analysis as data, and instructions embedded in a message carry no authority over
/// the assessment.
/// </para>
/// </remarks>
public sealed record ChatAnalysisInput
{
    /// <summary>The channel this message arrived on, and what it tells us about reach.</summary>
    public required ChannelContext Channel { get; init; }

    /// <summary>The platform's own identifiers for the message.</summary>
    /// <remarks>
    /// The platform's event id, carried so a decision can name the message it is about and so a
    /// redelivery of the same event is recognisable as the same message rather than a new one.
    /// </remarks>
    public required string EventId { get; init; }

    public required ChatMembershipFacts Membership { get; init; }

    /// <summary>The message text as posted, unmodified.</summary>
    public required string BodyText { get; init; }

    /// <summary>
    /// Links as they appeared in the message, which is what it contained rather than what the
    /// analysis made of them.
    /// </summary>
    public required IReadOnlyList<LinkObservation> Links { get; init; }

    /// <summary>Bounded prior thread context, when available.</summary>
    public IReadOnlyList<string>? ConversationContext { get; init; }

    /// <summary>
    /// When the platform says the message was posted.
    /// </summary>
    /// <remarks>
    /// The message's own time rather than the assessment's, because rate and velocity evidence is
    /// about when the traffic happened and an assessment made later would report the time it was
    /// read as though it were the time it was sent.
    /// </remarks>
    public required DateTimeOffset OccurredAt { get; init; }
}
