using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StyloMail.Core;
using StyloMail.Host.Contracts;
using StyloMail.Host.Controls;
using StyloMail.Host.Decisions;
using StyloMail.Host.Profiles;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;
using StyloMail.Profiles.Kpis;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// Entity, domain, company and conversation profiles: versioned KPI definitions, coverage, named
/// absence and tenant scoping.
/// </summary>
/// <remarks>
/// <para>
/// <b>The assertions are about what the system refuses to claim</b>, as much as what it computes.
/// A KPI with no answered evidence must be NotProduced, a thin population NotEnoughHistory, and a
/// zero only where a denominator exists. Tests that only checked the produced path would pass on a
/// system that turned every outage into a calm zero.
/// </para>
/// <para>
/// Fixtures seed the real ledger and the real queue, because entity attribution joins them. A fake
/// that returned rows would leave the join, the scope predicate and the tenancy filter unverified.
/// </para>
/// </remarks>
public sealed class ProfileKpiTests
{
    private const string Acme = TestPrincipals.AcmeTenant;
    private const string PrincipalA = "principal-a";
    private const string PrincipalB = "principal-b";
    private const string Key = "0123456789abcdef0123456789abcdef";

    // ---------------------------------------------------------------------------------------------
    // Catalogue and the empty profile
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_catalogue_serves_six_versioned_definitions_with_named_reasons()
    {
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var catalogue = await ReadAsync<KpiCatalogueResponse>(await reviewer.GetAsync("/v1/kpis"));

        Assert.Equal(6, catalogue.Definitions.Count);
        Assert.Equal(6, catalogue.Definitions.Select(definition => definition.Id).Distinct().Count());

        Assert.All(catalogue.Definitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Question));
            Assert.False(string.IsNullOrWhiteSpace(definition.Numerator));
            Assert.False(string.IsNullOrWhiteSpace(definition.Denominator));
            Assert.False(string.IsNullOrWhiteSpace(definition.Window));
            Assert.False(string.IsNullOrWhiteSpace(definition.Version));
            Assert.True(definition.MinimumSample >= 1);
            Assert.NotEmpty(definition.AvailabilityReasons);
        });
    }

    [Fact]
    public async Task An_empty_profile_names_every_absence_instead_of_zero()
    {
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var profile = await ProfileAsync(reviewer, "entity", "nobody");

        Assert.Equal(0, profile.Coverage.Messages);
        Assert.Equal(KpiReasons.NoAttributableMessages, profile.Coverage.Reason);
        Assert.Null(profile.Relationship.InboundMessages);
        Assert.Null(profile.Relationship.OutboundMessages);

        Assert.All(profile.Kpis, kpi =>
        {
            Assert.Equal(KpiState.NotProduced, kpi.State);
            Assert.Null(kpi.Value);
            Assert.Equal(KpiReasons.NoAttributableMessages, kpi.Reason);
            Assert.Equal(0, kpi.Coverage.Total);
        });

        var absent = profile.NotMeasured.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("tone", absent);
        Assert.Contains("friendliness", absent);
        Assert.Contains("composite_score", absent);
        Assert.Contains("intent", absent);
        Assert.Contains("reply_threading", absent);
    }

    [Fact]
    public async Task Profiles_need_review_and_never_a_tenant_parameter()
    {
        using var host = new TestHost();
        using var anonymous = host.Anonymous();
        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/v1/profiles/entity/{PrincipalA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await sender.GetAsync($"/v1/profiles/entity/{PrincipalA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await reviewer.GetAsync($"/v1/profiles/entity/{PrincipalA}")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await reviewer.GetAsync($"/v1/profiles/entity/{PrincipalA}?windowDays=3")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await reviewer.GetAsync($"/v1/profiles/planet/{PrincipalA}")).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Evidence KPIs
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pressure_uses_answered_rows_as_its_denominator()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a1", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.9)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a2", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.8)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a3", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.7)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a4", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.1)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a5", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.2)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a6", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.0, EvidenceAvailability.Unavailable)));

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "entity", PrincipalA);
        var pressure = Kpi(profile, KpiDefinitions.Pressure);

        Assert.Equal(KpiState.Produced, pressure.State);
        Assert.Equal(0.6, pressure.Value!.Value, 3);
        Assert.Equal(3, pressure.Numerator);
        Assert.Equal(5, pressure.Denominator);

        // Six messages, five answers: the outage is excluded from the denominator, not counted calm.
        Assert.Equal(6, pressure.Coverage.Total);
        Assert.Equal(5, pressure.Coverage.Analysed);
    }

    [Fact]
    public async Task A_thin_signal_is_not_enough_history_not_a_number()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a1", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.9)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a2", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.1)));

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "entity", PrincipalA);
        var pressure = Kpi(profile, KpiDefinitions.Pressure);

        Assert.Equal(KpiState.NotEnoughHistory, pressure.State);
        Assert.Null(pressure.Value);
        Assert.Equal(1, pressure.Numerator);
        Assert.Equal(2, pressure.Denominator);
        Assert.Equal(KpiReasons.NotEnoughHistory, pressure.Reason);
        Assert.Equal(KpiReasons.NotEnoughHistory, pressure.Coverage.Reason);
    }

    [Fact]
    public async Task Sensitive_requests_are_model_evidence_from_either_dimension()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-5);

        // Five answered messages: three credential, two payment, one message carrying both.
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a1", at, MailAction.Allow),
            Signal("semantic.credential_request", 0.9)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a2", at, MailAction.Allow),
            Signal("semantic.credential_request", 0.8)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a3", at, MailAction.Allow),
            Signal("semantic.credential_request", 0.7),
            Signal("semantic.payment_redirection", 0.9)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a4", at, MailAction.Allow),
            Signal("semantic.payment_redirection", 0.8)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a5", at, MailAction.Allow),
            Signal("semantic.payment_redirection", 0.1)));

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var kpi = Kpi(
            await ProfileAsync(reviewer, "entity", PrincipalA),
            KpiDefinitions.SensitiveRequests);

        Assert.Equal(KpiState.Produced, kpi.State);
        Assert.Equal(4, kpi.Numerator);
        Assert.Equal(5, kpi.Denominator);
        Assert.Equal(0.8, kpi.Value!.Value, 3);
        Assert.Contains("model evidence", kpi.Definition.Window, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Consistency_is_identity_anomaly_prevalence_not_drift()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-5);

        for (var index = 0; index < 5; index++)
        {
            var evidence = Signal(
                "deterministic.display_name_address_mismatch",
                index == 0 ? 1 : 0,
                EvidenceAvailability.Available,
                EvidenceOrigin.Deterministic);

            await SeedAsync(host, Assessment(
                Acme,
                $"a{index}",
                at,
                MailAction.Allow,
                [evidence]));
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var kpi = Kpi(
            await ProfileAsync(reviewer, "entity", PrincipalA),
            KpiDefinitions.Consistency);

        Assert.Equal(KpiState.Produced, kpi.State);
        Assert.Equal(0.2, kpi.Value!.Value, 3);
        Assert.Equal(KpiDirection.LowerIsBetter, kpi.Definition.Direction);
        Assert.Contains("NOT drift", kpi.Definition.Window, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handling_counts_holds_and_quarantines_against_all_messages()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-5);

        await SeedAsync(host, Assessment(Acme, "a1", at, MailAction.Hold));
        await SeedAsync(host, Assessment(Acme, "a2", at, MailAction.Quarantine));
        await SeedAsync(host, Assessment(Acme, "a3", at, MailAction.Allow));
        await SeedAsync(host, Assessment(Acme, "a4", at, MailAction.Allow));
        await SeedAsync(host, Assessment(Acme, "a5", at, MailAction.Allow));

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var kpi = Kpi(
            await ProfileAsync(reviewer, "entity", PrincipalA),
            KpiDefinitions.Handling);

        Assert.Equal(KpiState.Produced, kpi.State);
        Assert.Equal(2, kpi.Numerator);
        Assert.Equal(5, kpi.Denominator);
        Assert.Equal(0.4, kpi.Value!.Value, 3);
    }

    [Fact]
    public async Task Momentum_compares_the_window_with_its_own_past()
    {
        using var host = new TestHost().WithClock();
        var now = host.Clock.GetUtcNow();

        for (var index = 0; index < 3; index++)
        {
            await SeedAsync(host, Assessment(Acme, $"prior{index}", now.AddDays(-60), MailAction.Allow));
        }

        for (var index = 0; index < 6; index++)
        {
            await SeedAsync(host, Assessment(Acme, $"recent{index}", now.AddDays(-10), MailAction.Allow));
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var kpi = Kpi(
            await ProfileAsync(reviewer, "entity", PrincipalA, windowDays: 90),
            KpiDefinitions.Momentum);

        Assert.Equal(KpiState.Produced, kpi.State);
        Assert.Equal(6, kpi.Numerator);
        Assert.Equal(3, kpi.Denominator);
        Assert.Equal(2.0, kpi.Value!.Value, 3);
    }

    [Fact]
    public async Task A_window_with_no_prior_half_is_not_enough_history()
    {
        using var host = new TestHost().WithClock();
        var now = host.Clock.GetUtcNow();

        for (var index = 0; index < 4; index++)
        {
            await SeedAsync(host, Assessment(Acme, $"recent{index}", now.AddDays(-10), MailAction.Allow));
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var kpi = Kpi(
            await ProfileAsync(reviewer, "entity", PrincipalA, windowDays: 90),
            KpiDefinitions.Momentum);

        Assert.Equal(KpiState.NotEnoughHistory, kpi.State);
        Assert.Null(kpi.Value);
        Assert.Equal(0, kpi.Denominator);
    }

    // ---------------------------------------------------------------------------------------------
    // Responsiveness and scopes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Responsiveness_is_address_reciprocity_within_seven_days()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(Key);
        var now = host.Clock.GetUtcNow();

        // Five inbound messages from five counterparties; four answered two hours later.
        for (var index = 0; index < 5; index++)
        {
            var inboundAt = now.AddDays(-10).AddMinutes(index);
            await SeedAsync(
                host,
                Assessment(Acme, $"in{index}", inboundAt, MailAction.Allow),
                principal: PrincipalA,
                mailFrom: $"person{index}@example.test",
                direction: MailDirection.Inbound,
                recipients: "owner@acme.test");

            if (index == 4)
            {
                continue;
            }

            await SeedAsync(
                host,
                Assessment(
                    Acme,
                    $"out{index}",
                    inboundAt.AddHours(2),
                    MailAction.Allow,
                    dispositions:
                    [
                        new RecipientDisposition
                        {
                            Recipient = $"person{index}@example.test",
                            RecipientRisk = 0,
                            Action = MailAction.Allow,
                            DeliveryState = DeliveryState.Queued,
                        },
                    ]),
                principal: PrincipalA,
                mailFrom: "owner@acme.test",
                direction: MailDirection.Outbound,
                recipients: $"person{index}@example.test");
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var kpi = Kpi(
            await ProfileAsync(reviewer, "entity", PrincipalA),
            KpiDefinitions.Responsiveness);

        Assert.Equal(KpiState.Produced, kpi.State);
        Assert.Equal(4, kpi.Numerator);
        Assert.Equal(5, kpi.Denominator);
        Assert.Equal(0.8, kpi.Value!.Value, 3);

        var median = Assert.Single(kpi.Measurements, measurement => measurement.Id == "median_reply_gap");
        Assert.Equal(2.0, median.Value!.Value, 3);

        var censored = Assert.Single(
            kpi.Measurements,
            measurement => measurement.Id == "recent_inbound_censored");
        Assert.Equal(0, censored.Value!.Value);
        Assert.Contains("address reciprocity", kpi.Definition.Window, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT produced", kpi.Definition.Window, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responsiveness_without_a_profile_key_is_a_named_absence()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SeedAsync(
            host,
            Assessment(Acme, "in1", at, MailAction.Allow),
            mailFrom: "person@example.test",
            direction: MailDirection.Inbound,
            recipients: "owner@acme.test");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var kpi = Kpi(
            await ProfileAsync(reviewer, "entity", PrincipalA),
            KpiDefinitions.Responsiveness);

        Assert.Equal(KpiState.NotProduced, kpi.State);
        Assert.Equal(KpiReasons.NoProfileKey, kpi.Reason);
    }

    [Fact]
    public async Task A_domain_profile_is_inbound_only_and_says_so()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(Key);
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SeedAsync(
            host,
            Assessment(Acme, "in1", at, MailAction.Allow),
            mailFrom: "sender@example.test",
            direction: MailDirection.Inbound,
            recipients: "owner@acme.test");
        await SeedAsync(
            host,
            Assessment(Acme, "in2", at, MailAction.Allow),
            mailFrom: "other@elsewhere.test",
            direction: MailDirection.Inbound,
            recipients: "owner@acme.test");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "domain", "example.test");

        Assert.Equal(1, profile.Coverage.Messages);
        var identity = Assert.Single(profile.Identities);
        Assert.Equal("domain", identity.Kind);
        Assert.Equal("example.test", identity.Value);

        var responsiveness = Kpi(profile, KpiDefinitions.Responsiveness);
        Assert.Equal(KpiState.NotProduced, responsiveness.State);
        Assert.Equal(KpiReasons.NoOutboundRecipientIdentity, responsiveness.Reason);
        Assert.Contains(profile.NotMeasured, entry => entry.Id == "outbound_to_domain");
    }

    [Fact]
    public async Task A_conversation_profile_reads_thread_decisions_without_an_envelope()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-2);
        var chat = new ChannelContext
        {
            Kind = ChannelKind.Slack,
            WorkspaceId = "W1",
            ChannelId = "C1",
            ThreadId = "T1",
        };

        for (var index = 0; index < 3; index++)
        {
            await RecordOnlyAsync(host, WithSignals(
                Assessment(Acme, $"chat{index}", at, MailAction.Allow, channel: chat),
                Signal("semantic.urgency_pressure", 0.9)));
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "conversation", "T1");

        Assert.Equal(3, profile.Coverage.Messages);
        Assert.Null(profile.Relationship.InboundMessages);
        Assert.Null(profile.Relationship.OutboundMessages);
        Assert.Equal(KpiReasons.NoIdentityCaptured, profile.Relationship.DirectionReason);
        Assert.Contains(profile.NotMeasured, entry => entry.Id == "envelope_direction");

        var pressure = Kpi(profile, KpiDefinitions.Pressure);
        Assert.Equal(KpiState.NotEnoughHistory, pressure.State);
        Assert.Equal(3, pressure.Denominator);
    }

    [Fact]
    public async Task One_entity_profile_does_not_see_another_entitys_messages()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-3);

        for (var index = 0; index < 2; index++)
        {
            await SeedAsync(
                host,
                Assessment(Acme, $"a{index}", at, MailAction.Allow),
                PrincipalA);
        }

        for (var index = 0; index < 3; index++)
        {
            await SeedAsync(
                host,
                Assessment(Acme, $"b{index}", at, MailAction.Allow),
                PrincipalB);
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        Assert.Equal(2, (await ProfileAsync(reviewer, "entity", PrincipalA)).Coverage.Messages);
        Assert.Equal(3, (await ProfileAsync(reviewer, "entity", PrincipalB)).Coverage.Messages);
    }

    [Fact]
    public async Task A_globex_profile_cannot_see_acme_messages_or_their_absence_as_signal()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-3);

        await SeedAsync(host, Assessment(Acme, "a1", at, MailAction.Allow), PrincipalA);

        using var globex = host.ClientAs(TestPrincipals.GlobexOperatorKey);
        var profile = await ProfileAsync(globex, "entity", PrincipalA);

        Assert.Equal(0, profile.Coverage.Messages);
        Assert.Equal(0, profile.Coverage.TenantMessages);
        Assert.All(profile.Kpis, kpi => Assert.Equal(KpiReasons.NoAttributableMessages, kpi.Reason));
    }

    [Fact]
    public async Task An_unreadable_ledger_row_is_counted_rather_than_treated_as_clean()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-3);

        await SeedAsync(host, Assessment(Acme, "a1", at, MailAction.Allow), PrincipalA);

        using (var connection = host.Services.GetRequiredService<HostDatabase>().Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE host_decision_ledger SET payload = '{not-json' "
                + "WHERE tenant_id = $tenant AND assessment_id = 'a1';";
            command.Parameters.AddWithValue("$tenant", Acme);
            command.ExecuteNonQuery();
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "entity", PrincipalA);

        Assert.Equal(0, profile.Coverage.Messages);
        Assert.Equal(1, profile.Coverage.Unparsable);
        Assert.All(profile.Kpis, kpi => Assert.Equal(KpiState.NotProduced, kpi.State));
    }

    // ---------------------------------------------------------------------------------------------
    // Display label
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_profile_without_a_stored_label_says_so_and_names_no_identity()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-3);

        await SeedAsync(host, Assessment(Acme, "a1", at, MailAction.Allow), PrincipalA);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "entity", PrincipalA);

        Assert.False(profile.DisplayLabelAvailable);
        Assert.Null(profile.DisplayLabel);
        Assert.Null(profile.DisplayLabelSource);
        Assert.Equal(KpiReasons.NoStoredLabel, profile.DisplayLabelReason);
    }

    [Fact]
    public async Task An_entity_label_comes_from_the_operator_sender_profile()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-3);

        await host.Services.GetRequiredService<ISenderProfileStore>().PutAsync(
            Acme,
            new SenderProfile
            {
                PrincipalId = PrincipalA,
                Label = "Acme Billing",
                UpdatedBy = "test",
                UpdatedAt = at,
            },
            CancellationToken.None);

        await SeedAsync(host, Assessment(Acme, "a1", at, MailAction.Allow), PrincipalA);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "entity", PrincipalA);

        Assert.True(profile.DisplayLabelAvailable);
        Assert.Equal("Acme Billing", profile.DisplayLabel);
        Assert.Equal("sender_profile", profile.DisplayLabelSource);
        Assert.Null(profile.DisplayLabelReason);
    }

    [Fact]
    public async Task A_company_label_comes_from_the_company_record()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-3);

        await host.Services.GetRequiredService<ICompanyStore>().PutAsync(
            Acme,
            new Company
            {
                CompanyId = "co_acme",
                Name = "Acme Corporation",
                UpdatedBy = "test",
                UpdatedAt = at,
            },
            CancellationToken.None);

        await host.Services.GetRequiredService<ISenderProfileStore>().PutAsync(
            Acme,
            new SenderProfile
            {
                PrincipalId = PrincipalA,
                CompanyId = "co_acme",
                UpdatedBy = "test",
                UpdatedAt = at,
            },
            CancellationToken.None);

        await SeedAsync(host, Assessment(Acme, "a1", at, MailAction.Allow), PrincipalA);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "company", "co_acme");

        Assert.True(profile.DisplayLabelAvailable);
        Assert.Equal("Acme Corporation", profile.DisplayLabel);
        Assert.Equal("company", profile.DisplayLabelSource);
    }

    [Fact]
    public async Task A_domain_label_is_the_domain_the_caller_named()
    {
        using var host = new TestHost().WithClock();

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "domain", "Example.Test");

        Assert.True(profile.DisplayLabelAvailable);
        Assert.Equal("example.test", profile.DisplayLabel);
        Assert.Equal("domain", profile.DisplayLabelSource);
    }

    // ---------------------------------------------------------------------------------------------
    // Prior-window trend
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_trend_compares_the_equal_length_prior_window()
    {
        using var host = new TestHost().WithClock();
        var now = host.Clock.GetUtcNow();

        // Prior window: 4 interventions in 5 messages. Current: 1 in 5.
        for (var index = 0; index < 5; index++)
        {
            await SeedAsync(host, Assessment(
                Acme,
                $"prior{index}",
                now.AddDays(-120),
                index < 4 ? MailAction.Hold : MailAction.Allow));
            await SeedAsync(host, Assessment(
                Acme,
                $"current{index}",
                now.AddDays(-30),
                index == 0 ? MailAction.Hold : MailAction.Allow));
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "entity", PrincipalA, windowDays: 90);
        var trend = Kpi(profile, KpiDefinitions.Handling).Trend;

        Assert.NotNull(trend);
        Assert.Equal(KpiTrendState.Produced, trend!.State);
        Assert.Equal(0.8, trend.PriorValue!.Value, 3);
        Assert.Equal(-0.6, trend.Delta!.Value, 3);
        Assert.Equal(KpiTrendMovement.Down, trend.Movement);
        Assert.Equal(profile.WindowStart, trend.PriorWindowEnd);
        Assert.Equal(profile.WindowStart.AddDays(-90), trend.PriorWindowStart);
        Assert.False(trend.PriorTruncated);
    }

    [Fact]
    public async Task An_empty_prior_window_is_not_enough_history_not_a_rise()
    {
        using var host = new TestHost().WithClock();
        var now = host.Clock.GetUtcNow();

        for (var index = 0; index < 5; index++)
        {
            await SeedAsync(host, Assessment(Acme, $"current{index}", now.AddDays(-30), MailAction.Hold));
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "entity", PrincipalA, windowDays: 90);
        var trend = Kpi(profile, KpiDefinitions.Handling).Trend;

        Assert.NotNull(trend);
        Assert.Equal(KpiTrendState.NotEnoughHistory, trend!.State);
        Assert.Equal(KpiReasons.PriorWindowEmpty, trend.Reason);
        Assert.Null(trend.Movement);
        Assert.Null(trend.Delta);
    }

    [Fact]
    public async Task A_thin_prior_window_is_not_enough_history()
    {
        using var host = new TestHost().WithClock();
        var now = host.Clock.GetUtcNow();

        for (var index = 0; index < 2; index++)
        {
            await SeedAsync(host, WithSignals(
                Assessment(Acme, $"prior{index}", now.AddDays(-120), MailAction.Allow),
                Signal("semantic.urgency_pressure", 0.9)));
        }

        for (var index = 0; index < 5; index++)
        {
            await SeedAsync(host, WithSignals(
                Assessment(Acme, $"current{index}", now.AddDays(-30), MailAction.Allow),
                Signal("semantic.urgency_pressure", 0.9)));
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "entity", PrincipalA, windowDays: 90);
        var trend = Kpi(profile, KpiDefinitions.Pressure).Trend;

        Assert.NotNull(trend);
        Assert.Equal(KpiTrendState.NotEnoughHistory, trend!.State);
        Assert.Equal(KpiReasons.PriorWindowNotEnoughHistory, trend.Reason);
        Assert.Null(trend.PriorValue);
        Assert.Null(trend.Movement);
    }

    [Fact]
    public async Task A_current_window_without_a_value_has_no_trend()
    {
        using var host = new TestHost().WithClock();

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "entity", PrincipalA);
        var trend = Kpi(profile, KpiDefinitions.Handling).Trend;

        Assert.NotNull(trend);
        Assert.Equal(KpiTrendState.NotProduced, trend!.State);
        Assert.Equal(KpiReasons.CurrentWindowNotProduced, trend.Reason);
        Assert.Null(trend.Movement);
    }

    // ---------------------------------------------------------------------------------------------
    // Coverage guards found in review
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_null_valued_signal_is_not_an_answer()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a1", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.9)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a2", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.9)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a3", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.9)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a4", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.1)));
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a5", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.1)));

        // A model answer that was "available" but carried no value is not a negative answer.
        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a6", at, MailAction.Allow),
            SignalWithNoValue("semantic.urgency_pressure")));

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var pressure = Kpi(
            await ProfileAsync(reviewer, "entity", PrincipalA),
            KpiDefinitions.Pressure);

        Assert.Equal(KpiState.Produced, pressure.State);
        Assert.Equal(5, pressure.Denominator);
        Assert.Equal(3, pressure.Numerator);
        Assert.Equal(0.6, pressure.Value!.Value, 3);
        Assert.Equal(6, pressure.Coverage.Total);
    }

    [Fact]
    public async Task Recent_inbound_is_censored_not_counted_as_unanswered()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(Key);
        var now = host.Clock.GetUtcNow();
        var old = now.AddDays(-20);

        for (var index = 0; index < 5; index++)
        {
            var inboundAt = old.AddHours(index);
            await SeedAsync(
                host,
                Assessment(Acme, $"in{index}", inboundAt, MailAction.Allow),
                mailFrom: $"old{index}@example.test",
                direction: MailDirection.Inbound,
                recipients: "owner@acme.test");

            if (index == 4)
            {
                continue;
            }

            await SeedAsync(
                host,
                Assessment(
                    Acme,
                    $"out{index}",
                    inboundAt.AddHours(2),
                    MailAction.Allow,
                    dispositions:
                    [
                        new RecipientDisposition
                        {
                            Recipient = $"old{index}@example.test",
                            RecipientRisk = 0,
                            Action = MailAction.Allow,
                            DeliveryState = DeliveryState.Queued,
                        },
                    ]),
                mailFrom: "owner@acme.test",
                direction: MailDirection.Outbound,
                recipients: $"old{index}@example.test");
        }

        // Two messages younger than the 7-day opportunity: too early to know, not unanswered.
        for (var index = 0; index < 2; index++)
        {
            await SeedAsync(
                host,
                Assessment(Acme, $"recent{index}", now.AddDays(-2), MailAction.Allow),
                mailFrom: $"new{index}@example.test",
                direction: MailDirection.Inbound,
                recipients: "owner@acme.test");
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var kpi = Kpi(
            await ProfileAsync(reviewer, "entity", PrincipalA),
            KpiDefinitions.Responsiveness);

        Assert.Equal(KpiState.Produced, kpi.State);
        Assert.Equal(5, kpi.Denominator);
        Assert.Equal(4, kpi.Numerator);
        Assert.Equal(0.8, kpi.Value!.Value, 3);
        Assert.Equal(2, kpi.Coverage.Censored);

        var censored = Assert.Single(
            kpi.Measurements,
            measurement => measurement.Id == "recent_inbound_censored");
        Assert.Equal(2, censored.Value!.Value);
    }

    [Fact]
    public async Task A_truncated_candidate_set_reduces_coverage_and_never_counts_negatives()
    {
        using var host = WithReadCap(new TestHost().WithClock().WithProfileMasterKey(Key), 10);
        var now = host.Clock.GetUtcNow();
        var old = now.AddDays(-20);

        // Twenty old outbound rows occupy the older part of the candidate window.
        for (var index = 0; index < 20; index++)
        {
            await SeedAsync(
                host,
                Assessment(Acme, $"oldout{index}", old.AddDays(-10).AddMinutes(index), MailAction.Allow),
                mailFrom: "owner@acme.test",
                direction: MailDirection.Outbound,
                recipients: $"someone{index}@elsewhere.test");
        }

        for (var index = 0; index < 5; index++)
        {
            var inboundAt = old.AddHours(index);
            await SeedAsync(
                host,
                Assessment(Acme, $"in{index}", inboundAt, MailAction.Allow),
                mailFrom: $"person{index}@example.test",
                direction: MailDirection.Inbound,
                recipients: "owner@acme.test");

            await SeedAsync(
                host,
                Assessment(
                    Acme,
                    $"reply{index}",
                    inboundAt.AddHours(2),
                    MailAction.Allow,
                    dispositions:
                    [
                        new RecipientDisposition
                        {
                            Recipient = $"person{index}@example.test",
                            RecipientRisk = 0,
                            Action = MailAction.Allow,
                            DeliveryState = DeliveryState.Queued,
                        },
                    ]),
                mailFrom: "owner@acme.test",
                direction: MailDirection.Outbound,
                recipients: $"person{index}@example.test");
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var kpi = Kpi(
            await ProfileAsync(reviewer, "entity", PrincipalA),
            KpiDefinitions.Responsiveness);

        Assert.True(kpi.Coverage.CandidatesTruncated);
        Assert.Equal(10, kpi.Coverage.CandidateCount);

        // The retained candidates still reach back before every inbound message, so the five are
        // judgeable and matched; the state discloses that only part of the candidate set was read.
        Assert.Equal(KpiState.ReducedCoverage, kpi.State);
        Assert.Equal(KpiReasons.ReplyCandidatesIncomplete, kpi.Reason);
        Assert.Equal(5, kpi.Denominator);
        Assert.Equal(5, kpi.Numerator);
        Assert.Equal(1.0, kpi.Value!.Value, 3);
    }

    [Fact]
    public async Task An_unreadable_outbound_candidate_reduces_coverage()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(Key);
        var now = host.Clock.GetUtcNow();
        var start = now.AddDays(-30);

        var corrupted = false;

        for (var index = 0; index < 6; index++)
        {
            var inboundAt = start.AddDays(index);
            await SeedAsync(
                host,
                Assessment(Acme, $"in{index}", inboundAt, MailAction.Allow),
                mailFrom: $"person{index}@example.test",
                direction: MailDirection.Inbound,
                recipients: "owner@acme.test");

            await SeedAsync(
                host,
                Assessment(
                    Acme,
                    $"reply{index}",
                    inboundAt.AddHours(2),
                    MailAction.Allow,
                    dispositions:
                    [
                        new RecipientDisposition
                        {
                            Recipient = $"person{index}@example.test",
                            RecipientRisk = 0,
                            Action = MailAction.Allow,
                            DeliveryState = DeliveryState.Queued,
                        },
                    ]),
                mailFrom: "owner@acme.test",
                direction: MailDirection.Outbound,
                recipients: $"person{index}@example.test");

            if (!corrupted && index == 0)
            {
                using var connection = host.Services.GetRequiredService<HostDatabase>().Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE host_decision_ledger SET payload = '{not-json' "
                    + "WHERE tenant_id = $tenant AND assessment_id = 'reply0';";
                command.Parameters.AddWithValue("$tenant", Acme);
                command.ExecuteNonQuery();
                corrupted = true;
            }
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var kpi = Kpi(
            await ProfileAsync(reviewer, "entity", PrincipalA),
            KpiDefinitions.Responsiveness);

        Assert.Equal(1, kpi.Coverage.CandidateUnparsable);
        Assert.Equal(KpiState.ReducedCoverage, kpi.State);
        Assert.Equal(KpiReasons.ReplyCandidatesIncomplete, kpi.Reason);
        Assert.Equal(5, kpi.Denominator);
        Assert.Equal(5, kpi.Numerator);

        var excluded = Assert.Single(
            kpi.Measurements,
            measurement => measurement.Id == "inbound_excluded_incomplete_candidates");
        Assert.Equal(1, excluded.Value!.Value);
    }

    [Fact]
    public async Task A_reduced_coverage_prior_window_is_not_a_trend_baseline()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(Key);
        var now = host.Clock.GetUtcNow();
        var priorStart = now.AddDays(-120);
        var currentStart = now.AddDays(-30);

        await SeedReciprocityAsync(host, "prior", priorStart, corruptFirstReply: true);
        await SeedReciprocityAsync(host, "current", currentStart, corruptFirstReply: false);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var profile = await ProfileAsync(reviewer, "entity", PrincipalA, windowDays: 90);
        var trend = Kpi(profile, KpiDefinitions.Responsiveness).Trend;

        Assert.NotNull(trend);
        Assert.Equal(KpiTrendState.NotProduced, trend!.State);
        Assert.Equal(KpiReasons.PriorWindowReducedCoverage, trend.Reason);
        Assert.Null(trend.Movement);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static async Task SeedAsync(
        TestHost host,
        MailAssessment assessment,
        string principal = PrincipalA,
        string mailFrom = "sender@example.test",
        MailDirection direction = MailDirection.Inbound,
        params string[] recipients)
    {
        await RecordOnlyAsync(host, assessment);

        var queue = host.Services.GetRequiredService<QueueStore>();
        var admitted = recipients.Length == 0 ? ["owner@acme.test"] : recipients;

        var accepted = await queue.AcceptAsync(
            new QueueSubmission
            {
                TenantId = assessment.TenantId,
                InternalMessageId = assessment.InternalMessageId,
                Direction = direction,
                TrustedPrincipalId = principal,
                MailFrom = mailFrom,
                MimeDigest = "digest-" + Guid.NewGuid().ToString("N"),
                Payload = Encoding.UTF8.GetBytes("Subject: profile\r\n\r\nbody"),
                Recipients =
                [
                    .. admitted.Select(recipient => new RecipientAdmission { Recipient = recipient }),
                ],
                HopCount = 0,
            },
            CancellationToken.None);

        Assert.True(accepted.IsAccepted, accepted.Detail);
    }

    private static async Task RecordOnlyAsync(TestHost host, MailAssessment assessment)
        => await host.Services.GetRequiredService<IDecisionLedger>()
            .RecordAsync(assessment, CancellationToken.None);

    private static MailAssessment Assessment(
        string tenantId,
        string assessmentId,
        DateTimeOffset at,
        MailAction action,
        IReadOnlyList<Evidence>? evidence = null,
        ChannelContext? channel = null,
        IReadOnlyList<RecipientDisposition>? dispositions = null) => new()
        {
            AssessmentId = assessmentId,
            InternalMessageId = $"message-{assessmentId}",
            TenantId = tenantId,
            Channel = channel ?? ChannelContext.Email,
            Evidence = evidence ?? [],
            RiskDimensions = [],
            RiskIndex = 0,
            RiskIndexDenominator = 0,
            CoveredWeightFraction = 0,
            Action = action,
            DeliveryTiming = DeliveryTiming.PostDelivery,
            Reasons = [],
            Versions = new AssessmentVersions
            {
                PolicyVersion = "policy/1",
                QuestionSchemaVersion = "questions/1",
                PreprocessingVersion = "preprocess/1",
            },
            Coverage = new AnalysisCoverage
            {
                BodyParsed = true,
                HtmlPresent = false,
                HasAttachments = false,
                HtmlTextDisagreement = false,
                ParserLimitExceeded = false,
                ContentEncrypted = false,
                Truncated = false,
                ConversationContextMissing = true,
            },
            RecipientDispositions = dispositions ?? [],
            AssessedAt = at,
        };

    private static TestHost WithReadCap(TestHost host, int cap)
        => host.Override(services =>
        {
            services.RemoveAll<ProfileQueryStore>();
            services.AddSingleton(sp => new ProfileQueryStore(
                sp.GetRequiredService<HostDatabase>(),
                sp.GetRequiredService<IAddressPseudonymSource>())
            {
                ReadCap = cap,
            });
        });

    private static async Task SeedReciprocityAsync(
        TestHost host,
        string prefix,
        DateTimeOffset start,
        bool corruptFirstReply)
    {
        for (var index = 0; index < 6; index++)
        {
            var inboundAt = start.AddDays(index);
            await SeedAsync(
                host,
                Assessment(Acme, $"{prefix}-in{index}", inboundAt, MailAction.Allow),
                mailFrom: $"{prefix}-person{index}@example.test",
                direction: MailDirection.Inbound,
                recipients: "owner@acme.test");

            await SeedAsync(
                host,
                Assessment(
                    Acme,
                    $"{prefix}-reply{index}",
                    inboundAt.AddHours(2),
                    MailAction.Allow,
                    dispositions:
                    [
                        new RecipientDisposition
                        {
                            Recipient = $"{prefix}-person{index}@example.test",
                            RecipientRisk = 0,
                            Action = MailAction.Allow,
                            DeliveryState = DeliveryState.Queued,
                        },
                    ]),
                mailFrom: "owner@acme.test",
                direction: MailDirection.Outbound,
                recipients: $"{prefix}-person{index}@example.test");

            if (corruptFirstReply && index == 0)
            {
                using var connection = host.Services.GetRequiredService<HostDatabase>().Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE host_decision_ledger SET payload = '{not-json' "
                    + "WHERE tenant_id = $tenant AND assessment_id = $id;";
                command.Parameters.AddWithValue("$tenant", Acme);
                command.Parameters.AddWithValue("$id", $"{prefix}-reply0");
                command.ExecuteNonQuery();
            }
        }
    }

    private static Evidence SignalWithNoValue(string signalId) => new()
    {
        SignalId = signalId,
        Origin = EvidenceOrigin.Semantic,
        Availability = EvidenceAvailability.Available,
        Value = null,
        SourceVersion = "test/1",
        ObservedAt = DateTimeOffset.UnixEpoch,
    };

    private static MailAssessment WithSignals(MailAssessment assessment, params Evidence[] evidence)
        => assessment with { Evidence = [.. assessment.Evidence, .. evidence] };

    private static Evidence Signal(
        string signalId,
        double value,
        EvidenceAvailability availability = EvidenceAvailability.Available,
        EvidenceOrigin origin = EvidenceOrigin.Semantic) => new()
        {
            SignalId = signalId,
            Origin = origin,
            Availability = availability,
            Value = value,
            SourceVersion = "test/1",
            ObservedAt = DateTimeOffset.UnixEpoch,
        };

    private static async Task<ProfileResponse> ProfileAsync(
        HttpClient client,
        string kind,
        string id,
        int? windowDays = null)
    {
        var url = $"/v1/profiles/{kind}/{Uri.EscapeDataString(id)}";
        if (windowDays is not null)
        {
            url += $"?windowDays={windowDays}";
        }

        return await ReadAsync<ProfileResponse>(await client.GetAsync(url));
    }

    private static KpiValueResponse Kpi(ProfileResponse profile, string id)
        => Assert.Single(profile.Kpis, kpi => kpi.Definition.Id == id);

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
