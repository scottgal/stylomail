using StyloMail.Chat.Slack;

namespace StyloMail.Chat.Tests;

public sealed class SlackEventReaderTests
{
    private const string MessageEvent = """
        {"type":"event_callback","event_id":"Ev01","event_time":1760000000,
         "team_id":"T01",
         "event":{"type":"message","channel":"C01","user":"U01","text":"hello",
                  "ts":"1760000000.000100"}}
        """;

    /// <summary>The deployment's own app, so a post by it is recognised as our own output.</summary>
    private static readonly SlackBotIdentity Own = new() { BotId = "B0OWN", BotUserId = "U0OWN" };

    [Fact]
    public void A_message_event_becomes_a_chat_message()
    {
        Assert.True(SlackEventReader.TryRead(MessageEvent, Own, out var message, out _));

        Assert.Equal(Core.ChannelKind.Slack, message.ChannelKind);
        Assert.Equal("T01", message.WorkspaceId);
        Assert.Equal("C01", message.ChannelId);
        Assert.Equal("U01", message.AuthorId);
        Assert.Equal("hello", message.Text);
        Assert.Equal("Ev01", message.EventId);
        Assert.Null(message.ThreadId);
    }

    [Fact]
    public void A_threaded_reply_carries_its_thread()
    {
        var json = MessageEvent.Replace("\"ts\":\"1760000000.000100\"",
            "\"ts\":\"1760000000.000200\",\"thread_ts\":\"1760000000.000100\"");

        Assert.True(SlackEventReader.TryRead(json, Own, out var message, out _));
        Assert.Equal("1760000000.000100", message.ThreadId);
    }

    [Fact]
    public void Another_bots_message_is_read_and_carries_which_bot_posted_it()
    {
        // A workspace whose integration token has been stolen posts phishing with it, and that is
        // inbound traffic this extension exists to catch. So another integration's posts are read.
        //
        // Refusing our own app's posts is not judging bots in general: it is only refusing to eat our
        // own output, which is why the identity is a parameter and everything else is read.
        var json = MessageEvent.Replace("\"user\":\"U01\"", "\"bot_id\":\"B01\",\"user\":\"U01\"");

        Assert.True(SlackEventReader.TryRead(json, Own, out var message, out _));
        Assert.Equal("B01", message.BotId);
        Assert.Equal("U01", message.AuthorId);
    }

    [Fact]
    public void A_bot_post_with_no_user_is_attributed_to_the_bot_itself()
    {
        // Slack does not always send a user for a bot's post: the platform has attributed the
        // message to the bot. Refusing it for a missing user would silently drop every such message
        // while looking like a malformed-event problem rather than the policy it would actually be.
        var json = MessageEvent.Replace("\"user\":\"U01\"", "\"bot_id\":\"B01\"");

        Assert.True(SlackEventReader.TryRead(json, Own, out var message, out _));
        Assert.Equal("B01", message.BotId);
        Assert.Equal("B01", message.AuthorId);
    }

    [Fact]
    public void A_message_from_a_person_carries_no_bot_identifier()
    {
        Assert.True(SlackEventReader.TryRead(MessageEvent, Own, out var message, out _));
        Assert.Null(message.BotId);
        Assert.Equal("U01", message.AuthorId);
    }

    [Fact]
    public void An_edited_message_is_ignored_because_it_was_already_read()
    {
        // message_changed and message_deleted wrap the original inside a nested event, and reading
        // one as a fresh message would assess the same content twice with different words.
        var json = """{"type":"event_callback","event_id":"Ev02","team_id":"T01","event":{"type":"message","subtype":"message_changed","channel":"C01"}}""";

        Assert.False(SlackEventReader.TryRead(json, Own, out _, out var reason));
        Assert.Equal(SlackEventIgnored.NotAPlainMessage, reason);
    }

    [Theory]
    [InlineData("""{"type":"url_verification","challenge":"abc"}""")]
    [InlineData("""{"type":"event_callback","event":{"type":"reaction_added"}}""")]
    [InlineData("not json at all")]
    public void Anything_that_is_not_a_plain_message_is_ignored_rather_than_thrown(string json)
    {
        Assert.False(SlackEventReader.TryRead(json, Own, out _, out _));
    }

    [Theory]
    // A field present but of the wrong JSON type. JsonElement.GetString() throws
    // InvalidOperationException on anything that is not a string, so each of these is a way for a
    // well-formed body to take an exception out of a parser that promises none.
    [InlineData("""{"type":"event_callback","event":{"type":123,"channel":"C01"}}""")]
    [InlineData("""{"type":"event_callback","event":{"type":"message","subtype":5,"channel":"C01"}}""")]
    [InlineData("""{"type":"event_callback","event":{"subtype":"message_changed","channel":"C01"}}""")]
    [InlineData("""{"type":"event_callback","event_id":123,"team_id":"T01","event":{"type":"message","channel":"C01","user":"U01","ts":"1760000000.0001"}}""")]
    // A timestamp that is not a number, and two that DateTimeOffset would refuse. Slack's ts is
    // "seconds.microseconds", and none of these is.
    [InlineData("""{"type":"event_callback","event_id":"Ev1","team_id":"T01","event":{"type":"message","channel":"C01","user":"U01","ts":"not-a-number"}}""")]
    [InlineData("""{"type":"event_callback","event_id":"Ev1","team_id":"T01","event":{"type":"message","channel":"C01","user":"U01","ts":"99999999999999"}}""")]
    [InlineData("""{"type":"event_callback","event_id":"Ev1","team_id":"T01","event":{"type":"message","channel":"C01","user":"U01","ts":"NaN"}}""")]
    public void A_required_field_of_the_wrong_json_type_is_refused_rather_than_thrown(string json)
    {
        // The contract is that nothing here throws, because this runs on an unauthenticated surface
        // where a parse failure is a response code and not an exception out of the host. A shape
        // that has never been seen is exactly when that contract gets tested. Without the event
        // there is no message, so these are refusals rather than a degraded read.
        Assert.False(SlackEventReader.TryRead(json, Own, out _, out _));
    }

    [Fact]
    public void A_text_that_is_not_a_string_is_read_as_no_text()
    {
        // Unlike the fields above, text is already optional here: an absent one becomes the empty
        // string. A wrong-typed one therefore degrades the same way, rather than costing the whole
        // message over a field the reader was already willing to do without.
        var json = """{"type":"event_callback","event_id":"Ev1","team_id":"T01","event":{"type":"message","channel":"C01","user":"U01","ts":"1760000000.0001","text":123}}""";

        Assert.True(SlackEventReader.TryRead(json, Own, out var message, out _));
        Assert.Equal(string.Empty, message.Text);
    }

    [Fact]
    public void A_thread_that_is_not_a_string_is_read_as_no_thread()
    {
        var json = """{"type":"event_callback","event_id":"Ev1","team_id":"T01","event":{"type":"message","channel":"C01","user":"U01","ts":"1760000000.0001","thread_ts":123}}""";

        Assert.True(SlackEventReader.TryRead(json, Own, out var message, out _));
        Assert.Null(message.ThreadId);
    }

    [Fact]
    public void Our_own_bots_post_is_refused_rather_than_read()
    {
        // The loop this prevents needs no attacker: we post, the platform delivers it back, we assess
        // it, and an action posts again. It is a property of the reader rather than an obligation on
        // the caller, because a caller that could forget is a caller that eventually does, and a
        // caller with no identity to supply cannot obtain a message at all.
        var json = MessageEvent.Replace("\"user\":\"U01\"", "\"bot_id\":\"B0OWN\",\"user\":\"U0OWN\"");

        Assert.False(SlackEventReader.TryRead(json, Own, out _, out var reason));
        Assert.Equal(SlackEventIgnored.FromOurBot, reason);
    }

    [Theory]
    // Which identifier the platform carries on a given post is a fact about its payloads rather than
    // something to assume, so either one is enough to recognise our own output.
    [InlineData("\"bot_id\":\"B0OWN\"")]
    [InlineData("\"user\":\"U0OWN\"")]
    public void Our_own_bot_is_recognised_by_either_identifier(string authorFields)
    {
        var json = MessageEvent.Replace("\"user\":\"U01\"", authorFields);

        Assert.False(SlackEventReader.TryRead(json, Own, out _, out var reason));
        Assert.Equal(SlackEventIgnored.FromOurBot, reason);
    }

    [Fact]
    public void An_unconfigured_identity_refuses_nothing()
    {
        // Stated rather than assumed: a deployment that has not been given its own identity reads
        // every bot's posts, including its own. Task 4 is where the identity is configured, and that
        // is where an empty one has to be a startup failure rather than a silent loop.
        var json = MessageEvent.Replace("\"user\":\"U01\"", "\"bot_id\":\"B0OWN\"");

        Assert.True(SlackEventReader.TryRead(json, SlackBotIdentity.None, out var message, out _));
        Assert.Equal("B0OWN", message.BotId);
    }
}
