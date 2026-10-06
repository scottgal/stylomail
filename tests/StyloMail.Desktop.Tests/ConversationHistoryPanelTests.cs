using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Tests;

public sealed class ConversationHistoryPanelTests
{
    [Fact]
    public void A_page_keeps_each_assessment_and_its_observations_separate()
    {
        var decision = Json.Read<DecisionResponse>(Wire.Decision) with
        {
            Channel = new ChannelContext
            {
                Kind = ChannelKind.Slack,
                WorkspaceId = "workspace-1",
                ChannelId = "channel-2",
                ThreadId = "thread-3",
            },
            AssessedAt = new DateTimeOffset(2026, 10, 6, 12, 30, 0, TimeSpan.Zero),
            Evidence =
            [
                new EvidenceResponse
                {
                    SignalId = "behavioural.trend.velocity",
                    Origin = EvidenceOrigin.Behavioural,
                    Availability = EvidenceAvailability.Unavailable,
                    Value = 0,
                    Confidence = null,
                    SampleSupport = 0,
                    SourceVersion = "behaviour-v1",
                    ObservedScope = "OutboundSender",
                    Window = "burst",
                    ObservedAt = new DateTimeOffset(2026, 10, 6, 12, 29, 0, TimeSpan.Zero),
                    AvailabilityReasons = ["no history"],
                },
            ],
        };
        var panel = ConversationHistoryPanel.For(decision);
        var response = new ConversationHistoryResponse
        {
            TenantId = "tenant-1",
            WorkspaceId = "workspace-1",
            ChannelId = "channel-2",
            ThreadId = "thread-3",
            From = null,
            To = null,
            Decisions = [decision],
            NextCursor = "cursor-next",
            HasMore = true,
            SkippedCount = 1,
        };

        panel.BeginLoad(append: false);
        panel.ApplyPage(response, append: false);

        var row = Assert.Single(panel.Decisions);
        var observation = Assert.Single(row.TrendObservations);
        Assert.Equal("Assessed at 2026-10-06 12:30:00 +00:00", row.AssessedAtLabel);
        Assert.Equal("Quarantine", row.ActionLabel);
        Assert.Equal("msg_9c1b7e", row.InternalMessageId);
        Assert.Equal("OutboundSender · burst", observation.ScopeLabel);
        Assert.Equal("Unavailable", observation.AvailabilityLabel);
        Assert.Equal("not produced", observation.ValueLabel);
        Assert.Equal("0 samples", observation.SampleSupportLabel);
        Assert.Equal("Observed at 2026-10-06 12:29:00 +00:00", observation.ObservedAtLabel);
        Assert.Equal("cursor-next", panel.NextCursor);
        Assert.True(panel.ShowLoadMoreButton);
        Assert.Contains("skipped 1 record", panel.SkippedLabel!);
    }

    [Fact]
    public void A_page_for_another_conversation_is_rejected_without_showing_its_rows()
    {
        var decision = Json.Read<DecisionResponse>(Wire.Decision) with
        {
            Channel = new ChannelContext
            {
                Kind = ChannelKind.Slack,
                WorkspaceId = "workspace-1",
                ChannelId = "channel-2",
            },
        };
        var panel = ConversationHistoryPanel.For(decision);
        var response = new ConversationHistoryResponse
        {
            TenantId = "tenant-1",
            WorkspaceId = "workspace-other",
            ChannelId = "channel-2",
            ThreadId = null,
            From = null,
            To = null,
            Decisions = [decision],
            NextCursor = null,
            HasMore = false,
            SkippedCount = 0,
        };

        panel.BeginLoad(append: false);
        panel.ApplyPage(response, append: false);

        Assert.Empty(panel.Decisions);
        Assert.Contains("different conversation", panel.StatusLabel);
        Assert.False(panel.HasLoaded);
        Assert.True(panel.ShowInitialLoadButton);
    }

    [Fact]
    public void A_page_claiming_more_without_a_cursor_is_not_presented_as_complete()
    {
        var decision = Json.Read<DecisionResponse>(Wire.Decision) with
        {
            Channel = new ChannelContext
            {
                Kind = ChannelKind.Slack,
                WorkspaceId = "workspace-1",
                ChannelId = "channel-2",
            },
        };
        var panel = ConversationHistoryPanel.For(decision);
        var response = new ConversationHistoryResponse
        {
            TenantId = "tenant-1",
            WorkspaceId = "workspace-1",
            ChannelId = "channel-2",
            ThreadId = null,
            From = null,
            To = null,
            Decisions = [decision],
            NextCursor = null,
            HasMore = true,
            SkippedCount = 0,
        };

        panel.BeginLoad(append: false);
        panel.ApplyPage(response, append: false);

        Assert.Empty(panel.Decisions);
        Assert.Contains("without a continuation cursor", panel.StatusLabel);
        Assert.True(panel.ShowInitialLoadButton);
    }

    [Fact]
    public void A_decision_whose_channel_keys_do_not_match_the_page_is_not_shown()
    {
        var selected = Json.Read<DecisionResponse>(Wire.Decision) with
        {
            Channel = new ChannelContext
            {
                Kind = ChannelKind.Slack,
                WorkspaceId = "workspace-1",
                ChannelId = "channel-2",
            },
        };
        var wrongChannel = Json.Read<DecisionResponse>(Wire.Decision);
        var panel = ConversationHistoryPanel.For(selected);
        var response = new ConversationHistoryResponse
        {
            TenantId = "tenant-1",
            WorkspaceId = "workspace-1",
            ChannelId = "channel-2",
            ThreadId = null,
            From = null,
            To = null,
            Decisions = [wrongChannel],
            NextCursor = null,
            HasMore = false,
            SkippedCount = 0,
        };

        panel.BeginLoad(append: false);
        panel.ApplyPage(response, append: false);

        Assert.Empty(panel.Decisions);
        Assert.Contains("without matching Slack conversation keys", panel.StatusLabel);
    }

    [Fact]
    public void A_next_page_appends_original_assessments_and_uses_the_returned_cursor()
    {
        var first = SlackDecision("asm-first");
        var second = SlackDecision("asm-second");
        var panel = ConversationHistoryPanel.For(first);
        panel.BeginLoad(append: false);
        panel.ApplyPage(Page(first, "cursor-2", hasMore: true), append: false);

        panel.BeginLoad(append: true);
        Assert.Equal("cursor-2", panel.NextCursor);
        panel.ApplyPage(Page(second, nextCursor: null, hasMore: false), append: true);

        Assert.Equal(["asm-first", "asm-second"], panel.Decisions.Select(row => row.AssessmentId));
        Assert.Null(panel.NextCursor);
        Assert.False(panel.ShowLoadMoreButton);
    }

    [Fact]
    public void A_failed_older_page_keeps_current_records_and_the_retry_cursor()
    {
        var first = SlackDecision("asm-first");
        var panel = ConversationHistoryPanel.For(first);
        panel.BeginLoad(append: false);
        panel.ApplyPage(Page(first, "cursor-2", hasMore: true), append: false);

        panel.BeginLoad(append: true);
        panel.Fail("History request failed.", preservePageState: true);

        Assert.Single(panel.Decisions);
        Assert.Equal("cursor-2", panel.NextCursor);
        Assert.True(panel.ShowLoadMoreButton);
        Assert.Equal("History request failed.", panel.StatusLabel);
    }

    [Fact]
    public void A_slack_channel_without_a_thread_key_is_labelled_as_all_threads()
    {
        var decision = Json.Read<DecisionResponse>(Wire.Decision) with
        {
            Channel = new ChannelContext
            {
                Kind = ChannelKind.Slack,
                WorkspaceId = "workspace-1",
                ChannelId = "channel-2",
                ThreadId = null,
            },
        };

        var panel = ConversationHistoryPanel.For(decision);

        Assert.Equal("Slack · workspace workspace-1 · channel channel-2 · all threads", panel.ScopeLabel);
    }

    private static DecisionResponse SlackDecision(string id)
        => Json.Read<DecisionResponse>(Wire.Decision) with
        {
            AssessmentId = id,
            Channel = new ChannelContext
            {
                Kind = ChannelKind.Slack,
                WorkspaceId = "workspace-1",
                ChannelId = "channel-2",
                ThreadId = "thread-3",
            },
        };

    private static ConversationHistoryResponse Page(
        DecisionResponse decision,
        string? nextCursor,
        bool hasMore)
        => new()
        {
            TenantId = "tenant-1",
            WorkspaceId = "workspace-1",
            ChannelId = "channel-2",
            ThreadId = "thread-3",
            From = null,
            To = null,
            Decisions = [decision],
            NextCursor = nextCursor,
            HasMore = hasMore,
            SkippedCount = 0,
        };
}
