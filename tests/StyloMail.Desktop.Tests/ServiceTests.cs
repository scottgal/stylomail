using System.Net;
using System.Text;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Models;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The Service page over the proxy management and summary routes.
/// </summary>
public sealed class ServiceTests
{
    [Fact]
    public async Task The_service_page_reads_the_management_surfaces()
    {
        var handler = Handler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/proxy/accounts" => Json(AccountsJson()),
            "/v1/proxy/sessions" => Json(SessionsJson()),
            "/v1/proxy/listeners" => Json(ListenersJson()),
            "/v1/proxy/operations" => Json(OperationsJson()),
            "/v1/queues/summary" => Json(QueuesJson()),
            "/v1/storage/summary" => Json(StorageJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        Assert.Equal("Mail is flowing normally.", model.ServiceHeadline);
        Assert.Equal("Accepting mail: yes.", model.AcceptingLabel);
        Assert.Single(model.Accounts);
        Assert.Equal("Enabled", model.Accounts[0].StateLabel);
        Assert.Single(model.Listeners);
        Assert.Equal("Listening", model.Listeners[0].StateLabel);
        Assert.Equal("Encrypted", model.Listeners[0].TlsLabel);
        Assert.Single(model.Sessions);
        Assert.Contains("opened 2", model.OperationsLabel, StringComparison.Ordinal);
        Assert.Contains("In the delivery queue 1", model.QueueLabel, StringComparison.Ordinal);
        Assert.Contains("Stored:", model.StorageLabel, StringComparison.Ordinal);
        Assert.Contains(model.TechnicalLines, line => line.StartsWith("Listener l1:", StringComparison.Ordinal));
        Assert.Contains(model.TechnicalLines, line => line.StartsWith("Routes read:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_paused_account_maps_to_paused_and_enable_posts_the_route()
    {
        var handler = Handler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/proxy/accounts" => Json(AccountsJson(enabled: false)),
            "/v1/proxy/sessions" => Json(SessionsJson(availability: "not_produced")),
            "/v1/proxy/listeners" => Json(ListenersJson()),
            "/v1/proxy/operations" => Json(OperationsJson()),
            "/v1/queues/summary" => Json(QueuesJson()),
            "/v1/storage/summary" => Json(StorageJson()),
            _ => request.Path.StartsWith("/v1/proxy/accounts/", StringComparison.Ordinal)
                ? Json(AccountJson())
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        var account = Assert.Single(model.Accounts);
        Assert.Equal("Paused", account.StateLabel);
        Assert.True(account.CanEnable);
        Assert.False(account.CanPause);

        model.AskEnableAccount(account);
        model.PendingReason = "harness run";
        await model.ConfirmAsync();

        Assert.Contains(handler.Requests, request => request.Path == "/v1/proxy/accounts/a1/enable");
        Assert.True(
            model.LastResult?.Contains("Applied", StringComparison.Ordinal) == true,
            "last=" + (model.LastResult ?? "<null>") + " failed=" + model.LastFailed + " status=" + model.StatusLabel);
    }

    [Fact]
    public async Task A_revoked_account_cannot_be_enabled()
    {
        var handler = Handler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/proxy/accounts" => Json(AccountsJson(revoked: true)),
            "/v1/proxy/sessions" => Json(SessionsJson(availability: "not_produced")),
            "/v1/proxy/listeners" => Json(ListenersJson()),
            "/v1/proxy/operations" => Json(OperationsJson()),
            "/v1/queues/summary" => Json(QueuesJson()),
            "/v1/storage/summary" => Json(StorageJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        var account = Assert.Single(model.Accounts);
        Assert.Equal("Revoked", account.StateLabel);
        Assert.False(account.CanEnable);
        Assert.False(account.CanRevoke);
    }

    [Fact]
    public async Task A_draining_listener_maps_to_draining()
    {
        var handler = Handler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/proxy/accounts" => Json(AccountsJson()),
            "/v1/proxy/sessions" => Json(SessionsJson()),
            "/v1/proxy/listeners" => Json(ListenersJson(draining: true)),
            "/v1/proxy/operations" => Json(OperationsJson()),
            "/v1/queues/summary" => Json(QueuesJson()),
            "/v1/storage/summary" => Json(StorageJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        var listener = Assert.Single(model.Listeners);
        Assert.Equal("Draining", listener.StateLabel);
        Assert.False(listener.CanDrain);
        Assert.Contains("finish", listener.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Not_produced_sessions_and_operations_are_named()
    {
        var handler = Handler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/proxy/accounts" => Json(AccountsJson()),
            "/v1/proxy/sessions" => Json(SessionsJson(availability: "not_produced")),
            "/v1/proxy/listeners" => Json(ListenersJson()),
            "/v1/proxy/operations" => Json(OperationsJson(availability: "not_produced")),
            "/v1/queues/summary" => Json(QueuesJson()),
            "/v1/storage/summary" => Json(StorageJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        Assert.Empty(model.Sessions);
        Assert.Contains("does not report live sessions", model.SessionsNote, StringComparison.Ordinal);
        Assert.Contains("not produced", model.OperationsLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_action_is_a_result_not_an_exception()
    {
        var handler = Handler(request => request.Path switch
        {
            "/health/ready" => Json("""{ "status": "ready" }"""),
            "/v1/proxy/accounts" => Json(AccountsJson(enabled: true)),
            "/v1/proxy/sessions" => Json(SessionsJson(availability: "not_produced")),
            "/v1/proxy/listeners" => Json(ListenersJson()),
            "/v1/proxy/operations" => Json(OperationsJson()),
            "/v1/queues/summary" => Json(QueuesJson()),
            "/v1/storage/summary" => Json(StorageJson()),
            "/v1/proxy/accounts/a1/disable" => Json(
                """{ "error": "listener_runtime_unavailable", "detail": "The listener runtime is unavailable." }""",
                HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        model.AskPauseAccount(Assert.Single(model.Accounts));
        model.PendingReason = "harness run";
        await model.ConfirmAsync();

        Assert.True(model.LastFailed);
        Assert.Contains("Could not apply", model.LastResult, StringComparison.Ordinal);
        Assert.Null(model.PendingAction);
    }

    [Fact]
    public void The_service_destination_opens_the_service_page()
    {
        var model = ShellModel.CreateDefault();
        var service = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Service");

        model.SelectedItem = service;

        Assert.True(model.IsServiceSelected);
        Assert.False(model.ShowQueuePanes);
    }

    private static ServiceViewModel Model(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5271") };
        return new ServiceViewModel(new StyloMailApiClient(http, new TestApiKeyProvider()));
    }

    private static StubHttpMessageHandler Handler(Func<CapturedRequest, HttpResponseMessage> respond)
        => new(respond);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string AccountJson()
        => """
        {
          "accountId": "a1",
          "loginKey": "lk1",
          "enabled": true,
          "providerId": "gmail",
          "credentialDiscriminator": "app_password",
          "enrollmentState": "Enrolled",
          "createdAt": "2026-10-18T10:00:00+00:00",
          "updatedAt": "2026-10-18T10:00:00+00:00",
          "revokedAt": null
        }
        """;

    private static string AccountsJson(bool enabled = true, bool revoked = false)
        => $$"""
        {
          "tenantId": "t1",
          "accounts": [
            {
              "accountId": "a1",
              "loginKey": "lk1",
              "enabled": {{(enabled ? "true" : "false")}},
              "providerId": "gmail",
              "credentialDiscriminator": "app_password",
              "enrollmentState": "Enrolled",
              "createdAt": "2026-10-18T10:00:00+00:00",
              "updatedAt": "2026-10-18T10:00:00+00:00",
              "revokedAt": {{(revoked ? "\"2026-10-18T11:00:00+00:00\"" : "null")}}
            }
          ],
          "nextCursor": null,
          "hasMore": false
        }
        """;

    private static string SessionsJson(string availability = "available")
        => $$"""
        {
          "tenantId": "t1",
          "scope": "live",
          "availability": "{{availability}}",
          "sessions": [
            {
              "sessionId": "s1",
              "accountId": "a1",
              "protocol": "Imap",
              "direction": "Retrieval",
              "startedAt": "2026-10-19T10:00:00+00:00",
              "authenticatedAt": "2026-10-19T10:00:01+00:00",
              "endedAt": null,
              "outcome": null,
              "clientBytes": null,
              "backendBytes": null,
              "boundReason": null
            }
          ],
          "nextCursor": null,
          "hasMore": false
        }
        """;

    private static string ListenersJson(bool draining = false)
        => $$"""
        {
          "tenantId": "t1",
          "listeners": [
            {
              "listenerId": "l1",
              "protocol": "Imap",
              "enabled": true,
              "state": "Listening",
              "tlsPolicy": "Required",
              "draining": {{(draining ? "true" : "false")}},
              "boundPort": 1143,
              "observedAt": "2026-10-19T10:00:00+00:00",
              "faultReason": null,
              "safeDetail": null
            }
          ]
        }
        """;

    private static string OperationsJson(string availability = "available")
        => $$"""
        {
          "tenantId": "t1",
          "window": "last_24_hours",
          "windowStart": "2026-10-18T10:00:00+00:00",
          "windowEnd": "2026-10-19T10:00:00+00:00",
          "availability": "{{availability}}",
          "listeners": [],
          "counters": { "liveSessions": 1, "opened": 2, "closed": 1, "faulted": 0 },
          "provenance": "proxy_listener_events"
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
          "counts": { "awaitingDecision": 1, "held": 1, "quarantined": 0, "review": 0 },
          "coverage": { "complete": true, "note": null }
        }
        """;

    private static string StorageJson()
        => """
        {
          "tenantId": "t1",
          "asOf": "2026-10-19T10:00:00+00:00",
          "availability": "available",
          "databaseBytes": 4096,
          "writeAheadLogBytes": 1024,
          "spoolBytes": 2048,
          "spoolTruncated": null,
          "spoolFileCount": 1,
          "payloadBytes": 8192,
          "capacityBytes": 100000000000,
          "volumeFreeBytes": 90000000000,
          "capacityScope": "filesystem_volume",
          "retentionHorizon": null,
          "retentionReason": "retention_horizon_not_recorded",
          "rowCounts": [],
          "coverage": { "measured": ["database", "spool", "payloads"], "note": null }
        }
        """;
}
