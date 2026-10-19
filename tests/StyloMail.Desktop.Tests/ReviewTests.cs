using System.Net;
using System.Text;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Models;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Tests;

public sealed class ReviewTests
{
    [Fact]
    public async Task The_review_page_shows_the_opening_evidence_and_other_attention()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/policy/reviews" => Json(ReviewsJson()),
            "/v1/queues/summary" => Json(QueuesJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        var card = Assert.Single(model.Reviews);
        Assert.Contains("alice@example.test", card.Title, StringComparison.Ordinal);
        Assert.Equal("Suspicious payment change.", card.Reason);
        Assert.Contains("pack v1", card.PolicyLine, StringComparison.Ordinal);
        Assert.Contains("rule rule v2", card.PolicyLine, StringComparison.Ordinal);
        Assert.True(card.HasOverdue);
        Assert.Equal("1", model.HeldCountLabel);
        Assert.Equal("1", model.StoppedCountLabel);
        Assert.Contains("1 message is awaiting human review", model.StatusLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_review_queue_says_so()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/policy/reviews" => Json("""{ "tenantId": "t1", "items": [], "nextCursor": null }"""),
            "/v1/queues/summary" => Json(QueuesJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        Assert.Empty(model.Reviews);
        Assert.True(model.ShowEmpty);
        Assert.Equal("No messages are awaiting human review.", model.StatusLabel);
    }

    [Fact]
    public async Task Release_posts_the_review_route_with_the_reason()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/policy/reviews/q1/release" => Json("""{ "item": { "queueId": "q1", "internalMessageId": "m1" } }"""),
            "/v1/policy/reviews" => Json(ReviewsJson()),
            "/v1/queues/summary" => Json(QueuesJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        model.AskRelease(Assert.Single(model.Reviews));
        model.PendingReason = "harness review";
        await model.ConfirmAsync();

        var request = Assert.Single(handler.Requests, item => item.Path == "/v1/policy/reviews/q1/release");
        Assert.Contains("harness review", request.Body ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Applied", model.LastResult ?? string.Empty, StringComparison.Ordinal);
        Assert.False(model.LastFailed);
    }

    [Fact]
    public async Task A_failed_review_action_is_a_result_not_an_exception()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/policy/reviews/q1/reject" => Json(
                """{ "error": "forbidden", "detail": "The administer privilege is required." }""",
                HttpStatusCode.Forbidden),
            "/v1/policy/reviews" => Json(ReviewsJson()),
            "/v1/queues/summary" => Json(QueuesJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();
        model.AskReject(Assert.Single(model.Reviews));
        model.PendingReason = "harness review";
        await model.ConfirmAsync();

        Assert.True(model.LastFailed);
        Assert.Contains("Could not apply", model.LastResult ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(model.PendingAction);
    }

    [Fact]
    public void The_review_destination_opens_the_review_page()
    {
        var model = ShellModel.CreateDefault();
        var review = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Review queue");

        model.SelectedItem = review;

        Assert.True(model.IsReviewSelected);
        Assert.False(model.ShowQueuePanes);
    }

    private static ReviewViewModel Model(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5271") };
        return new ReviewViewModel(new StyloMailApiClient(http, new TestApiKeyProvider()));
    }

    private static StubHttpMessageHandler Handler(Func<CapturedRequest, HttpResponseMessage> respond) => new(respond);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ReviewsJson()
        => """
        {
          "tenantId": "t1",
          "items": [
            {
              "queueId": "q1",
              "tenantId": "t1",
              "internalMessageId": "m1",
              "recipients": [
                { "recipient": "alice@example.test", "state": "Review", "reviewDeadline": "2026-10-18T09:00:00+00:00", "overdue": true, "attempts": 0, "lastError": null }
              ],
              "policy": null,
              "opening": {
                "assessmentId": "a1",
                "source": "Pack",
                "packId": "pack",
                "packVersion": "1",
                "ruleSetId": "rs",
                "ruleSetVersion": "1",
                "ruleId": "rule",
                "ruleVersion": 2,
                "reasonCode": "payment_change",
                "reason": "Suspicious payment change.",
                "unavailableReason": null,
                "decidedAt": "2026-10-18T09:00:00+00:00"
              },
              "policyRecordMissing": false,
              "legacyReviewOpeningMissing": false,
              "createdAt": "2026-10-18T09:00:00+00:00",
              "updatedAt": "2026-10-18T09:00:00+00:00"
            }
          ],
          "nextCursor": null
        }
        """;

    private static string QueuesJson()
        => """
        {
          "tenantId": "t1",
          "asOf": "2026-10-19T10:00:00+00:00",
          "availability": "available",
          "basis": "current_attention_recipient_state",
          "overlapping": true,
          "counts": { "awaitingDecision": 2, "held": 1, "quarantined": 1, "review": 1 },
          "coverage": { "complete": true, "note": null }
        }
        """;
}
