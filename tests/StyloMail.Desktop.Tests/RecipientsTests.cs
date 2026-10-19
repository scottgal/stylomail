using System.Net;
using System.Text;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The people directory over the Host's entity route.
/// </summary>
public sealed class RecipientsTests
{
    [Fact]
    public async Task The_directory_shows_a_label_or_an_opaque_fallback()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/entities" => Json(DirectoryJson("""
                [
                  {
                    "scopeId": "hash_7f3a9c2b1d4e",
                    "kind": "recipient",
                    "direction": "Inbound",
                    "displayLabel": null,
                    "displayLabelAvailability": "not_produced",
                    "displayLabelReason": "no_stored_label",
                    "companyId": null,
                    "companyName": null,
                    "domain": "example.test",
                    "lastContactAt": "2026-10-18T09:00:00+00:00",
                    "lastContactReason": null,
                    "volume": { "messages": 3, "inbound": 3, "outbound": 0, "availability": "available", "basis": "lifetime_profile_observations", "reason": null },
                    "evidence": {
                      "reply": { "availability": "not_produced", "label": null, "coverage": null, "reason": "directory_evidence_not_computed" },
                      "pressure": { "availability": "not_produced", "label": null, "coverage": null, "reason": "directory_evidence_not_computed" },
                      "identity": { "availability": "not_produced", "label": null, "coverage": null, "reason": "directory_evidence_not_computed" }
                    }
                  }
                ]
                """)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        var row = Assert.Single(model.Rows);
        Assert.Equal("hash_7f3a9...", row.Title);
        Assert.DoesNotContain("@", row.Title, StringComparison.Ordinal);
        Assert.Contains("Writes to you", row.DirectionLabel, StringComparison.Ordinal);
        Assert.Contains("3 observations (lifetime)", row.VolumeLabel, StringComparison.Ordinal);
        Assert.Contains("Last contact", row.LastContactLabel, StringComparison.Ordinal);
        Assert.Contains("Open the profile", row.EvidenceLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_masked_label_is_rendered_as_served_and_not_decoded()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/entities" => Json(DirectoryJson("""
                [
                  { "scopeId": "hash_mask", "kind": "recipient", "direction": "Inbound", "displayLabel": "a***@example.test", "displayLabelAvailability": "available", "displayLabelReason": null, "displayLabelSource": "queue_envelope_masked", "companyId": null, "companyName": null, "domain": "example.test", "lastContactAt": null, "lastContactReason": "no_profile_recorded", "volume": null, "evidence": null }
                ]
                """)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        var row = Assert.Single(model.Rows);
        Assert.Equal("a***@example.test", row.Title);
        Assert.Null(row.LabelNote);
    }

    [Fact]
    public async Task The_direction_tabs_filter_the_host_page()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/entities" => Json(DirectoryJson("""
                [
                  { "scopeId": "in_1", "kind": "recipient", "direction": "Inbound", "displayLabel": "i***@example.test", "displayLabelAvailability": "available", "displayLabelReason": null, "displayLabelSource": "queue_envelope_masked", "companyId": null, "companyName": null, "domain": "example.test", "lastContactAt": null, "lastContactReason": "no_profile_recorded", "volume": null, "evidence": null },
                  { "scopeId": "out_1", "kind": "recipient", "direction": "Outbound", "displayLabel": "o***@example.test", "displayLabelAvailability": "available", "displayLabelReason": null, "displayLabelSource": "queue_envelope_masked", "companyId": null, "companyName": null, "domain": "example.test", "lastContactAt": null, "lastContactReason": "no_profile_recorded", "volume": null, "evidence": null }
                ]
                """)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();
        Assert.Equal("i***@example.test", Assert.Single(model.Rows).Title);

        await model.LoadKindAsync("recipient", "Outbound");
        Assert.Equal("o***@example.test", Assert.Single(model.Rows).Title);
        Assert.Contains("mixed-direction", model.CoverageLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_named_absence_explains_the_opaque_fallback()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/entities" => Json(DirectoryJson("""
                [
                  { "scopeId": "hash_absence_1", "kind": "recipient", "direction": "Inbound", "displayLabel": null, "displayLabelAvailability": "not_produced", "displayLabelReason": "address_not_in_bounded_scan", "displayLabelSource": null, "companyId": null, "companyName": null, "domain": null, "lastContactAt": null, "lastContactReason": "no_profile_recorded", "volume": null, "evidence": null }
                ]
                """)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        var row = Assert.Single(model.Rows);
        Assert.Equal("hash_absen...", row.Title);
        Assert.Contains("outside the recent envelope scan", row.LabelNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_with_no_match_says_so()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/entities" => Json(DirectoryJson("[]")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.SearchAsync();
        model.SearchText = "nobody";
        await model.SearchAsync();

        Assert.True(model.ShowEmpty);
        Assert.Contains("No match", model.EmptyLabel, StringComparison.Ordinal);
        Assert.Contains("nobody", model.EmptyLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_not_produced_directory_is_named()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/entities" => Json("""
                {
                  "tenantId": "t1",
                  "kind": "recipient",
                  "availability": "not_produced",
                  "entities": [],
                  "nextCursor": null,
                  "hasMore": false,
                  "coverage": { "complete": false, "scanned": null, "note": null },
                  "notProduced": [ { "series": "profiles", "reason": "no_profile_key" } ]
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        Assert.Empty(model.Rows);
        Assert.Contains("Not produced", model.StatusLabel, StringComparison.Ordinal);
        Assert.Contains("no_profile_key", model.StatusLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_next_page_echoes_the_opaque_cursor()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/entities" when request.Query?.Contains("after=cursor_next", StringComparison.Ordinal) == true =>
                Json(DirectoryJson("[]")),
            "/v1/entities" => Json(DirectoryJson("""
                [
                  { "scopeId": "hash_1", "kind": "recipient", "direction": "Inbound", "displayLabel": "A", "displayLabelAvailability": "available", "displayLabelReason": null, "companyId": null, "companyName": null, "domain": null, "lastContactAt": null, "lastContactReason": "no_profile_recorded", "volume": null, "evidence": null }
                ]
                """, nextCursor: "cursor_next")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        Assert.True(model.ShowLoadMore);
        await model.LoadAsync(append: true);

        Assert.Contains(handler.Requests, request =>
            request.Path == "/v1/entities" && request.Query?.Contains("after=cursor_next", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_paused_sender_is_marked_from_the_senders_listing()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/entities" => Json(DirectoryJson("""
                [
                  { "scopeId": "p1", "kind": "sender", "direction": "Outbound", "displayLabel": "Billing", "displayLabelAvailability": "available", "displayLabelReason": null, "companyId": null, "companyName": null, "domain": null, "lastContactAt": null, "lastContactReason": "no_profile_recorded", "volume": null, "evidence": null }
                ]
                """)),
            "/v1/senders" => Json("""
                {
                  "tenantId": "t1",
                  "senders": [ { "principalId": "p1", "control": { "paused": true, "reason": "harness" }, "label": "Billing", "source": "store", "companyId": null } ]
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadKindAsync("sender");

        var row = Assert.Single(model.Rows);
        Assert.True(row.IsPaused);
        Assert.True(row.PauseStateAvailable);
        Assert.Equal("Sending is paused.", row.WarningLabel);
    }

    [Fact]
    public async Task An_unread_pause_join_is_not_reported_as_active()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/entities" => Json(DirectoryJson("""
                [
                  { "scopeId": "p1", "kind": "sender", "direction": "Outbound", "displayLabel": "Billing", "displayLabelAvailability": "available", "displayLabelReason": null, "companyId": null, "companyName": null, "domain": null, "lastContactAt": null, "lastContactReason": "no_profile_recorded", "volume": null, "evidence": null }
                ]
                """)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadKindAsync("sender");

        var row = Assert.Single(model.Rows);
        Assert.False(row.PauseStateAvailable);
        Assert.Equal("Pause state could not be read.", row.WarningLabel);
    }

    [Fact]
    public void The_recipients_destination_opens_the_directory_not_the_queue_panes()
    {
        var model = ShellModel.CreateDefault();
        var recipients = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Recipients");

        model.SelectedItem = recipients;

        Assert.True(model.IsRecipientsSelected);
        Assert.False(model.ShowQueuePanes);

        model.SelectedItem = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Queue == MessageListState.Held);

        Assert.False(model.IsRecipientsSelected);
        Assert.True(model.ShowQueuePanes);
    }

    [Fact]
    public void Queue_badges_are_four_counts_and_a_null_is_a_question_mark()
    {
        var model = ShellModel.CreateDefault();

        model.ApplyQueueCounts(new QueueCounts
        {
            AwaitingDecision = 3,
            Held = 1,
            Quarantined = 0,
            Review = null,
        });

        var rows = model.Sections
            .SelectMany(section => section.Items)
            .Where(item => item.Queue is not null)
            .ToArray();

        Assert.Equal("3", rows.Single(item => item.Queue == MessageListState.AwaitingDecision).Badge);
        Assert.Equal("1", rows.Single(item => item.Queue == MessageListState.Held).Badge);
        Assert.Equal("0", rows.Single(item => item.Queue == MessageListState.Quarantined).Badge);
        Assert.Equal("?", rows.Single(item => item.Queue == MessageListState.Review).Badge);
    }

    private static RecipientsViewModel Model(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5271") };
        return new RecipientsViewModel(new StyloMailApiClient(http, new TestApiKeyProvider()));
    }

    private static StubHttpMessageHandler Handler(Func<CapturedRequest, HttpResponseMessage> respond)
        => new(respond);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string DirectoryJson(string entities, string? nextCursor = null)
        => $$"""
        {
          "tenantId": "t1",
          "kind": "recipient",
          "availability": "available",
          "entities": {{entities}},
          "nextCursor": {{(nextCursor is null ? "null" : $"\"{nextCursor}\"")}},
          "hasMore": {{(nextCursor is null ? "false" : "true")}},
          "coverage": { "complete": true, "scanned": 1, "note": null },
          "notProduced": []
        }
        """;
}
