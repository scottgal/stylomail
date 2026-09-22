namespace StyloMail.Chat.Slack;

/// <summary>
/// What kind of conversation a message was posted in, as the platform reports it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because "talking to a person" and "posting to an audience" are different
/// claims.</b> A direct message has a counterpart; a channel post goes to whoever happens to be
/// there. Counting both as one relationship makes the fan-out evidence report a member as suddenly
/// talking to new <em>people</em> when they have merely posted in a channel they had not used, which
/// is a confidently wrong measurement rather than a missing one.
/// </para>
/// <para>
/// <see cref="Unknown"/> is a distinct state, not a default. An event that does not say is not
/// evidence for either kind, and guessing "channel" would file a private conversation into the
/// audience pool while guessing the other way would invent a person the event never named.
/// </para>
/// </remarks>
public enum SlackConversationType
{
    /// <summary>The platform did not report one. Not a guess about which it is.</summary>
    Unknown = 0,

    /// <summary>A public channel. An audience rather than a counterpart.</summary>
    Channel = 1,

    /// <summary>A private channel. Still an audience.</summary>
    Group = 2,

    /// <summary>A direct message, whose counterpart is one person.</summary>
    DirectMessage = 3,

    /// <summary>A multi-person direct message, whose counterparts are several people.</summary>
    MultiPersonDirectMessage = 4,
}

/// <summary>
/// Whether a conversation's other side is people or an audience.
/// </summary>
/// <remarks>
/// The distinction the scope key needs. It is derived from <see cref="SlackConversationType"/> rather
/// than stored, so a conversation cannot be recorded as an audience and simultaneously name people.
/// </remarks>
public static class SlackConversation
{
    /// <summary>
    /// True when the conversation's other side is people rather than an audience.
    /// </summary>
    /// <remarks>
    /// False for <see cref="SlackConversationType.Unknown"/> as well as for channels: an unknown
    /// conversation is not a person, because claiming it is would put a member's channel traffic
    /// into the pool that answers "who do they talk to".
    /// </remarks>
    public static bool HasPeopleCounterpart(this SlackConversationType type) =>
        type is SlackConversationType.DirectMessage or SlackConversationType.MultiPersonDirectMessage;
}
