using StyloMail.Chat.Slack;
using StyloMail.Core;

namespace StyloMail.Chat;

/// <summary>
/// Turns a normalised chat message into the analysis input the rest of the system reads.
/// </summary>
/// <remarks>
/// The last step that knows anything about the platform. Everything downstream reads
/// <see cref="ChatAnalysisInput"/> and nothing reads a <see cref="ChatMessage"/>, so a second
/// platform is a second reader rather than a second pipeline.
/// </remarks>
public static class ChatInputFactory
{
    public static ChatAnalysisInput From(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new ChatAnalysisInput
        {
            Channel = new ChannelContext
            {
                Kind = message.ChannelKind,
                WorkspaceId = message.WorkspaceId,
                ChannelId = message.ChannelId,
                ThreadId = message.ThreadId,
            },

            EventId = message.EventId,

            // The platform's four types mapped onto the engine's two, here rather than downstream,
            // so a second platform maps its own onto the same two.
            Conversation = message.Conversation switch
            {
                SlackConversationType.DirectMessage or SlackConversationType.MultiPersonDirectMessage
                    => ChatConversationKind.People,
                SlackConversationType.Channel or SlackConversationType.Group
                    => ChatConversationKind.Audience,
                _ => ChatConversationKind.Unknown,
            },

            Membership = new ChatMembershipFacts
            {
                AuthorId = message.AuthorId,
                BotId = message.BotId,
                IsExternal = message.IsExternal,
            },

            BodyText = message.Text,

            // Observations. The hosts are recorded because the platform asserted them and a reader
            // may want them; whether they are a lure is decided downstream.
            Links = [.. SlackLinkMarkup.Parse(message.Text).Select(Observe)],

            // No bounded prior context is available from a single event. Null is the honest answer,
            // and it is distinct from an empty list, which would claim we looked and found none.
            ConversationContext = null,

            OccurredAt = message.OccurredAt,
        };
    }

    private static LinkObservation Observe(PresentedLink link)
    {
        var target = UrlTools.Observe(link.ActualTarget);

        return new LinkObservation
        {
            DisplayedText = link.DisplayedText,
            ActualTarget = link.ActualTarget,
            UnicodeHost = target?.UnicodeHost,
            AsciiHost = target?.AsciiHost,
        };
    }
}
