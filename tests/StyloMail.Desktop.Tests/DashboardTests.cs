using System.Net;
using System.Text;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The dashboard's cards, read from the routes the console actually calls.
/// </summary>
public sealed class DashboardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_dashboard_reads_the_flow_and_labels_its_basis()
    {
        var model = Model(new StubHttpMessageHandler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/overview/flow" => Json(FlowJson()),
            "/v1/queues/summary" => Json(QueueJson()),
            "/v1/storage/summary" => Json(StorageJson()),
            "/v1/decisions" => Json(DecisionsJson()),
            "/v1/proxy/connection-outcomes" => Json(ConnectionsJson()),
            "/v1/senders" => Json(SendersJson()),
            "/v1/proxy/conversations" => Json(ConversationsJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        await model.LoadAsync();

        Assert.Equal("ready", model.FlowState);
        Assert.Equal("Accepted in the last 24 hours, current state", model.FlowBasisLabel);
        Assert.NotNull(model.FlowChart);
        Assert.Contains("Inbound", model.FlowChart!.Lines.Select(line => line.Name));
        Assert.Contains("Outbound", model.FlowChart.Lines.Select(line => line.Name));
        Assert.Contains("Delivered", model.FlowChart.Lines.Select(line => line.Name));
        Assert.Contains("Held", model.FlowChart.Lines.Select(line => line.Name));
        Assert.Contains("whole window was counted", model.FlowNote, StringComparison.Ordinal);
        Assert.Contains("messages", model.FlowChart.Summary, StringComparison.Ordinal);

        Assert.Equal(4, model.QueueCounts.Count);
        Assert.Contains("not a sum", model.QueueRelationLabel, StringComparison.Ordinal);
        Assert.Equal("3", model.QueueCounts[0].CountLabel);

        Assert.Equal("ready", model.StorageState);
        Assert.Contains("Database", model.StorageHeadline, StringComparison.Ordinal);
        Assert.Contains("disk", model.StorageDetail, StringComparison.Ordinal);

        Assert.Contains(model.Warnings, warning => warning.Id == "review-waiting" && warning.IsWarning);
    }

    [Fact]
    public async Task A_series_the_host_did_not_produce_is_named_and_not_drawn()
    {
        var model = Model(new StubHttpMessageHandler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/overview/flow" => Json(FlowJson(notProduced: """[{"series":"inbound","reason":"direction_not_recorded"}]""", inbound: "null")),
            "/v1/queues/summary" => Json(QueueJson()),
            "/v1/storage/summary" => Json(StorageJson()),
            "/v1/decisions" => Json(DecisionsJson()),
            "/v1/proxy/connection-outcomes" => Json(ConnectionsJson()),
            "/v1/senders" => Json(SendersJson()),
            "/v1/proxy/conversations" => Json(ConversationsJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        await model.LoadAsync();

        Assert.NotNull(model.FlowChart);
        Assert.DoesNotContain("Inbound", model.FlowChart!.Lines.Select(line => line.Name));
        Assert.Contains("Outbound", model.FlowChart.Lines.Select(line => line.Name));
        Assert.Contains("Not produced: inbound", model.FlowNote, StringComparison.OrdinalIgnoreCase);

        // A produced zero is still a value: the outbound line is drawn through it.
        Assert.True(model.FlowChart.Lines.Single(line => line.Name == "Outbound").Points.All(point => point.Value == 0));
    }

    [Fact]
    public async Task A_missing_summary_route_is_pending_not_zero()
    {
        var model = Model(new StubHttpMessageHandler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/decisions" => Json(DecisionsJson()),
            "/v1/proxy/connection-outcomes" => Json(ConnectionsJson()),
            "/v1/senders" => Json(SendersJson()),
            "/v1/proxy/conversations" => Json(ConversationsJson()),
            "/v1/messages" => Json("""{ "tenantId": "t1", "state": "held", "messages": [], "nextCursor": null, "hasMore": false }"""),
            "/v1/policy/reviews" => Json("""{ "tenantId": "t1", "items": [], "nextCursor": null }"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        await model.LoadAsync();

        Assert.Equal("pending", model.FlowState);
        Assert.Null(model.FlowChart);
        Assert.Equal("pending", model.StorageState);
        Assert.Contains("Waiting", model.StorageHeadline, StringComparison.Ordinal);
        Assert.Equal("fallback", model.QueueState);
        Assert.Contains(model.Warnings, warning => warning.Id == "summary-pending" && warning.IsInfo);
        Assert.Contains("mail flow", model.Warnings.Single(warning => warning.Id == "summary-pending").NextAction, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/", model.Warnings.Single(warning => warning.Id == "summary-pending").NextAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_window_with_no_messages_renders_one_empty_state()
    {
        var model = Model(new StubHttpMessageHandler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/overview/flow" => Json(EmptyFlowJson()),
            "/v1/queues/summary" => Json(QueueJson()),
            "/v1/storage/summary" => Json(StorageJson()),
            "/v1/decisions" => Json(DecisionsJson()),
            "/v1/proxy/connection-outcomes" => Json(ConnectionsJson()),
            "/v1/senders" => Json(SendersJson()),
            "/v1/proxy/conversations" => Json(ConversationsJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        await model.LoadAsync();

        Assert.Equal("empty", model.FlowState);
        Assert.Null(model.FlowChart);
        Assert.Equal("No messages in this window.", model.FlowNote);
        Assert.NotEmpty(model.FlowWindowLabel);
        Assert.NotEmpty(model.FlowAsOfLabel);
    }

    [Fact]
    public async Task The_service_card_carries_specific_facts_not_the_page_headline()
    {
        var model = Model(new StubHttpMessageHandler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/overview/flow" => Json(FlowJson()),
            "/v1/queues/summary" => Json(QueueJson()),
            "/v1/storage/summary" => Json(StorageJson()),
            "/v1/decisions" => Json(DecisionsJson()),
            "/v1/proxy/connection-outcomes" => Json(ConnectionsJson()),
            "/v1/senders" => Json(SendersJson()),
            "/v1/proxy/conversations" => Json(ConversationsJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        model.SetFeedStatus("Live");
        await model.LoadAsync();

        Assert.Equal("Mail is flowing normally.", model.ServiceHeadline);
        Assert.Equal("Accepting mail: yes.", model.ServiceAcceptingLabel);
        Assert.Equal("Live feed: connected.", model.ServiceFeedLabel);
    }

    [Fact]
    public async Task A_truncated_spool_is_a_lower_bound_and_an_unreadable_table_is_not_zero()
    {
        var model = Model(new StubHttpMessageHandler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/overview/flow" => Json(EmptyFlowJson()),
            "/v1/queues/summary" => Json(QueueJson()),
            "/v1/storage/summary" => Json(TruncatedStorageJson()),
            "/v1/decisions" => Json(DecisionsJson()),
            "/v1/proxy/connection-outcomes" => Json(ConnectionsJson()),
            "/v1/senders" => Json(SendersJson()),
            "/v1/proxy/conversations" => Json(ConversationsJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        await model.LoadAsync();

        Assert.True(
            model.StorageHeadline.Contains("at least", StringComparison.OrdinalIgnoreCase),
            model.StorageState + " || " + model.StorageHeadline + " || " + model.StorageDetail);
        Assert.Contains("could not be read", model.StorageDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not measured", model.StorageDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_count_axis_uses_whole_numbers()
    {
        var start = new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);

        var chart = new TimeChart
        {
            Title = "Test",
            Unit = "messages",
            WindowLabel = "Last 24 hours",
            CoverageLabel = "Complete.",
            Lines = [new ChartLine("Series", [new ChartPoint(start, 1)])],
        };

        Assert.Equal(2, chart.Maximum);
        Assert.Equal("2", chart.YMaximumLabel);
        Assert.Equal("1", chart.YMiddleLabel);
    }

    [Fact]
    public void The_sidebar_opens_on_the_dashboard_and_leaves_it_for_a_queue()
    {
        var model = ShellModel.CreateDefault();

        Assert.True(model.IsDashboardSelected);
        Assert.Equal("Dashboard", model.SelectedItem?.Title);

        var queue = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Queue == MessageListState.Review);

        model.SelectedItem = queue;

        Assert.False(model.IsDashboardSelected);
        Assert.Equal("Awaiting human review", queue.Title);
    }

    [Fact]
    public void A_null_is_a_gap_and_a_zero_is_a_value()
    {
        var start = new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);

        var chart = new TimeChart
        {
            Title = "Test",
            Unit = "messages",
            WindowLabel = "Last 24 hours",
            CoverageLabel = "Complete.",
            Lines = [new ChartLine("Series", [new ChartPoint(start, 0), new ChartPoint(start.AddHours(1), null), new ChartPoint(start.AddHours(2), 2)])],
        };

        Assert.True(chart.HasData);
        Assert.Equal(2, chart.Maximum);
        Assert.Contains("2 messages", chart.Summary, StringComparison.Ordinal);

        var empty = new TimeChart
        {
            Title = "Test",
            Unit = "messages",
            WindowLabel = "Last 24 hours",
            CoverageLabel = "Complete.",
            Lines = [new ChartLine("Series", [new ChartPoint(start, null)])],
        };

        Assert.False(empty.HasData);
        Assert.Contains("No data for this window", empty.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_critical_warning_cannot_be_dismissed_and_an_info_warning_can()
    {
        var model = Model(new StubHttpMessageHandler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "not_ready", "failedChecks": ["assessor_unavailable"] }""", HttpStatusCode.ServiceUnavailable),
            "/v1/decisions" => Json(DecisionsJson()),
            "/v1/proxy/connection-outcomes" => Json(ConnectionsJson()),
            "/v1/senders" => Json(SendersJson()),
            "/v1/proxy/conversations" => Json(ConversationsJson()),
            "/v1/messages" => Json("""{ "tenantId": "t1", "state": "held", "messages": [], "nextCursor": null, "hasMore": false }"""),
            "/v1/policy/reviews" => Json("""{ "tenantId": "t1", "items": [], "nextCursor": null }"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

        await model.LoadAsync();

        var critical = model.Warnings.Single(warning => warning.Id == "service-critical");
        Assert.False(critical.CanDismiss);

        var countBefore = model.Warnings.Count;
        model.ActivateWarning(critical);
        Assert.Equal(countBefore, model.Warnings.Count);

        var info = model.Warnings.Single(warning => warning.Id == "summary-pending");
        Assert.True(info.CanDismiss);
        model.Dismiss(info);
        Assert.DoesNotContain(model.Warnings, warning => warning.Id == "summary-pending");
    }

    private static DashboardViewModel Model(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5271") };
        var keys = new TestApiKeyProvider();
        return new DashboardViewModel(
            new StyloMailApiClient(http, keys),
            new ProxyConversationClient(http, keys),
            () => Now);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string EmptyFlowJson()
        => $$"""
        {
          "tenantId": "t1",
          "window": "last_24_hours",
          "windowStart": "2026-10-18T10:00:00+00:00",
          "windowEnd": "2026-10-19T10:00:00+00:00",
          "bucketMinutes": 60,
          "availability": "available",
          "asOf": "2026-10-19T10:00:00+00:00",
          "basis": "accepted_in_window_current_disposition",
          "rejectedBasis": "rejected_decisions_in_bucket",
          "notProduced": [],
          "coverage": { "messagesCounted": 0, "messagesSkipped": 0, "complete": true, "directionNotRecoverable": 0, "note": null },
          "totals": { "inbound": 0, "outbound": 0, "held": 0, "delivered": 0, "quarantined": 0, "review": 0, "other": 0, "rejected": 0 },
          "buckets": [
            { "bucketStart": "2026-10-18T10:00:00+00:00", "inbound": 0, "outbound": 0, "held": 0, "delivered": 0, "quarantined": 0, "review": 0, "other": 0, "rejected": 0 }
          ]
        }
        """;

    private static string FlowJson(string notProduced = "[]", string inbound = "600")
        => $$"""
        {
          "tenantId": "t1",
          "window": "last_24_hours",
          "windowStart": "2026-10-18T10:00:00+00:00",
          "windowEnd": "2026-10-19T10:00:00+00:00",
          "bucketMinutes": 60,
          "availability": "available",
          "asOf": "2026-10-19T10:00:00+00:00",
          "basis": "accepted_in_window_current_disposition",
          "rejectedBasis": "rejected_decisions_in_bucket",
          "notProduced": {{notProduced}},
          "coverage": { "messagesCounted": 1284, "messagesSkipped": 0, "complete": true, "directionNotRecoverable": 2, "note": null },
          "totals": { "inbound": 1200, "outbound": 84, "held": 3, "delivered": 1180, "quarantined": 2, "review": 2, "other": 0, "rejected": 1 },
          "buckets": [
            { "bucketStart": "2026-10-18T10:00:00+00:00", "inbound": {{inbound}}, "outbound": 0, "held": 1, "delivered": 0, "quarantined": 1, "review": 1, "other": 0, "rejected": 0 },
            { "bucketStart": "2026-10-18T11:00:00+00:00", "inbound": {{inbound}}, "outbound": 0, "held": 2, "delivered": 0, "quarantined": 1, "review": 1, "other": 0, "rejected": 1 }
          ]
        }
        """;

    private static string QueueJson()
        => """
        {
          "tenantId": "t1",
          "asOf": "2026-10-19T10:00:00+00:00",
          "availability": "available",
          "basis": "current_item_state",
          "overlapping": true,
          "counts": { "awaitingDecision": 3, "held": 0, "quarantined": 2, "review": 2 },
          "coverage": { "complete": true, "note": null }
        }
        """;

    private static string StorageJson()
        => """
        {
          "tenantId": "t1",
          "asOf": "2026-10-19T10:00:00+00:00",
          "availability": "available",
          "databaseBytes": 1073741824,
          "writeAheadLogBytes": 1048576,
          "spoolBytes": 12582912,
          "payloadBytes": 83886080,
          "capacityBytes": 10737418240,
          "volumeFreeBytes": 5368709120,
          "retentionHorizon": "2026-11-18T10:00:00+00:00",
          "retentionReason": null,
          "capacityScope": "filesystem_volume",
          "spoolFileCount": 4,
          "rowCounts": [ { "table": "queue_item", "count": 12000, "truncated": true, "reason": "capped" } ],
          "coverage": { "measured": ["database", "spool", "payloads"], "note": null }
        }
        """;

    private static string TruncatedStorageJson()
        => """
        {
          "tenantId": "t1",
          "asOf": "2026-10-19T10:00:00+00:00",
          "availability": "partial",
          "databaseBytes": 1073741824,
          "writeAheadLogBytes": 1048576,
          "spoolBytes": 12582912,
          "spoolTruncated": true,
          "spoolFileCount": 5,
          "payloadBytes": 83886080,
          "capacityBytes": null,
          "volumeFreeBytes": null,
          "capacityScope": "filesystem_volume",
          "retentionHorizon": null,
          "retentionReason": "retention_horizon_not_recorded",
          "rowCounts": [
            { "table": "queue_item", "count": null, "truncated": false, "reason": "table_unreadable" },
            { "table": "decision_ledger", "count": 0, "truncated": false, "reason": null }
          ],
          "coverage": { "measured": ["database", "spool", "payloads"], "note": null },
          "notProduced": [ { "series": "queue_item", "reason": "table_unreadable" } ]
        }
        """;

    private static string DecisionsJson()
        => """
        {
          "tenantId": "t1",
          "action": null,
          "decisions": [
            {
              "assessmentId": "a1",
              "internalMessageId": "m1",
              "action": "Allow",
              "proposedActionInShadow": null,
              "riskIndex": 0.1,
              "reasons": [],
              "versions": { "policyVersion": "1", "classifierModelVersion": null, "questionSchemaVersion": "1", "preprocessingVersion": "1", "regimeId": null },
              "coverage": { "bodyParsed": true, "htmlPresent": false, "hasAttachments": false, "htmlTextDisagreement": false, "parserLimitExceeded": false, "contentEncrypted": false, "truncated": false, "conversationContextMissing": false },
              "assessedAt": "2026-10-19T09:00:00+00:00"
            }
          ],
          "skippedCount": 0,
          "nextCursor": null,
          "hasMore": false
        }
        """;

    private static string ConnectionsJson()
        => """
        {
          "tenantId": "t1",
          "from": "2026-10-18T10:00:00+00:00",
          "through": "2026-10-19T10:00:00+00:00",
          "buckets": [
            { "protocol": "Imap", "outcome": "AdmissionRejected", "reason": "GlobalSessionLimitReached", "bucketStart": "2026-10-19T09:30:00+00:00", "count": 2 }
          ]
        }
        """;

    private static string SendersJson()
        => """
        {
          "tenantId": "t1",
          "senders": [
            { "principalId": "p1", "control": { "paused": false }, "label": "Billing", "source": "store", "companyId": null }
          ]
        }
        """;

    private static string ConversationsJson()
        => """
        {
          "tenantId": "t1",
          "window": "last_24_hours",
          "windowStart": "2026-10-18T10:00:00+00:00",
          "windowEnd": "2026-10-19T10:00:00+00:00",
          "availability": "available",
          "conversations": [
            { "threadToken": "abcdef123456", "accountId": "a1", "participantTokens": ["p1", "p2"], "referenceDepth": 1, "lastActivityAt": "2026-10-19T09:00:00+00:00", "availability": "available" }
          ],
          "nextCursor": null,
          "hasMore": false
        }
        """;
}
