using StyloMail.Assessment.Campaign;
using StyloMail.Assessment.Triage;
using StyloMail.Chat;
using StyloMail.Chat.Slack;
using StyloMail.Core;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// The algorithmic layer: what decides a message is not worth the pipeline, and what it admits.
/// </summary>
/// <remarks>
/// Written from `docs/chat-channels-plan-03-triage.md` rather than from the implementation, so a test
/// and the record can disagree. The record's rule is that every check is specified by which way it
/// fails, and these pin the two properties overview- said he would audit first: that a dismissal is
/// visible, and that nothing is silently unrecorded.
/// </remarks>
public sealed class TriageTests
{
    private static ChatAnalysisInput Input(
        string text = "hello",
        string channelId = "C01",
        bool isExternal = false,
        string eventId = "Ev01") =>
        ChatInputFactory.From(new ChatMessage
        {
            ChannelKind = ChannelKind.Slack,
            EventId = eventId,
            WorkspaceId = "T01",
            ChannelId = channelId,
            AuthorId = "U01",
            IsExternal = isExternal,
            Conversation = SlackConversationType.Channel,
            Text = text,
            OccurredAt = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000),
        });

    private static TriageContext Context(params string[] watchedChannels) =>
        TriageContext.For(watchedChannels);

    [Fact]
    public void A_message_dismissed_on_scope_does_not_read_as_having_passed_the_later_checks()
    {
        // "A message dismissed at check 1 must not read as one that passed checks 2 through 4." That
        // is the rule the whole system is built on, applied to triage's own output rather than to its
        // input: absence of a finding is a distinct state from a check that looked and found nothing.
        var outcome = TriageEngine.Evaluate(Input(channelId: "C99"), Context("C01"));

        Assert.Equal(TriageDisposition.Dismiss, outcome.Disposition);
        Assert.Equal(TriageCheck.Scope, outcome.DecidedBy);

        // The checks behind the one that settled it are named as not having run, rather than being
        // absent from the evidence and left for a reader to infer.
        Assert.Contains(TriageCheck.NearDuplicate, outcome.NotRun);
        Assert.Contains(TriageCheck.Links, outcome.NotRun);
        Assert.Contains(TriageCheck.Behaviour, outcome.NotRun);
    }

    private static TriageContext WithCampaign(string channel, CampaignNearDuplicateDetector campaign) =>
        TriageContext.For(channel) with { Campaign = campaign };

    private static CampaignNearDuplicateDetector Campaign() =>
        new(new RecentCampaignWindow(8, TimeSpan.FromHours(1)));

    [Fact]
    public void The_same_words_a_second_time_are_a_duplicate_and_are_dismissed()
    {
        // Cutting channel noise is job three, and the shape of that noise is the same words
        // broadcast again rather than the same links.
        var campaign = Campaign();
        TriageEngine.Evaluate(
            Input("did the deploy finish?", eventId: "Ev01"), WithCampaign("C01", campaign));

        var outcome = TriageEngine.Evaluate(
            Input("did the deploy finish?", eventId: "Ev02"), WithCampaign("C01", campaign));

        Assert.Equal(TriageDisposition.Dismiss, outcome.Disposition);
        Assert.Equal(TriageCheck.NearDuplicate, outcome.DecidedBy);
    }

    [Fact]
    public void The_same_words_pointing_somewhere_new_escalate_by_construction()
    {
        // The fifty-first message: identical words, one destination changed. It must escalate, and it
        // must do so because the fingerprints differ rather than because a threshold happened to
        // catch it. That is the whole reason the basis is text AND components.
        var campaign = Campaign();
        TriageEngine.Evaluate(
            Input("urgent: <http://example.com/pay|paypal.com>", eventId: "Ev01"), WithCampaign("C01", campaign));

        var outcome = TriageEngine.Evaluate(
            Input("urgent: <http://paypal.com.evil.example|paypal.com>", eventId: "Ev02"), WithCampaign("C01", campaign));

        Assert.NotEqual(TriageDisposition.Dismiss, outcome.Disposition);
        Assert.NotEqual(TriageCheck.NearDuplicate, outcome.DecidedBy);
    }

    [Fact]
    public void A_message_with_nothing_normalisable_agrees_with_nothing()
    {
        // An image with no caption is not a duplicate of every other image with no caption. The same
        // rule as a fingerprint over nothing: an empty basis is not agreement.
        var campaign = Campaign();
        TriageEngine.Evaluate(Input(string.Empty, eventId: "Ev01"), WithCampaign("C01", campaign));

        var outcome = TriageEngine.Evaluate(Input(string.Empty, eventId: "Ev02"), WithCampaign("C01", campaign));

        Assert.NotEqual(TriageCheck.NearDuplicate, outcome.DecidedBy);
    }

    [Fact]
    public void A_channel_this_deployment_watches_is_not_dismissed_on_scope()
    {
        // Closes the other direction, so the test above cannot pass because scope dismisses
        // everything.
        var outcome = TriageEngine.Evaluate(Input(channelId: "C01"), Context("C01"));

        Assert.NotEqual(TriageCheck.Scope, outcome.DecidedBy);
    }

    [Fact]
    public void A_deployment_watching_nothing_watches_nothing()
    {
        // Not a default: an empty configuration is a deployment that has not been set up, and
        // answering "watch everything" for it would make an unconfigured host the most permissive
        // one. The record says this check is configuration rather than judgement.
        var outcome = TriageEngine.Evaluate(Input(channelId: "C01"), Context());

        Assert.Equal(TriageDisposition.Dismiss, outcome.Disposition);
        Assert.Equal(TriageCheck.Scope, outcome.DecidedBy);
    }
}
