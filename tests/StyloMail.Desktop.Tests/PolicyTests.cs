using System.Net;
using System.Text;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Models;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Tests;

public sealed class PolicyTests
{
    [Fact]
    public async Task The_policy_page_reads_status_pack_tolerances_and_bindings()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/policy/status" => Json(StatusJson()),
            "/v1/policy/packs" => Json(PacksJson()),
            "/v1/policy/packs/pack" => Json(PackJson()),
            "/v1/policy/bindings" => Json(BindingsJson()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        Assert.Equal("Protection is on.", model.ProtectionLabel);
        Assert.Equal("Shadow mode is off.", model.ShadowLabel);
        Assert.Contains("GB", model.JurisdictionLabel, StringComparison.Ordinal);
        Assert.True(model.HasActivePack);
        Assert.Contains("pack", model.ActivePackTitle, StringComparison.Ordinal);
        Assert.Contains("Commerce", model.ActivePackContext, StringComparison.Ordinal);
        Assert.Contains("No message content is retained", model.ActivePackCapture, StringComparison.Ordinal);
        Assert.Contains("stop before delivery", model.ActivePackCeiling, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Payment redirection", model.Tolerances.Single(row => row.Category == "Payment redirection").Category, StringComparison.Ordinal);
        Assert.Contains(model.Tolerances, row => row.Level == "Strict");
        Assert.Equal(12, model.Tolerances.Count);
        Assert.Contains(model.Tolerances, row => row.Level == "Not declared");
        Assert.Single(model.Packs);
        Assert.Single(model.Bindings);
        Assert.Contains(model.TechnicalLines, line => line.StartsWith("Routes read:", StringComparison.Ordinal));
        Assert.Contains("\"status\"", model.TechnicalJson, StringComparison.Ordinal);
        Assert.Contains("\"packs\"", model.TechnicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_active_pack_is_an_honest_empty_state()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/policy/status" => Json("""
                {
                  "tenantId": "t1",
                  "authoritativeEnabled": false,
                  "jurisdiction": null,
                  "effectivePackId": null,
                  "effectivePackVersion": null,
                  "shadowEnabled": false,
                  "reviewStateAvailable": false,
                  "cacheAge": null
                }
                """),
            "/v1/policy/packs" => Json("""{ "tenantId": "t1", "packs": [] }"""),
            "/v1/policy/bindings" => Json("""{ "tenantId": "t1", "bindings": [] }"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        Assert.False(model.HasActivePack);
        Assert.False(model.HasPacks);
        Assert.False(model.HasBindings);
        Assert.Contains("No active policy pack", model.StatusLabel, StringComparison.Ordinal);
        Assert.Equal("Protection is off.", model.ProtectionLabel);
    }

    [Fact]
    public async Task A_policy_read_failure_is_named_without_throwing()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/policy/status" => Json(
                """{ "error": "storage_unavailable", "detail": "The policy store could not be read." }""",
                HttpStatusCode.ServiceUnavailable),
            "/v1/policy/packs" => Json("""{ "tenantId": "t1", "packs": [] }"""),
            "/v1/policy/bindings" => Json("""{ "tenantId": "t1", "bindings": [] }"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();

        Assert.Contains("could not be read", model.StatusLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_policy_destination_opens_the_policy_page()
    {
        var model = ShellModel.CreateDefault();
        var policy = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Policy");

        model.SelectedItem = policy;

        Assert.True(model.IsPolicySelected);
        Assert.False(model.ShowQueuePanes);
    }

    private static PolicyViewModel Model(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5271") };
        return new PolicyViewModel(new StyloMailApiClient(http, new TestApiKeyProvider()));
    }

    private static StubHttpMessageHandler Handler(Func<CapturedRequest, HttpResponseMessage> respond) => new(respond);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string StatusJson()
        => """
        {
          "tenantId": "t1",
          "authoritativeEnabled": true,
          "jurisdiction": "GB",
          "effectivePackId": "pack",
          "effectivePackVersion": "1",
          "shadowEnabled": false,
          "reviewStateAvailable": true,
          "cacheAge": null
        }
        """;

    private static string PacksJson()
        => """
        {
          "tenantId": "t1",
          "packs": [
            {
              "id": "pack",
              "version": "1",
              "state": "Effective",
              "jurisdiction": "GB",
              "contentSha256": "abc",
              "author": "operator",
              "approver": null,
              "secondApprover": null,
              "effectiveFrom": "2026-10-18T09:00:00+00:00",
              "reviewDue": null,
              "createdAt": "2026-10-18T09:00:00+00:00",
              "approvedAt": "2026-10-18T09:00:00+00:00",
              "supersededBy": null
            }
          ]
        }
        """;

    private static string PackJson()
        => """
        {
          "tenantId": "t1",
          "id": "pack",
          "version": "1",
          "state": "Effective",
          "context": { "vertical": "Commerce", "audience": "Adults", "jurisdiction": "GB" },
          "categoryLevels": { "payment_redirection": "Strict", "promotion": "Standard" },
          "actionCeiling": "Quarantine",
          "captureText": { "mode": "None", "maxCharacters": 0, "retentionDays": 0, "statement": "No message content is retained." },
          "evidenceRetentionDays": 30,
          "reviewDeadline": "1.00:00:00",
          "ruleSetVersion": "rs1",
          "scorerVersion": "s1",
          "author": "operator",
          "approver": null,
          "secondApprover": null,
          "effectiveFrom": "2026-10-18T09:00:00+00:00",
          "reviewDue": null,
          "supersedes": null,
          "contentSha256": "abc",
          "createdAt": "2026-10-18T09:00:00+00:00",
          "approvedAt": "2026-10-18T09:00:00+00:00",
          "supersededBy": null
        }
        """;

    private static string BindingsJson()
        => """
        {
          "tenantId": "t1",
          "bindings": [
            {
              "id": "b1",
              "state": "Active",
              "scopeKind": "Domain",
              "scopeValue": "example.test",
              "action": "RaiseTolerance",
              "categoryId": "promotion",
              "targetLevel": "Standard",
              "packId": "pack",
              "packVersion": "1",
              "author": "operator",
              "approvedBy": null,
              "reason": "known supplier",
              "startsAt": "2026-10-18T09:00:00+00:00",
              "expiresAt": "2026-11-18T09:00:00+00:00",
              "reviewBy": "2026-11-01T09:00:00+00:00",
              "onExpiry": "ReturnToPack"
            }
          ]
        }
        """;
}
