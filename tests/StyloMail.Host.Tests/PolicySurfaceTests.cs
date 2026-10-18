using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Contracts;
using StyloMail.Host.Policy;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;
using StyloMail.Policy;

namespace StyloMail.Host.Tests;

/// <summary>
/// The policy administration surface: pack lifecycle, bindings and the capability catalogue.
/// </summary>
/// <remarks>
/// Phase 1 stores and serves policy; it does not apply it. These tests therefore assert on the
/// lifecycle and the refusals, never on a delivery action. The one structural claim they enforce is
/// that the routes work with no assessor at all: if a policy route had reached the evaluator, the
/// host built without one could not answer it.
/// </remarks>
public sealed class PolicySurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string PackId = "pack-commerce";
    private const string RuleSetVersion = "rules/1";

    // ---------------------------------------------------------------------------------------------
    // Pack lifecycle
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_pack_moves_from_draft_to_effective_only_through_approval()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);

        var draft = await DraftAsync(client, "1.0.0");
        Assert.Equal(PolicyPackState.Draft, draft.State);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, draft.Author);

        var approved = await ApproveAsync(client, "1.0.0");
        Assert.Equal(PolicyPackState.Approved, approved.State);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, approved.Approver);

        var effective = await ActivateAsync(client, "1.0.0");
        Assert.Equal(PolicyPackState.Effective, effective.State);
        Assert.Null(effective.SupersededBy);

        var list = await ReadAsync<PolicyPackListResponse>(await client.GetAsync("/v1/policy/packs"));
        var only = Assert.Single(list.Packs);
        Assert.Equal(PolicyPackState.Effective, only.State);
    }

    [Fact]
    public async Task A_second_version_supersedes_the_first_and_leaves_one_effective()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);

        await DraftAsync(client, "1.0.0");
        await ApproveAsync(client, "1.0.0");
        await ActivateAsync(client, "1.0.0");

        await DraftAsync(client, "2.0.0");
        await ApproveAsync(client, "2.0.0");
        await ActivateAsync(client, "2.0.0");

        var list = await ReadAsync<PolicyPackListResponse>(await client.GetAsync("/v1/policy/packs"));
        var first = Assert.Single(list.Packs, pack => pack.Version == "1.0.0");
        var second = Assert.Single(list.Packs, pack => pack.Version == "2.0.0");

        Assert.Equal(PolicyPackState.Superseded, first.State);
        Assert.Equal($"{PackId}@2.0.0", first.SupersededBy);
        Assert.Equal(PolicyPackState.Effective, second.State);

        // One effective pack is the invariant the partial unique index also enforces.
        Assert.Single(list.Packs, pack => pack.State == PolicyPackState.Effective);
    }

    [Fact]
    public async Task Lowering_a_category_needs_a_distinct_second_administrator()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);

        await DraftAsync(client, "1.0.0");
        await ApproveAsync(client, "1.0.0");
        await ActivateAsync(client, "1.0.0");

        var levels = DefaultLevels();
        levels[PolicyCategories.PaymentRedirection] = ToleranceLevel.Standard;
        await DraftAsync(client, "2.0.0", levels: levels);

        var reader = host.Services.GetRequiredService<IPolicyPackReader>();
        var store = host.Services.GetRequiredService<SqlitePolicyPackStore>();
        Assert.NotNull(store.FindEffectiveRow(TestPrincipals.AcmeTenant, "GB"));

        var v2 = await reader.GetVersionAsync(TestPrincipals.AcmeTenant, PackId, "2.0.0");
        Assert.NotNull(v2);
        Assert.Equal(
            ToleranceLevel.Standard,
            v2!.CategoryLevels[PolicyCategories.PaymentRedirection]);

        // The previous pack is effective and its payment level is the one being lowered. Without
        // this, a green approval would only show that nothing was compared.
        var previous = await reader.GetEffectiveAsync(TestPrincipals.AcmeTenant, "GB", Now);
        Assert.NotNull(previous);
        Assert.Equal(
            ToleranceLevel.Strict,
            previous!.CategoryLevels[PolicyCategories.PaymentRedirection]);

        var noSecond = await client.PostAsync(
            $"/v1/policy/packs/{PackId}/2.0.0/approve",
            JsonContent.Create(new PolicyPackApprovalRequest(), options: HostJson.Options));
        Assert.Equal(HttpStatusCode.BadRequest, noSecond.StatusCode);
        Assert.Contains("pack.level_lowered_without_second_approver", await noSecond.Content.ReadAsStringAsync());

        var samePrincipal = await client.PostAsync(
            $"/v1/policy/packs/{PackId}/2.0.0/approve",
            JsonContent.Create(
                new PolicyPackApprovalRequest { SecondApprover = TestPrincipals.AcmeOperatorPrincipal },
                options: HostJson.Options));
        Assert.Equal(HttpStatusCode.BadRequest, samePrincipal.StatusCode);
        Assert.Contains("pack.second_approver_must_differ", await samePrincipal.Content.ReadAsStringAsync());

        var second = await client.PostAsync(
            $"/v1/policy/packs/{PackId}/2.0.0/approve",
            JsonContent.Create(
                new PolicyPackApprovalRequest { SecondApprover = TestPrincipals.AcmeSecondOperatorPrincipal },
                options: HostJson.Options));
        await AssertSuccess(second);

        var approved = await ReadAsync<PolicyPackResponse>(second);
        Assert.Equal(TestPrincipals.AcmeSecondOperatorPrincipal, approved.SecondApprover);
    }

    [Fact]
    public async Task A_pack_cannot_take_effect_before_it_was_approved()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);

        await DraftAsync(client, "1.0.0", effectiveFrom: Now.AddDays(-1));
        await ApproveAsync(client, "1.0.0");

        var activation = await client.PostAsync(
            $"/v1/policy/packs/{PackId}/1.0.0/activate",
            JsonContent.Create(new { }, options: HostJson.Options));

        Assert.Equal(HttpStatusCode.BadRequest, activation.StatusCode);
        Assert.Contains("pack.effective_before_approval", await activation.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_effective_pack_is_deactivated_rather_than_withdrawn_in_place()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);

        await DraftAsync(client, "1.0.0");
        await ApproveAsync(client, "1.0.0");
        await ActivateAsync(client, "1.0.0");

        var withdraw = await client.PostAsync(
            $"/v1/policy/packs/{PackId}/1.0.0/withdraw",
            JsonContent.Create(new PolicyPackTransitionRequest { Reason = "wrong pack" }, options: HostJson.Options));
        Assert.Equal(HttpStatusCode.Conflict, withdraw.StatusCode);
        Assert.Contains("pack.effective_requires_deactivate", await withdraw.Content.ReadAsStringAsync());

        var deactivate = await client.PostAsync(
            $"/v1/policy/packs/{PackId}/1.0.0/deactivate",
            JsonContent.Create(new PolicyPackTransitionRequest { Reason = "return to legacy" }, options: HostJson.Options));
        await AssertSuccess(deactivate);

        var response = await ReadAsync<PolicyPackResponse>(deactivate);
        Assert.Equal(PolicyPackState.Withdrawn, response.State);
    }

    [Fact]
    public async Task Activation_refuses_a_pack_whose_rule_set_is_not_live()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await DraftAsync(client, "1.0.0");
        await ApproveAsync(client, "1.0.0");

        var activation = await client.PostAsync(
            $"/v1/policy/packs/{PackId}/1.0.0/activate",
            JsonContent.Create(new { }, options: HostJson.Options));

        Assert.Equal(HttpStatusCode.BadRequest, activation.StatusCode);
        Assert.Contains("pack.rule_set_not_live", await activation.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_read_that_promotes_a_due_pack_records_a_scheduler_event()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);

        await DraftAsync(client, "1.0.0");
        await ApproveAsync(client, "1.0.0");
        await ActivateAsync(client, "1.0.0");

        // A future pack is a schedule, not an activation. The read that promotes it is a state
        // change on a Review read, so it must leave an audit row naming the system actor.
        await DraftAsync(client, "2.0.0", effectiveFrom: Now.AddHours(1));
        await ApproveAsync(client, "2.0.0");
        var scheduled = await ActivateAsync(client, "2.0.0");
        Assert.Equal(PolicyPackState.Approved, scheduled.State);

        host.Clock.Advance(TimeSpan.FromHours(2));

        var reader = host.Services.GetRequiredService<IPolicyPackReader>();
        var effective = await reader.GetEffectiveAsync(
            TestPrincipals.AcmeTenant, "GB", host.Clock.GetUtcNow());

        Assert.NotNull(effective);
        Assert.Equal("2.0.0", effective!.Version);
        Assert.Equal(
            ToleranceLevel.Strict,
            effective.CategoryLevels[PolicyCategories.PaymentRedirection]);

        var database = host.Services.GetRequiredService<HostDatabase>();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM host_policy_pack_event
            WHERE tenant_id = $tenant AND pack_id = $pack AND version = '2.0.0'
              AND event = 'activated' AND actor = 'policy-scheduler';
            """;
        command.Parameters.AddWithValue("$tenant", TestPrincipals.AcmeTenant);
        command.Parameters.AddWithValue("$pack", PackId);

        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_a_pack()
    {
        using var host = new TestHost().WithClock();
        using var acme = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        using var globex = host.ClientAs(TestPrincipals.GlobexOperatorKey);
        await SeedRuleSetAsync(host);

        await DraftAsync(acme, "1.0.0");

        var globexList = await ReadAsync<PolicyPackListResponse>(await globex.GetAsync("/v1/policy/packs"));
        Assert.Empty(globexList.Packs);

        var globexRead = await globex.GetAsync($"/v1/policy/packs/{PackId}?version=1.0.0");
        Assert.Equal(HttpStatusCode.NotFound, globexRead.StatusCode);
    }

    [Fact]
    public async Task Reading_packs_needs_review_and_changing_them_needs_administer()
    {
        using var host = new TestHost().WithClock();
        await SeedRuleSetAsync(host);

        using var anonymous = host.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/policy/packs")).StatusCode);

        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await sender.GetAsync("/v1/policy/packs")).StatusCode);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        Assert.Equal(HttpStatusCode.OK, (await reviewer.GetAsync("/v1/policy/packs")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await reviewer.PostAsync(
                "/v1/policy/packs",
                JsonContent.Create(DraftRequest("1.0.0"), options: HostJson.Options))).StatusCode);

        using var admin = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await AssertSuccess(await admin.PostAsync(
            "/v1/policy/packs",
            JsonContent.Create(DraftRequest("1.0.0"), options: HostJson.Options)));
    }

    [Fact]
    public async Task An_approved_pack_cannot_be_reapproved()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);

        await DraftAsync(client, "1.0.0");
        await ApproveAsync(client, "1.0.0");

        var again = await client.PostAsync(
            $"/v1/policy/packs/{PackId}/1.0.0/approve",
            JsonContent.Create(new PolicyPackApprovalRequest(), options: HostJson.Options));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("pack.version_immutable", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_binding_must_be_authored_against_the_effective_pack()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);

        await DraftAsync(client, "1.0.0");
        await ApproveAsync(client, "1.0.0");

        // Approved is not effective: a binding that could be created now would encode an exception
        // against a pack that cannot yet decide anything.
        var response = await client.PostAsync(
            "/v1/policy/bindings",
            JsonContent.Create(BindingRequest(BindingAction.Permit), options: HostJson.Options));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("binding.pack_not_effective", await response.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // Bindings
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_binding_is_created_listed_and_revoked_as_an_event()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);
        await DraftAsync(client, "1.0.0");
        await ApproveAsync(client, "1.0.0");
        await ActivateAsync(client, "1.0.0");

        var created = await CreateBindingAsync(client, BindingAction.Permit);
        Assert.Equal(PolicyBindingState.Active, created.State);
        Assert.StartsWith("bnd_", created.Id);

        var active = await ReadAsync<PolicyBindingListResponse>(await client.GetAsync("/v1/policy/bindings"));
        Assert.Single(active.Bindings);

        var revoked = await ReadAsync<PolicyBindingResponse>(await client.PostAsync(
            $"/v1/policy/bindings/{created.Id}/revoke",
            JsonContent.Create(new PolicyPackTransitionRequest { Reason = "contract ended" }, options: HostJson.Options)));
        Assert.Equal(PolicyBindingState.Revoked, revoked.State);

        var after = await ReadAsync<PolicyBindingListResponse>(await client.GetAsync("/v1/policy/bindings"));
        Assert.Empty(after.Bindings);

        var all = await ReadAsync<PolicyBindingListResponse>(
            await client.GetAsync("/v1/policy/bindings?activeOnly=false"));
        Assert.Single(all.Bindings);
    }

    [Fact]
    public async Task A_binding_cannot_lower_a_hard_floor()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);
        await DraftAsync(client, "1.0.0");
        await ApproveAsync(client, "1.0.0");
        await ActivateAsync(client, "1.0.0");

        var response = await client.PostAsync(
            "/v1/policy/bindings",
            JsonContent.Create(
                BindingRequest(
                    BindingAction.LowerTolerance,
                    categoryId: PolicyCategories.ChildSafetyGrooming,
                    targetLevel: ToleranceLevel.Observe,
                    approvedBy: TestPrincipals.AcmeSecondOperatorPrincipal),
                options: HostJson.Options));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("binding.hard_floor_below_strict", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_capture_binding_is_refused_when_the_pack_permits_no_capture()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);
        await DraftAsync(client, "1.0.0");
        await ApproveAsync(client, "1.0.0");
        await ActivateAsync(client, "1.0.0");

        var response = await client.PostAsync(
            "/v1/policy/bindings",
            JsonContent.Create(
                BindingRequest(
                    BindingAction.CaptureText,
                    approvedBy: TestPrincipals.AcmeSecondOperatorPrincipal),
                options: HostJson.Options));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("binding.capture_exceeds_pack", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_binding_that_has_not_started_is_pending_not_active()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        await SeedRuleSetAsync(host);
        await DraftAsync(client, "1.0.0");
        await ApproveAsync(client, "1.0.0");
        await ActivateAsync(client, "1.0.0");

        var created = await CreateBindingAsync(
            client,
            BindingAction.Permit,
            startsAt: Now.AddDays(1),
            expiresAt: Now.AddDays(30),
            reviewBy: Now.AddDays(20));

        Assert.Equal(PolicyBindingState.Pending, created.State);

        var active = await ReadAsync<PolicyBindingListResponse>(await client.GetAsync("/v1/policy/bindings"));
        Assert.Empty(active.Bindings);
    }

    // ---------------------------------------------------------------------------------------------
    // Capabilities
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_capability_catalogue_covers_every_action_and_names_what_is_not_available()
    {
        using var host = new TestHost().WithClock().WithoutAssessor();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var response = await ReadAsync<PolicyCapabilitiesResponse>(
            await client.GetAsync("/v1/policy/capabilities"));

        foreach (var kind in Enum.GetValues<PolicyActionKind>())
        {
            Assert.Contains(response.Capabilities, capability => capability.Kind == kind);
        }

        var review = Assert.Single(response.Capabilities, capability => capability.Kind == PolicyActionKind.Review);
        Assert.Equal(PolicyCapabilityAvailability.Available, review.Availability);
        Assert.Null(review.DenialReason);
        Assert.NotNull(review.Review);
        Assert.Equal("quarantine", review.Review!.EscalationAction);

        Assert.Contains(response.Capabilities, capability => capability.Kind == PolicyActionKind.ReleaseReview);
        Assert.Contains(response.Capabilities, capability => capability.Kind == PolicyActionKind.QuarantineReview);
        var rejectReview = Assert.Single(
            response.Capabilities,
            capability => capability.Kind == PolicyActionKind.RejectReview);
        Assert.Equal("Administer", rejectReview.RequiredPrivilege);

        var monitor = Assert.Single(response.Capabilities, capability => capability.Kind == PolicyActionKind.MonitorAllMail);
        Assert.Equal(PolicyCapabilityAvailability.NotAvailable, monitor.Availability);
        Assert.Equal("monitor_all_mail_not_implemented", monitor.DenialReason);

        var affected = Assert.Single(
            response.Capabilities,
            capability => capability.Kind == PolicyActionKind.Quarantine);
        var messages = Assert.Single(
            affected.Inputs,
            input => input.Name == "affected_messages");
        Assert.Equal(ConsequenceAvailability.NotProduced, messages.DefaultAvailability);
    }

    [Fact]
    public async Task The_status_route_reports_tenant_enablement_from_the_map()
    {
        using var host = new TestHost()
            .WithClock()
            .Configure("StyloMail:Policy:Authoritative:Tenants:acme", "GB");
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var status = await ReadAsync<PolicyStatusResponse>(
            await reviewer.GetAsync("/v1/policy/status"));

        Assert.Equal(TestPrincipals.AcmeTenant, status.TenantId);
        Assert.True(status.AuthoritativeEnabled);
        Assert.Equal("GB", status.Jurisdiction);
        Assert.False(status.ShadowEnabled);
        Assert.True(status.ReviewStateAvailable);
        Assert.Null(status.EffectivePackId);
    }

    [Fact]
    public async Task The_status_route_is_off_by_default_and_tenant_scoped()
    {
        using var host = new TestHost().WithClock();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var status = await ReadAsync<PolicyStatusResponse>(
            await reviewer.GetAsync("/v1/policy/status"));

        Assert.False(status.AuthoritativeEnabled);
        Assert.Null(status.Jurisdiction);
    }

    [Fact]
    public async Task The_capability_catalogue_is_not_a_tenant_or_pack_read()
    {
        // A host with no assessor at all still answers: the policy routes in Phase 1 do not call the
        // evaluator, and a static catalogue cannot depend on a pack that does not exist.
        using var host = new TestHost().WithClock().WithoutAssessor();
        using var anonymous = host.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/policy/capabilities")).StatusCode);

        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await sender.GetAsync("/v1/policy/capabilities")).StatusCode);

        using var reviewer = host.ClientAs(TestPrincipals.GlobexReviewerKey);
        var response = await ReadAsync<PolicyCapabilitiesResponse>(
            await reviewer.GetAsync("/v1/policy/capabilities"));
        Assert.NotEmpty(response.Capabilities);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures and helpers
    // ---------------------------------------------------------------------------------------------

    private static Dictionary<string, ToleranceLevel> DefaultLevels() => new(StringComparer.Ordinal)
    {
        [PolicyCategories.ChildSafetyGrooming] = ToleranceLevel.Strict,
        [PolicyCategories.CredibleThreatsOfViolence] = ToleranceLevel.Strict,
        [PolicyCategories.IllegalContent] = ToleranceLevel.Strict,
        [PolicyCategories.PaymentRedirection] = ToleranceLevel.Strict,
        [PolicyCategories.CredentialRequest] = ToleranceLevel.Standard,
    };

    private static PolicyPackDraftRequest DraftRequest(
        string version,
        IReadOnlyDictionary<string, ToleranceLevel>? levels = null,
        DateTimeOffset? effectiveFrom = null) => new()
        {
            Id = PackId,
            Version = version,
            Context = new PolicyPackContext
            {
                Vertical = PolicyVertical.Commerce,
                Audience = PolicyAudience.Adults,
                Jurisdiction = "GB",
            },
            CategoryLevels = levels ?? DefaultLevels(),
            ActionCeiling = PolicyVerdict.Quarantine,
            CaptureText = TextCaptureRules.Disabled,
            EvidenceRetentionDays = 30,
            RuleSetVersion = RuleSetVersion,
            ScorerVersion = "scorer/1",
            EffectiveFrom = effectiveFrom ?? Now,
            ReviewDue = Now.AddMonths(6),
        };

    private static PolicyRuleSet RuleSet() => new()
    {
        Id = "rules-commerce",
        Version = RuleSetVersion,
        Rules =
        [
            new PolicyRule
            {
                Id = "rule-payment-redirection",
                Version = 1,
                Category = PolicyCategories.PaymentRedirection,
                SignalId = "semantic.payment_redirection",
                Comparison = RuleComparison.AtLeast,
                RequiredSignalIds = ["semantic.payment_redirection"],
                Bands =
                [
                    new RuleBand(0.80, PolicyVerdict.Quarantine, "policy.payment_quarantine"),
                    new RuleBand(0.55, PolicyVerdict.Hold, "policy.payment_hold"),
                ],
                MinimumSampleSupport = 1,
            },
        ],
    };

    private static async Task SeedRuleSetAsync(TestHost host, string version = RuleSetVersion)
    {
        var store = host.Services.GetRequiredService<SqlitePolicyRuleSetStore>();
        await store.SaveAsync(TestPrincipals.AcmeTenant, RuleSet() with { Version = version }, PolicyRuleSetState.Live, "test");
    }

    private static async Task<PolicyPackResponse> DraftAsync(
        HttpClient client,
        string version,
        IReadOnlyDictionary<string, ToleranceLevel>? levels = null,
        DateTimeOffset? effectiveFrom = null)
        => await ReadAsync<PolicyPackResponse>(await client.PostAsync(
            "/v1/policy/packs",
            JsonContent.Create(DraftRequest(version, levels, effectiveFrom), options: HostJson.Options)));

    private static async Task<PolicyPackResponse> ApproveAsync(
        HttpClient client,
        string version,
        string? secondApprover = null)
        => await ReadAsync<PolicyPackResponse>(await client.PostAsync(
            $"/v1/policy/packs/{PackId}/{version}/approve",
            JsonContent.Create(new PolicyPackApprovalRequest { SecondApprover = secondApprover }, options: HostJson.Options)));

    private static async Task<PolicyPackResponse> ActivateAsync(HttpClient client, string version)
        => await ReadAsync<PolicyPackResponse>(await client.PostAsync(
            $"/v1/policy/packs/{PackId}/{version}/activate",
            JsonContent.Create(new { }, options: HostJson.Options)));

    private static PolicyBindingRequest BindingRequest(
        BindingAction action,
        string? categoryId = null,
        ToleranceLevel? targetLevel = null,
        string? approvedBy = null,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? reviewBy = null) => new()
        {
            ScopeKind = BindingScope.Entity,
            ScopeValue = "entity-1",
            Action = action,
            CategoryId = categoryId,
            TargetLevel = targetLevel,
            PackId = PackId,
            PackVersion = "1.0.0",
            ApprovedBy = approvedBy,
            Reason = "The tenant is a school; this supplier is its known payment processor.",
            StartsAt = startsAt ?? Now,
            ExpiresAt = expiresAt ?? Now.AddDays(30),
            ReviewBy = reviewBy ?? Now.AddDays(20),
            OnExpiry = BindingOnExpiry.HoldForReview,
        };

    private static async Task<PolicyBindingResponse> CreateBindingAsync(
        HttpClient client,
        BindingAction action,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? reviewBy = null)
        => await ReadAsync<PolicyBindingResponse>(await client.PostAsync(
            "/v1/policy/bindings",
            JsonContent.Create(
                BindingRequest(action, startsAt: startsAt, expiresAt: expiresAt, reviewBy: reviewBy),
                options: HostJson.Options)));

    private static async Task AssertSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        Assert.Fail($"Expected success, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
        where T : class
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {(int)response.StatusCode}: {body}");

        var value = JsonSerializer.Deserialize<T>(body, HostJson.Options);
        Assert.NotNull(value);
        return value!;
    }
}
