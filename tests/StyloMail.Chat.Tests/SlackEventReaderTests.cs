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

    [Fact]
    public void A_message_event_becomes_a_chat_message()
    {
        Assert.True(SlackEventReader.TryRead(MessageEvent, out var message, out _));

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

        Assert.True(SlackEventReader.TryRead(json, out var message, out _));
        Assert.Equal("1760000000.000100", message.ThreadId);
    }

    [Fact]
    public void A_message_the_system_posted_itself_is_ignored()
    {
        // A bot's own posts come back as message events. Assessing them would make the system read
        // its own output as traffic, and act on it.
        var json = MessageEvent.Replace("\"user\":\"U01\"", "\"bot_id\":\"B01\"");

        Assert.False(SlackEventReader.TryRead(json, out _, out var reason));
        Assert.Equal(SlackEventIgnored.FromABot, reason);
    }

    [Fact]
    public void An_edited_message_is_ignored_because_it_was_already_read()
    {
        // message_changed and message_deleted wrap the original inside a nested event, and reading
        // one as a fresh message would assess the same content twice with different words.
        var json = """{"type":"event_callback","event_id":"Ev02","team_id":"T01","event":{"type":"message","subtype":"message_changed","channel":"C01"}}""";

        Assert.False(SlackEventReader.TryRead(json, out _, out var reason));
        Assert.Equal(SlackEventIgnored.NotAPlainMessage, reason);
    }

    [Theory]
    [InlineData("""{"type":"url_verification","challenge":"abc"}""")]
    [InlineData("""{"type":"event_callback","event":{"type":"reaction_added"}}""")]
    [InlineData("not json at all")]
    public void Anything_that_is_not_a_plain_message_is_ignored_rather_than_thrown(string json)
    {
        Assert.False(SlackEventReader.TryRead(json, out _, out _));
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
        Assert.False(SlackEventReader.TryRead(json, out _, out _));
    }

    [Fact]
    public void A_text_that_is_not_a_string_is_read_as_no_text()
    {
        // Unlike the fields above, text is already optional here: an absent one becomes the empty
        // string. A wrong-typed one therefore degrades the same way, rather than costing the whole
        // message over a field the reader was already willing to do without.
        var json = """{"type":"event_callback","event_id":"Ev1","team_id":"T01","event":{"type":"message","channel":"C01","user":"U01","ts":"1760000000.0001","text":123}}""";

        Assert.True(SlackEventReader.TryRead(json, out var message, out _));
        Assert.Equal(string.Empty, message.Text);
    }

    [Fact]
    public void A_thread_that_is_not_a_string_is_read_as_no_thread()
    {
        var json = """{"type":"event_callback","event_id":"Ev1","team_id":"T01","event":{"type":"message","channel":"C01","user":"U01","ts":"1760000000.0001","thread_ts":123}}""";

        Assert.True(SlackEventReader.TryRead(json, out var message, out _));
        Assert.Null(message.ThreadId);
    }
}
