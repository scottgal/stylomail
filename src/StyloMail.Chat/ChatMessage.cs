using StyloMail.Chat.Slack;
using StyloMail.Core;

namespace StyloMail.Chat;

/// <summary>
/// One message from a chat platform, in the only shape the rest of the system sees.
/// </summary>
/// <remarks>
/// <para>
/// <b>Platform fields are decoded here and nowhere else.</b> Everything downstream reads this and
/// nothing reads Slack's JSON, so a second platform is a second reader rather than a second
/// pipeline.
/// </para>
/// <para>
/// <b><see cref="EventId"/> is the platform's own identifier, not ours.</b> It is what makes
/// deduplication possible when the platform retries a delivery, and it is carried rather than
/// derived from the content so that two identical messages are still two messages.
/// </para>
/// </remarks>
public sealed record ChatMessage
{
    public required ChannelKind ChannelKind { get; init; }

    public required string EventId { get; init; }

    public required string WorkspaceId { get; init; }

    public required string ChannelId { get; init; }

    public string? ThreadId { get; init; }

    public required string AuthorId { get; init; }

    /// <summary>
    /// The platform's id for the bot that posted this, when a bot posted it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Recorded, never judged here.</b> Whether this bot is <em>us</em> is a decision that needs
    /// the deployment's own identity, which this layer does not have, so the fact is carried and the
    /// caller compares it. That keeps "we must not assess our own output" a rule the caller enforces
    /// rather than a rule this reader silently applies to every bot.
    /// </para>
    /// <para>
    /// The distinction matters: a workspace whose integration token has been stolen posts phishing
    /// through a bot, and that is inbound traffic worth assessing. Dropping every bot message because
    /// one of them might be ours would hide exactly the traffic this extension was built for.
    /// </para>
    /// </remarks>
    public string? BotId { get; init; }

    /// <summary>
    /// True when the author belongs to a workspace other than the one the event was delivered to.
    /// </summary>
    /// <remarks>
    /// The fact the assessment's direction is derived from, and therefore which profile pool its
    /// observations land in. A member is an authenticated principal and a stranger is not, and the
    /// two must never be counted together.
    /// </remarks>
    public required bool IsExternal { get; init; }

    /// <summary>
    /// What kind of conversation the message was posted in.
    /// </summary>
    /// <remarks>
    /// Carried because the fan-out evidence has to distinguish talking to a person from posting to
    /// an audience, and that distinction has to survive into the profile key rather than being
    /// reconstructed later from the channel id.
    /// </remarks>
    public required SlackConversationType Conversation { get; init; }

    /// <summary>The message text as posted, unmodified.</summary>
    public required string Text { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
