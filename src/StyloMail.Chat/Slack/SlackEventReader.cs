using System.Globalization;
using System.Text.Json;

namespace StyloMail.Chat.Slack;

/// <summary>Why an event that arrived was not read as a message.</summary>
public enum SlackEventIgnored
{
    NotJson,
    NotAnEventCallback,
    NotAPlainMessage,

    /// <summary>Our own app posted it. Read would mean assessing our own output and acting on it.</summary>
    FromOurBot,

    MissingFields,
}

/// <summary>
/// Turns a verified Slack event into a <see cref="ChatMessage"/>, or says why it did not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every path returns rather than throws.</b> This runs on an unauthenticated HTTP surface, and a
/// parse failure there is a response code, not an exception out of the host.
/// </para>
/// <para>
/// <b>That contract is upheld by checking the value kind before reading a value.</b>
/// <c>JsonElement.GetString()</c> throws <c>InvalidOperationException</c> on anything that is not a
/// string and on an element that was never found, and <c>DateTimeOffset.FromUnixTimeSeconds</c>
/// throws outside its representable range. A body is well-formed JSON whenever it parses at all, so
/// "it is valid JSON" says nothing about whether these calls are safe. Every read here is therefore
/// guarded, and a field of an unexpected type is a missing field rather than an exception.
/// </para>
/// <para>
/// <b>A malformed body is a programming error only when the argument itself is null</b>, which is
/// why that still throws while malformed <em>data</em> never does.
/// </para>
/// <para>
/// <b>Our own app's posts are refused; every other bot's are read.</b> The rule "never assess and act
/// on our own output" is a loop prevention and cannot be an obligation a caller might forget, so the
/// deployment's identity is a parameter here and a caller that supplies none cannot obtain a
/// <see cref="ChatMessage"/> at all. Refusing our own output is not judging bots in general: another
/// integration's post is read and carries <c>BotId</c>, because a workspace whose integration token
/// has been stolen posts phishing through one, and that is the inbound traffic this extension exists
/// to catch.
/// </para>
/// <para>
/// <b>Edits and deletions are ignored, and that is a decision rather than an omission.</b> Slack
/// delivers those as a nested event describing a message that was already read once. Re-reading one
/// as fresh traffic would assess the same message twice, under words it no longer has.
/// </para>
/// </remarks>
public static class SlackEventReader
{
    private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();

    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    public static bool TryRead(
        string json,
        SlackBotIdentity ownIdentity,
        out ChatMessage message,
        out SlackEventIgnored reason)
    {
        ArgumentNullException.ThrowIfNull(ownIdentity);

        message = null!;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            reason = SlackEventIgnored.NotJson;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (SafeString(root, "type") != "event_callback")
            {
                reason = SlackEventIgnored.NotAnEventCallback;
                return false;
            }

            if (!root.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            {
                reason = SlackEventIgnored.MissingFields;
                return false;
            }

            // Absent type and a present subtype are the same refusal, so both are read the same way
            // and neither can dereference an element that was never there.
            if (SafeString(evt, "type") != "message" || SafeString(evt, "subtype") is not null)
            {
                reason = SlackEventIgnored.NotAPlainMessage;
                return false;
            }

            if (!TryString(root, "event_id", out var eventId)
                || !TryString(root, "team_id", out var workspaceId)
                || !TryString(evt, "channel", out var channelId)
                || !TryString(evt, "ts", out var ts))
            {
                reason = SlackEventIgnored.MissingFields;
                return false;
            }

            // A bot's post does not always carry a user: the platform has attributed it to the bot
            // itself, so the bot id is the author. Requiring `user` would drop those messages as
            // malformed, which is a policy about bots hiding inside an error path.
            var botId = SafeString(evt, "bot_id");
            var userId = SafeString(evt, "user");

            // Checked before the message is built, so our own output cannot be constructed let alone
            // assessed. See SlackBotIdentity for why this is the reader's job rather than a rule the
            // caller is trusted to apply.
            if (ownIdentity.IsOurOwnPost(botId, userId))
            {
                reason = SlackEventIgnored.FromOurBot;
                return false;
            }

            var authorId = userId ?? botId;
            if (string.IsNullOrEmpty(authorId))
            {
                reason = SlackEventIgnored.MissingFields;
                return false;
            }

            // Checked for finiteness before the bounds, because NaN compares false against both and
            // a NaN reaching the conversion would silently become the epoch rather than a refusal.
            if (!double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out var unixSeconds)
                || !double.IsFinite(unixSeconds)
                || unixSeconds < MinUnixSeconds
                || unixSeconds > MaxUnixSeconds)
            {
                reason = SlackEventIgnored.MissingFields;
                return false;
            }

            message = new ChatMessage
            {
                ChannelKind = Core.ChannelKind.Slack,
                EventId = eventId,
                WorkspaceId = workspaceId,
                ChannelId = channelId,
                ThreadId = SafeString(evt, "thread_ts"),
                AuthorId = authorId,
                BotId = botId,
                IsExternal = IsExternalAuthor(evt, workspaceId),
                Text = SafeString(evt, "text") ?? string.Empty,
                OccurredAt = DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds),
            };

            reason = default;
            return true;
        }
    }

    /// <summary>
    /// Whether the author belongs to a workspace other than the one the event arrived for.
    /// </summary>
    /// <remarks>
    /// Presence of <c>user_team</c> alone is not enough, because an Enterprise Grid delivers it for
    /// members too: the test is whether it names a different workspace from the event's own
    /// <c>team_id</c>. Absent means the platform is placing the author in this workspace, which is
    /// the member case rather than an unknown one, and it is the case that must not be guessed at
    /// backwards: a member is an authenticated principal.
    /// </remarks>
    private static bool IsExternalAuthor(JsonElement evt, string workspaceId) =>
        SafeString(evt, "user_team") is { Length: > 0 } authorTeam
        && !string.Equals(authorTeam, workspaceId, StringComparison.Ordinal);

    /// <summary>
    /// The property as a string, or null when it is absent or is not a string.
    /// </summary>
    /// <remarks>
    /// The value-kind test is the whole point: without it a body carrying a number where a string
    /// belongs throws out of a method that documents that it does not.
    /// </remarks>
    private static string? SafeString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static bool TryString(JsonElement element, string name, out string value)
    {
        if (SafeString(element, name) is { Length: > 0 } found)
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
