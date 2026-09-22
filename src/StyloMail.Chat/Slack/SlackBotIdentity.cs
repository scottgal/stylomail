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
    /// <para>
    /// <b>Production cannot produce this value.</b> It reads every bot's posts, including our own,
    /// which is the loop described above, so an ingress configured with it is a configuration error
    /// and must fail to start rather than run in a permissive degraded mode. A missing value that
    /// degrades into a permissive default works perfectly in tests and is wrong in production, which
    /// is why this project already fails loudly on a missing secret for the same reason.
    /// </para>
    /// <para>
    /// <b>It exists so the degenerate state is nameable and testable</b> rather than reached by
    /// accident, and so that a test can assert what it does instead of a reader inferring it.
    /// Enforcing that startup refuses it belongs to the ingress in plan 2b Task 4, not to this
    /// record, which cannot see whether it was configured.
    /// </para>
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
