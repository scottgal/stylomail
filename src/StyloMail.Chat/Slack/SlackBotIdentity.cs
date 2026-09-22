namespace StyloMail.Chat.Slack;

/// <summary>
/// The deployment's own identity on Slack, so its own output is never read as traffic.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists to break a loop, and the loop needs no attacker.</b> We post, the platform delivers
/// the post back as a message event, it arrives through the ingress, it is assessed, and an action
/// posts again. That is worse than most of what this system hunts because it is self-sustaining, so
/// the rule cannot be an obligation a caller is trusted to remember. It is enforced in
/// <see cref="SlackEventReader"/> instead: a caller with no identity to supply cannot obtain a
/// <see cref="ChatMessage"/> at all.
/// </para>
/// <para>
/// <b>Recognising our own app is not judging bots in general.</b> Another integration's posts are
/// read and assessed, because a workspace whose integration token has been stolen posts phishing
/// through one, and that is the inbound job. What is refused here is only our own output.
/// </para>
/// <para>
/// <b>Where these values come from.</b> The app's own identity is known at install time rather than
/// per event: the OAuth v2 access response carries the installing app's bot user id and app id, and
/// <c>auth.test</c> reports the authenticated bot's ids. Both are configuration the deployment
/// already holds, so nothing here needs a call on the message path.
/// </para>
/// </remarks>
public sealed record SlackBotIdentity
{
    /// <summary>
    /// No identity configured.
    /// </summary>
    /// <remarks>
    /// <b>A deployment must not run with this.</b> It reads every bot's posts, including its own,
    /// which is the loop described above. It exists so that the degenerate configuration is a value
    /// that can be named and tested rather than an accident, and configuring the identity is an
    /// ingress-startup obligation rather than something this record can enforce.
    /// </remarks>
    public static SlackBotIdentity None { get; } = new();

    /// <summary>The app's own bot id, as the platform reports it on a message.</summary>
    public string? BotId { get; init; }

    /// <summary>The app's own bot user id, as the install reports it.</summary>
    public string? BotUserId { get; init; }

    /// <summary>
    /// True when a message carrying these author identifiers was posted by this deployment's app.
    /// </summary>
    /// <remarks>
    /// Both identifiers are checked because which one a given post carries is a fact about the
    /// platform's payloads rather than something to assume. A bot's post may carry a bot id, a user
    /// id, or both, and the record refuses a post matching either.
    /// </remarks>
    public bool IsOurOwnPost(string? botId, string? userId) =>
        Matches(BotId, botId) || Matches(BotUserId, userId);

    private static bool Matches(string? ours, string? posted) =>
        ours is { Length: > 0 } && string.Equals(ours, posted, StringComparison.Ordinal);
}
