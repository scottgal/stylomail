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
        bool isExternal = false) =>
        ChatInputFactory.From(new ChatMessage
        {
            ChannelKind = ChannelKind.Slack,
            EventId = "Ev01",
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
