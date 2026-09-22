using StyloMail.Chat.Slack;
using StyloMail.Core;

namespace StyloMail.Chat.Tests;

/// <summary>
/// From a Slack message to deterministic evidence, without a network call and without a model.
/// </summary>
public sealed class ChatEvidenceTests
{
    // ---------------------------------------------------------------------------------------------
    // The platform's link markup, which is where a chat message hides a display text
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_slack_link_yields_its_label_and_its_target_separately()
    {
        // The markup exists to hide a destination behind a label, so reading it as one string would
        // destroy the comparison before anything had a chance to make it.
        var links = SlackLinkMarkup.Parse("please see <http://paypal.com.evil.example|paypal.com> now");

        var link = Assert.Single(links);
        Assert.Equal("paypal.com", link.DisplayedText);
        Assert.Equal("http://paypal.com.evil.example", link.ActualTarget);
    }

    [Fact]
    public void A_slack_link_with_no_label_is_its_own_display_text()
    {
        var links = SlackLinkMarkup.Parse("see <http://example.com/x> for details");

        var link = Assert.Single(links);
        Assert.Equal("http://example.com/x", link.DisplayedText);
        Assert.Equal("http://example.com/x", link.ActualTarget);
    }

    [Fact]
    public void A_bare_url_in_plain_text_is_still_found()
    {
        // Not every message uses the markup, and a URL pasted as plain text is the same lure.
        var links = SlackLinkMarkup.Parse("go to http://example.com/y");

        var link = Assert.Single(links);
        Assert.Equal("http://example.com/y", link.ActualTarget);
    }

    [Fact]
    public void A_label_containing_the_separator_character_does_not_break_the_parse()
    {
        // The first pipe separates. A second is part of the label, which is what Slack renders.
        var links = SlackLinkMarkup.Parse("<http://example.com|a|b>");

        var link = Assert.Single(links);
        Assert.Equal("http://example.com", link.ActualTarget);
        Assert.Equal("a|b", link.DisplayedText);
    }

    [Fact]
    public void Text_that_happens_to_contain_angle_brackets_is_not_a_link()
    {
        Assert.Empty(SlackLinkMarkup.Parse("no links here, just <text>"));
    }

    // ---------------------------------------------------------------------------------------------
    // The normaliser
    // ---------------------------------------------------------------------------------------------

    private static ChatMessage Message(string text) => new()
    {
        ChannelKind = ChannelKind.Slack,
        EventId = "Ev01",
        WorkspaceId = "T01",
        ChannelId = "C01",
        AuthorId = "U01",
        IsExternal = false,
        Text = text,
        OccurredAt = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000),
    };

    [Fact]
    public void The_input_names_the_channel_the_platform_asserted()
    {
        var input = ChatInputFactory.From(Message("hello"));

        Assert.Equal(ChannelKind.Slack, input.Channel.Kind);
        Assert.Equal("T01", input.Channel.WorkspaceId);
        Assert.Equal("C01", input.Channel.ChannelId);
        Assert.Null(input.Channel.ThreadId);
    }

    [Fact]
    public void The_input_carries_links_as_observations_and_not_as_findings()
    {
        // Structural decision 8. What the message contained is an observation; what the analysis
        // makes of it is a judgement. This asserts the connector stops at the observation, so the
        // producer below is the only thing that judges.
        var input = ChatInputFactory.From(Message("<http://example.com|example>"));

        var link = Assert.Single(input.Links);
        Assert.Equal("example", link.DisplayedText);
        Assert.Equal("http://example.com", link.ActualTarget);
    }

    [Fact]
    public void The_input_carries_the_membership_the_platform_asserted()
    {
        var bot = new ChatMessage
        {
            ChannelKind = ChannelKind.Slack,
            EventId = "Ev02",
            WorkspaceId = "T01",
            ChannelId = "C01",
            AuthorId = "B01",
            BotId = "B01",
            IsExternal = false,
            Text = "hello",
            OccurredAt = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000),
        };

        var input = ChatInputFactory.From(bot);

        Assert.Equal("B01", input.Membership.AuthorId);
        Assert.Equal("B01", input.Membership.BotId);
        Assert.True(input.Membership.IsBot);
    }

    // ---------------------------------------------------------------------------------------------
    // The deterministic evidence producer
    // ---------------------------------------------------------------------------------------------

    private static IReadOnlyList<Evidence> Produce(string text)
    {
        var input = ChatInputFactory.From(Message(text));
        return ChatEvidenceProducer.Produce(input);
    }

    private static Evidence Signal(IReadOnlyList<Evidence> evidence, string signalId) =>
        Assert.Single(evidence, e => e.SignalId == signalId);

    [Fact]
    public void A_label_pointing_somewhere_other_than_it_says_is_reported_as_a_mismatch()
    {
        var evidence = Produce("urgent: <http://paypal.com.evil.example|paypal.com>");

        var mismatch = Signal(evidence, "deterministic.link_display_mismatch");
        Assert.Equal(EvidenceAvailability.Available, mismatch.Availability);

        // A ratio, not a count: one mismatch among one labelled link and one among fifty are not the
        // same claim, and the value has to say which it is.
        Assert.Equal(1.0, mismatch.Value);
    }

    [Fact]
    public void A_message_with_no_links_says_the_signals_do_not_apply_rather_than_zero()
    {
        // NotApplicable is a statement that we looked and the question does not arise, which is not
        // the same as a zero. Collapsing them would make "no links" indistinguishable from "links
        // that are all fine", and the second is a claim the analysis has actually earned.
        var evidence = Produce("just a normal message");

        Assert.Equal(EvidenceAvailability.NotApplicable, Signal(evidence, "deterministic.link_idn").Availability);
        Assert.Equal(
            EvidenceAvailability.NotApplicable,
            Signal(evidence, "deterministic.link_display_mismatch").Availability);
    }

    [Fact]
    public void A_homograph_host_is_reported_with_what_it_appears_to_say()
    {
        var evidence = Produce("invoice <http://xn--pypal-4ve.com/login|paypal.com>");

        var homograph = Signal(evidence, "deterministic.link_idn_homograph");
        Assert.Equal(EvidenceAvailability.Available, homograph.Availability);
        Assert.Equal(1, homograph.Value);

        // The skeleton is the whole point of the signal: it is what the host reads as to a person,
        // which is the claim the attacker is relying on.
        Assert.Contains(
            homograph.Attributes ?? [],
            a => a.Value.Contains("paypal.com", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_signal_is_deterministic_and_stamped_by_this_producer()
    {
        // The origin cannot be forgotten on one signal and quietly turn a reproducible fact into
        // something a policy might treat as a model's opinion.
        var evidence = Produce("<http://xn--pypal-4ve.com/x|paypal.com>");

        Assert.NotEmpty(evidence);
        Assert.All(evidence, e => Assert.Equal(EvidenceOrigin.Deterministic, e.Origin));
        Assert.All(evidence, e => Assert.Equal("stylomail-chat/1", e.SourceVersion));
        Assert.All(evidence, e => Assert.Null(e.Confidence));
    }

    [Fact]
    public void The_attributes_are_bounded_so_one_message_cannot_inflate_the_ledger()
    {
        // Bounded cardinality is a property of this system rather than tuning, and this runs over
        // text an outside party chose.
        var many = string.Join(
            ' ',
            Enumerable.Range(0, 200).Select(i => $"<http://xn--pypal-4ve.com/{i}|paypal.com>"));

        var evidence = Produce(many);

        var homograph = Signal(evidence, "deterministic.link_idn_homograph");

        // The count is the real finding and is not capped; only the examples carried alongside it are,
        // so the signal stays bounded without the number going quiet about the volume.
        Assert.Equal(200, homograph.Value);
        Assert.True((homograph.Attributes ?? []).Count <= 8, "attribute list must be capped");

        // A value is either within the bound or visibly marked as having been cut. The shared builder
        // appends the marker rather than replacing a character, so a truncated value is one longer
        // than the bound, and asserting a bare length would fail on exactly the case this allows.
        Assert.All(
            homograph.Attributes ?? [],
            a => Assert.True(
                a.Value.Length <= 128 || a.Value.EndsWith('…'),
                $"attribute value is neither bounded nor marked as cut ({a.Value.Length} chars)"));
    }
}
