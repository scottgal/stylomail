using System.Net;
using System.Text;
using System.Text.Json;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Models;
using StyloMail.Desktop.ViewModels;
using StyloMail.Policy;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The policy configuration and approval surface: mode words, lifecycle, refusals, bindings and
/// the two deliberate gaps (authoritative enablement and preview).
/// </summary>
public sealed class PolicyConfigurationTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Protection_mode_is_plain_and_authoritative_is_deployment_configuration()
    {
        var modes = new (bool Authoritative, bool Shadow, string Expected)[]
        {
            (false, false, "Off"),
            (false, true, "Shadow"),
            (true, false, "Authoritative"),
        };

        foreach (var mode in modes)
        {
            var handler = Handler(request => request.Path switch
            {
                "/v1/policy/status" => Json(Status(mode.Authoritative, mode.Shadow)),
                "/v1/policy/packs" => Json(new { tenantId = "t1", packs = Array.Empty<object>() }),
                "/v1/policy/bindings" => Json(new { tenantId = "t1", bindings = Array.Empty<object>() }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

            var model = Model(handler);
            await model.LoadAsync();

            Assert.Equal(mode.Expected, model.ProtectionModeLabel);
            Assert.False(model.CanEnableAuthoritative);
            Assert.Contains("deployment configuration", model.AuthoritativeGapLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("does not invent one", model.PreviewGapLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("consequential", model.ConsequenceLabel, StringComparison.OrdinalIgnoreCase);
            Assert.False(model.HasActivePack);
            Assert.False(model.HasTolerances);
        }
    }

    [Fact]
    public async Task The_full_pack_lifecycle_moves_through_the_host_routes()
    {
        var created = false;
        var state = "Draft";
        CapturedRequest? draftRequest = null;
        CapturedRequest? approveRequest = null;
        CapturedRequest? activateRequest = null;
        CapturedRequest? deactivateRequest = null;

        var handler = Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return request.Path switch
                {
                    "/v1/policy/status" => Json(Status(authoritative: false, shadow: false)),
                    "/v1/policy/packs" => Json(new
                    {
                        tenantId = "t1",
                        packs = created ? new[] { PackSummary(state) } : Array.Empty<object>(),
                    }),
                    "/v1/policy/packs/pack" => Json(Pack(state)),
                    "/v1/policy/bindings" => Json(new { tenantId = "t1", bindings = Array.Empty<object>() }),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }

            if (request.Path == "/v1/policy/packs")
            {
                draftRequest = request;
                created = true;
                state = "Draft";
                return Json(Pack(state));
            }

            if (request.Path.EndsWith("/approve", StringComparison.Ordinal))
            {
                approveRequest = request;
                state = "Approved";
                return Json(Pack(state));
            }

            if (request.Path.EndsWith("/activate", StringComparison.Ordinal))
            {
                activateRequest = request;
                state = "Effective";
                return Json(Pack(state));
            }

            if (request.Path.EndsWith("/deactivate", StringComparison.Ordinal))
            {
                deactivateRequest = request;
                state = "Withdrawn";
                return Json(Pack(state));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var model = Model(handler);
        await model.LoadAsync();

        await model.OpenNewDraftAsync();
        model.DraftId = "pack";
        model.DraftVersion = "1";
        model.DraftJurisdiction = "GB";
        model.DraftRuleSetVersion = "rs1";
        model.DraftScorerVersion = "s1";

        await model.SaveDraftAsync();

        Assert.NotNull(draftRequest);
        Assert.Contains("\"id\":\"pack\"", draftRequest!.Body, StringComparison.Ordinal);
        Assert.Contains("\"jurisdiction\":\"GB\"", draftRequest.Body, StringComparison.Ordinal);
        Assert.Contains("\"child_safety_grooming\":\"Strict\"", draftRequest.Body, StringComparison.Ordinal);
        Assert.Contains("\"captureText\"", draftRequest.Body, StringComparison.Ordinal);
        Assert.Equal("Draft", model.Packs.Single().StateId);

        model.BeginPackAction(model.Packs.Single(), "approve");
        model.SecondApprover = "second-admin";
        model.PackActionConfirmed = true;
        await model.ConfirmPackActionAsync();

        Assert.NotNull(approveRequest);
        Assert.Contains("\"secondApprover\":\"second-admin\"", approveRequest!.Body, StringComparison.Ordinal);
        Assert.Equal("Approved", model.Packs.Single().StateId);

        model.BeginPackAction(model.Packs.Single(), "activate");
        model.PackActionConfirmed = true;
        await model.ConfirmPackActionAsync();

        Assert.NotNull(activateRequest);
        Assert.Equal("Effective", model.Packs.Single().StateId);
        Assert.Contains("In effect", model.Packs.Single().Title, StringComparison.Ordinal);

        model.BeginPackAction(model.Packs.Single(), "deactivate");
        model.PackActionReason = "returning to the legacy engine";
        model.PackActionConfirmed = true;
        await model.ConfirmPackActionAsync();

        Assert.NotNull(deactivateRequest);
        Assert.Contains("\"reason\":\"returning to the legacy engine\"", deactivateRequest!.Body, StringComparison.Ordinal);
        Assert.Equal("Withdrawn", model.Packs.Single().StateId);
    }

    [Fact]
    public async Task A_draft_can_be_withdrawn_with_an_audited_reason()
    {
        var state = "Draft";
        CapturedRequest? withdrawRequest = null;
        var handler = Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return request.Path switch
                {
                    "/v1/policy/status" => Json(Status()),
                    "/v1/policy/packs" => Json(new { tenantId = "t1", packs = new[] { PackSummary(state) } }),
                    "/v1/policy/packs/pack" => Json(Pack(state)),
                    "/v1/policy/bindings" => Json(new { tenantId = "t1", bindings = Array.Empty<object>() }),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }

            if (request.Path.EndsWith("/withdraw", StringComparison.Ordinal))
            {
                withdrawRequest = request;
                state = "Withdrawn";
                return Json(Pack(state));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var model = Model(handler);
        await model.LoadAsync();

        model.BeginPackAction(model.Packs.Single(), "withdraw");
        model.PackActionReason = "superseded by a later draft";
        model.PackActionConfirmed = true;
        await model.ConfirmPackActionAsync();

        Assert.NotNull(withdrawRequest);
        Assert.Contains("\"reason\":\"superseded by a later draft\"", withdrawRequest!.Body, StringComparison.Ordinal);
        Assert.Equal("Withdrawn", model.Packs.Single().StateId);
    }

    [Fact]
    public async Task A_second_approver_refusal_is_surfaced_and_keeps_the_panel_open()
    {
        CapturedRequest? approveRequest = null;
        var handler = Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return request.Path switch
                {
                    "/v1/policy/status" => Json(Status()),
                    "/v1/policy/packs" => Json(new { tenantId = "t1", packs = new[] { PackSummary("Draft") } }),
                    "/v1/policy/packs/pack" => Json(Pack("Draft")),
                    "/v1/policy/bindings" => Json(new { tenantId = "t1", bindings = Array.Empty<object>() }),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }

            approveRequest = request;
            return Json(
                new { error = "pack.second_approver_must_differ", detail = "The second approver must be a different human from the approver." },
                HttpStatusCode.BadRequest);
        });

        var model = Model(handler);
        await model.LoadAsync();

        model.BeginPackAction(model.Packs.Single(), "approve");
        model.SecondApprover = "same-person";
        model.PackActionConfirmed = true;
        await model.ConfirmPackActionAsync();

        Assert.NotNull(approveRequest);
        Assert.Contains("different", model.PackActionError, StringComparison.OrdinalIgnoreCase);
        Assert.True(model.IsPackActionOpen);
        Assert.Equal("Draft", model.Packs.Single().StateId);
    }

    [Fact]
    public async Task A_hard_floor_cannot_be_saved_below_strict()
    {
        var handler = Handler(request => request.Path switch
        {
            "/v1/policy/status" => Json(Status()),
            "/v1/policy/packs" => Json(new { tenantId = "t1", packs = Array.Empty<object>() }),
            "/v1/policy/bindings" => Json(new { tenantId = "t1", bindings = Array.Empty<object>() }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var model = Model(handler);
        await model.LoadAsync();
        await model.OpenNewDraftAsync();
        model.DraftId = "pack";
        model.DraftVersion = "1";
        model.DraftJurisdiction = "GB";
        model.DraftRuleSetVersion = "rs1";
        model.DraftScorerVersion = "s1";

        var floor = model.DraftTolerances.Single(row => row.CategoryId == PolicyCategories.ChildSafetyGrooming);
        floor.SelectedLevel = new PolicyChoice("Off", "Off");

        await model.SaveDraftAsync();

        Assert.Contains("hard floor", model.DraftError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_binding_is_created_with_its_consequence_and_revoked_with_a_reason()
    {
        var bindingExists = false;
        var bindingState = "Active";
        CapturedRequest? createRequest = null;
        CapturedRequest? revokeRequest = null;

        var handler = Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return request.Path switch
                {
                    "/v1/policy/status" => Json(Status(effectivePackId: "pack", effectivePackVersion: "1")),
                    "/v1/policy/packs" => Json(new { tenantId = "t1", packs = new[] { PackSummary("Effective") } }),
                    "/v1/policy/packs/pack" => Json(Pack("Effective")),
                    "/v1/policy/bindings" => Json(new
                    {
                        tenantId = "t1",
                        bindings = bindingExists ? new[] { Binding(bindingState) } : Array.Empty<object>(),
                    }),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }

            if (request.Path == "/v1/policy/bindings")
            {
                createRequest = request;
                bindingExists = true;
                return Json(Binding(bindingState));
            }

            if (request.Path.EndsWith("/revoke", StringComparison.Ordinal))
            {
                revokeRequest = request;
                bindingState = "Revoked";
                return Json(Binding(bindingState));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var model = Model(handler);
        await model.LoadAsync();
        Assert.True(model.HasActivePack);
        Assert.Equal(12, model.Tolerances.Count);

        model.OpenNewBinding();
        Assert.Equal("pack", model.BindingPackId);
        Assert.Equal("1", model.BindingPackVersion);

        model.BindingScopeValue = "example.test";
        model.BindingAction = PolicyChoices.Find(PolicyChoices.BindingActions, "RaiseTolerance", "Permit");
        model.BindingCategory = PolicyChoices.Find(PolicyChoices.Categories, "promotion", "promotion");
        model.BindingTargetLevel = PolicyChoices.Find(PolicyChoices.Levels, "Standard", "Standard");
        model.BindingReason = "known supplier with an existing relationship";
        model.BindingConfirmed = true;

        Assert.Contains("set to Standard", model.BindingConsequence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("more protective target wins", model.BindingPrecedence, StringComparison.OrdinalIgnoreCase);

        await model.SaveBindingAsync();

        Assert.NotNull(createRequest);
        Assert.Contains("\"scopeKind\":\"Domain\"", createRequest!.Body, StringComparison.Ordinal);
        Assert.Contains("\"scopeValue\":\"example.test\"", createRequest.Body, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"RaiseTolerance\"", createRequest.Body, StringComparison.Ordinal);
        Assert.Contains("\"categoryId\":\"promotion\"", createRequest.Body, StringComparison.Ordinal);
        Assert.Contains("\"targetLevel\":\"Standard\"", createRequest.Body, StringComparison.Ordinal);
        Assert.Contains("\"onExpiry\":\"RevertToPack\"", createRequest.Body, StringComparison.Ordinal);
        Assert.False(model.IsBindingEditorOpen);

        var binding = model.Bindings.Single();
        Assert.True(binding.CanRevoke);

        model.BeginRevokeBinding(binding);
        model.BindingRevokeReason = "the supplier relationship ended";
        model.BindingRevokeConfirmed = true;
        await model.ConfirmRevokeBindingAsync();

        Assert.NotNull(revokeRequest);
        Assert.Contains("\"reason\":\"the supplier relationship ended\"", revokeRequest!.Body, StringComparison.Ordinal);
        Assert.Equal("Revoked", model.Bindings.Single().StateId);
    }

    [Fact]
    public async Task Confirmation_gates_every_apply_and_refuses_without_a_request()
    {
        var handler = Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return request.Path switch
                {
                    "/v1/policy/status" => Json(Status(effectivePackId: "pack", effectivePackVersion: "1")),
                    "/v1/policy/packs" => Json(new { tenantId = "t1", packs = new[] { PackSummary("Draft") } }),
                    "/v1/policy/packs/pack" => Json(Pack("Draft")),
                    "/v1/policy/bindings" => Json(new { tenantId = "t1", bindings = new[] { Binding("Active") } }),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var model = Model(handler);
        await model.LoadAsync();

        model.BeginPackAction(model.Packs.Single(), "approve");
        await model.ConfirmPackActionAsync();
        Assert.Contains("Confirm", model.PackActionError, StringComparison.OrdinalIgnoreCase);

        model.OpenNewBinding();
        model.BindingScopeValue = "example.test";
        model.BindingReason = "a reason";
        await model.SaveBindingAsync();
        Assert.Contains("confirm", model.BindingError, StringComparison.OrdinalIgnoreCase);

        model.BeginRevokeBinding(model.Bindings.Single());
        model.BindingRevokeReason = "a reason";
        await model.ConfirmRevokeBindingAsync();
        Assert.Contains("Confirm", model.BindingRevokeError, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public void The_console_category_catalogue_is_pinned_to_the_host_contract()
    {
        Assert.Equal(PolicyCategories.All.Count, PolicyCategoryCatalog.All.Count);

        foreach (var hostCategory in PolicyCategories.All)
        {
            var local = PolicyCategoryCatalog.Find(hostCategory.Id);
            Assert.NotNull(local);
            Assert.Equal(hostCategory.Label, local!.Label);
            Assert.Equal(hostCategory.IsHardFloor, local.IsHardFloor);
        }

        var hostFloors = PolicyCategories.All
            .Where(category => category.IsHardFloor)
            .Select(category => category.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["child_safety_grooming", "credible_threats_of_violence", "illegal_content"],
            hostFloors);

        Assert.Equal(["Strict", "ZeroTolerance"], PolicyChoices.HardFloorLevels.Select(choice => choice.Id));
        Assert.Equal(["Off", "Observe", "Standard", "Strict", "ZeroTolerance"], PolicyChoices.Levels.Select(choice => choice.Id));
    }

    private static PolicyViewModel Model(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5271") };
        return new PolicyViewModel(new StyloMailApiClient(http, new TestApiKeyProvider()));
    }

    private static StubHttpMessageHandler Handler(Func<CapturedRequest, HttpResponseMessage> respond) => new(respond);

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, WebJson), Encoding.UTF8, "application/json"),
        };

    private static object Status(
        bool authoritative = false,
        bool shadow = false,
        string? effectivePackId = null,
        string? effectivePackVersion = null) => new
    {
        tenantId = "t1",
        authoritativeEnabled = authoritative,
        jurisdiction = "GB",
        effectivePackId,
        effectivePackVersion,
        shadowEnabled = shadow,
        reviewStateAvailable = true,
        cacheAge = (string?)null,
    };

    private static object PackSummary(string state) => new
    {
        tenantId = "t1",
        id = "pack",
        version = "1",
        state,
        jurisdiction = "GB",
        contentSha256 = "abc123",
        author = "operator",
        approver = state is "Approved" or "Effective" ? "approver-one" : null,
        secondApprover = state is "Approved" or "Effective" ? "approver-two" : null,
        effectiveFrom = "2026-10-20T00:00:00+00:00",
        reviewDue = "2027-01-18T00:00:00+00:00",
        createdAt = "2026-10-19T12:00:00+00:00",
        approvedAt = state is "Approved" or "Effective" ? "2026-10-19T12:30:00+00:00" : null,
        supersededBy = (string?)null,
    };

    private static object Pack(string state) => new
    {
        tenantId = "t1",
        id = "pack",
        version = "1",
        state,
        context = new { vertical = "Commerce", audience = "Adults", jurisdiction = "GB" },
        categoryLevels = PolicyCategoryCatalog.All.ToDictionary(
            category => category.Id,
            category => category.IsHardFloor ? "Strict" : "Standard"),
        actionCeiling = "Review",
        captureText = new
        {
            mode = "None",
            maxCharacters = 0,
            retentionDays = 0,
            statement = "No message content is retained.",
        },
        evidenceRetentionDays = 30,
        reviewDeadline = "1.00:00:00",
        ruleSetVersion = "rs1",
        scorerVersion = "s1",
        author = "operator",
        approver = state is "Approved" or "Effective" ? "approver-one" : null,
        secondApprover = state is "Approved" or "Effective" ? "approver-two" : null,
        effectiveFrom = "2026-10-20T00:00:00+00:00",
        reviewDue = "2027-01-18T00:00:00+00:00",
        supersedes = (string?)null,
        contentSha256 = "abc123",
        createdAt = "2026-10-19T12:00:00+00:00",
        approvedAt = state is "Approved" or "Effective" ? "2026-10-19T12:30:00+00:00" : null,
        supersededBy = (string?)null,
    };

    private static object Binding(string state) => new
    {
        id = "b1",
        state,
        scopeKind = "Domain",
        scopeValue = "example.test",
        action = "RaiseTolerance",
        categoryId = "promotion",
        targetLevel = "Standard",
        packId = "pack",
        packVersion = "1",
        author = "operator",
        approvedBy = (string?)null,
        reason = "known supplier with an existing relationship",
        startsAt = "2026-10-19T00:00:00+00:00",
        expiresAt = "2026-11-18T00:00:00+00:00",
        reviewBy = "2026-11-02T00:00:00+00:00",
        onExpiry = "RevertToPack",
    };
}
