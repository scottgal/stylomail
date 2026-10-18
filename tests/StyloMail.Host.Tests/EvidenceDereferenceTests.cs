using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Contracts;
using StyloMail.Host.Decisions;
using StyloMail.Host.Profiles;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;
using StyloMail.Profiles.Kpis;
using StyloMail.Profiles.Properties;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// The evidence drawer: what a KPI was computed from, and what a stored evidence reference points
/// at. Review-only, tenant-scoped and content-free.
/// </summary>
/// <remarks>
/// The assertions include the negative: no address, subject, filename or body appears in a
/// response. A drawer that could open a message would be a PII surface the property and KPI
/// contracts deliberately keep closed.
/// </remarks>
public sealed class EvidenceDereferenceTests
{
    private const string Acme = TestPrincipals.AcmeTenant;
    private const string Principal = "principal-a";

    [Fact]
    public async Task Kpi_evidence_returns_definition_coverage_signals_and_opaque_sources()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        for (var index = 0; index < 5; index++)
        {
            await SeedAsync(host, WithSignals(
                Assessment(Acme, $"a{index}", at, MailAction.Allow),
                Signal("semantic.urgency_pressure", index < 3 ? 0.9 : 0.1)));
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var body = await EvidenceJsonAsync(
            reviewer,
            "/v1/evidence/kpi/pressure.urgency_prevalence/entity/" + Principal);

        var evidence = JsonSerializer.Deserialize<KpiEvidenceResponse>(body, HostJson.Options);
        Assert.NotNull(evidence);

        Assert.Equal(KpiDefinitions.Pressure, evidence!.Definition.Id);
        Assert.Equal("1.1.0", evidence.Definition.Version);
        Assert.Equal(PropertyScopeKind.Entity, evidence.Scope.Kind);
        Assert.Equal(KpiState.Produced, evidence.Value.State);
        Assert.Equal(3, evidence.Value.Numerator);
        Assert.Equal(5, evidence.Value.Denominator);

        var signal = Assert.Single(evidence.Signals);
        Assert.Equal("semantic.urgency_pressure", signal.SignalId);
        Assert.Equal(5, signal.Answered);
        Assert.Equal(3, signal.Confirming);
        Assert.Equal(0, signal.Unavailable);

        Assert.Equal(5, evidence.SourceCount);
        Assert.False(evidence.SourcesTruncated);
        Assert.Equal(5, evidence.Sources.Count);
        Assert.All(evidence.Sources, source =>
        {
            Assert.False(string.IsNullOrWhiteSpace(source.AssessmentId));
            Assert.Contains("semantic.urgency_pressure", source.MeasuredSignalIds);
        });

        // No content, no addresses, no subject, no filename.
        Assert.DoesNotContain("@", body);
        Assert.DoesNotContain("Subject:", body);
        Assert.DoesNotContain("filename", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Kpi_evidence_for_a_thin_signal_is_not_enough_history()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        for (var index = 0; index < 2; index++)
        {
            await SeedAsync(host, WithSignals(
                Assessment(Acme, $"a{index}", at, MailAction.Allow),
                Signal("semantic.urgency_pressure", 0.9)));
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var evidence = await EvidenceAsync(
            reviewer,
            "/v1/evidence/kpi/pressure.urgency_prevalence/entity/" + Principal);

        Assert.Equal(KpiState.NotEnoughHistory, evidence.Value.State);
        Assert.Null(evidence.Value.Value);
        Assert.Equal(2, evidence.Signals.Single().Answered);
        Assert.Equal(2, evidence.SourceCount);
    }

    [Fact]
    public async Task Kpi_evidence_for_no_messages_is_not_produced()
    {
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var evidence = await EvidenceAsync(
            reviewer,
            "/v1/evidence/kpi/pressure.urgency_prevalence/entity/nobody");

        Assert.Equal(KpiState.NotProduced, evidence.Value.State);
        Assert.Equal(KpiReasons.NoAttributableMessages, evidence.Value.Reason);
        Assert.Empty(evidence.Sources);
        Assert.Equal(0, evidence.SourceCount);
        Assert.Equal(0, evidence.Signals.Single().Answered);
    }

    [Fact]
    public async Task A_stored_reference_resolves_to_citations_and_an_opaque_source()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SeedAsync(host, Assessment(Acme, "asm-ref-1", at, MailAction.Hold));
        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "asm-ref-1",
            Kind = EvidenceKind.Message,
            Label = "Submission asm-ref-1",
            ObservedAt = at,
            Retention = EvidenceRetention.Retained,
            AccessOutcome = EvidenceAccessOutcome.Available,
        });

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var body = await ReferenceJsonAsync(reviewer, "Message", "asm-ref-1");
        var evidence = JsonSerializer.Deserialize<ReferenceEvidenceResponse>(body, HostJson.Options);

        Assert.NotNull(evidence);
        Assert.Equal(ReferenceOutcome.Resolved, evidence!.Outcome);
        Assert.Equal("asm-ref-1", evidence.LatestClaim.Id);
        Assert.Equal(EvidenceKind.Message, evidence.LatestClaim.Kind);
        Assert.Equal(at, evidence.LatestClaim.ObservedAt);
        Assert.Equal("all", evidence.Selection);
        Assert.True(evidence.SourcePresent);

        var citation = Assert.Single(evidence.Citations);
        Assert.Equal(PropertyScopeKind.Entity, citation.Scope.Kind);
        Assert.Equal(PropertyKeys.Role, citation.Key);
        Assert.Equal("vendor", citation.Value);

        Assert.NotNull(evidence.Source);
        Assert.Equal("asm-ref-1", evidence.Source!.AssessmentId);
        Assert.True(evidence.SourcePresent);
        Assert.Equal(MailAction.Hold, evidence.Source.Action);

        Assert.DoesNotContain("@", body);
        Assert.DoesNotContain("Subject:", body);
        Assert.DoesNotContain("filename", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_expired_reference_reports_expired_without_claiming_resolution()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-60);

        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "asm-expired",
            Kind = EvidenceKind.Message,
            ObservedAt = at,
            Retention = EvidenceRetention.Scheduled,
            RetentionUntil = at.AddDays(1),
            AccessOutcome = EvidenceAccessOutcome.Available,
        });

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var evidence = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=Message&id=asm-expired");

        Assert.Equal(ReferenceOutcome.Expired, evidence.Outcome);
        Assert.Single(evidence.Citations);
        Assert.Null(evidence.Source);
    }

    [Fact]
    public async Task A_withheld_reference_reports_withheld()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "asm-withheld",
            Kind = EvidenceKind.ConversationTurn,
            ObservedAt = at,
            AccessOutcome = EvidenceAccessOutcome.Withheld,
        });

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var evidence = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=ConversationTurn&id=asm-withheld");

        Assert.Equal(ReferenceOutcome.Withheld, evidence.Outcome);
        Assert.Single(evidence.Citations);
    }

    [Fact]
    public async Task A_missing_reference_is_a_named_not_found()
    {
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var response = await reviewer.GetAsync("/v1/evidence/reference?kind=Message&id=does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("evidence_not_found", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Evidence_is_tenant_scoped()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SeedAsync(host, WithSignals(
            Assessment(Acme, "a1", at, MailAction.Allow),
            Signal("semantic.urgency_pressure", 0.9)));
        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "a1",
            Kind = EvidenceKind.Message,
            ObservedAt = at,
            AccessOutcome = EvidenceAccessOutcome.Available,
        });

        using var globex = host.ClientAs(TestPrincipals.GlobexOperatorKey);

        // A reference owned by acme does not exist for globex.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await globex.GetAsync("/v1/evidence/reference?kind=Message&id=a1")).StatusCode);

        // A KPI at the same entity id resolves to no evidence, not to acme's rows.
        var evidence = await EvidenceAsync(
            globex,
            "/v1/evidence/kpi/pressure.urgency_prevalence/entity/" + Principal);

        Assert.Equal(KpiState.NotProduced, evidence.Value.State);
        Assert.Equal(0, evidence.SourceCount);
    }

    [Fact]
    public async Task Evidence_needs_review()
    {
        using var host = new TestHost();
        using var anonymous = host.Anonymous();
        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/v1/evidence/kpi/pressure.urgency_prevalence/entity/{Principal}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await sender.GetAsync($"/v1/evidence/kpi/pressure.urgency_prevalence/entity/{Principal}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await reviewer.GetAsync($"/v1/evidence/kpi/pressure.urgency_prevalence/entity/{Principal}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await reviewer.GetAsync("/v1/evidence/reference?id=x")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await reviewer.GetAsync("/v1/evidence/reference?kind=Message")).StatusCode);
    }

    [Fact]
    public async Task Reference_kind_is_part_of_the_identity()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SeedAsync(host, Assessment(Acme, "shared", at, MailAction.Allow));

        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "shared",
            Kind = EvidenceKind.Message,
            ObservedAt = at,
            AccessOutcome = EvidenceAccessOutcome.Available,
        });

        await SetReferenceAsync(
            host,
            at,
            new EvidenceReference
            {
                Id = "shared",
                Kind = EvidenceKind.Import,
                ObservedAt = at,
                AccessOutcome = EvidenceAccessOutcome.Available,
            },
            key: PropertyKeys.Language,
            value: "en");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var asMessage = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=Message&id=shared");
        Assert.Single(asMessage.Citations);
        Assert.Equal(PropertyKeys.Role, asMessage.Citations[0].Key);
        Assert.NotNull(asMessage.Source);

        var asImport = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=Import&id=shared");
        Assert.Single(asImport.Citations);
        Assert.Equal(PropertyKeys.Language, asImport.Citations[0].Key);
        Assert.Null(asImport.Source);
        Assert.Equal(ReferenceOutcome.Unverified, asImport.Outcome);
    }

    [Fact]
    public async Task An_undefined_or_numeric_evidence_kind_is_refused()
    {
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var numeric = await reviewer.GetAsync("/v1/evidence/reference?kind=99&id=x");
        Assert.Equal(HttpStatusCode.BadRequest, numeric.StatusCode);
        using (var document = JsonDocument.Parse(await numeric.Content.ReadAsStringAsync()))
        {
            Assert.Equal("unknown_evidence_kind", document.RootElement.GetProperty("error").GetString());
        }

        var named = await reviewer.GetAsync("/v1/evidence/reference?kind=Nonsense&id=x");
        Assert.Equal(HttpStatusCode.BadRequest, named.StatusCode);
    }

    [Fact]
    public async Task A_freeform_label_is_not_echoed()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "asm-label",
            Kind = EvidenceKind.Message,
            Label = "Subject: password reset for person@example.test",
            ObservedAt = at,
            AccessOutcome = EvidenceAccessOutcome.Available,
        });

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var body = await ReferenceJsonAsync(reviewer, "Message", "asm-label");

        Assert.DoesNotContain("Subject:", body);
        Assert.DoesNotContain("@example.test", body);
        Assert.DoesNotContain("password reset", body);
    }

    [Fact]
    public async Task Mixed_reference_claims_are_conservative_and_per_citation()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        // Withheld is the older claim; Available is newer. A newest-wins implementation would
        // wrongly resolve this as available.
        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "asm-mixed",
            Kind = EvidenceKind.Message,
            ObservedAt = at,
            AccessOutcome = EvidenceAccessOutcome.Withheld,
        });

        await SetReferenceAsync(
            host,
            at.AddMinutes(1),
            new EvidenceReference
            {
                Id = "asm-mixed",
                Kind = EvidenceKind.Message,
                ObservedAt = at,
                AccessOutcome = EvidenceAccessOutcome.Available,
            },
            key: PropertyKeys.Language,
            value: "en");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var evidence = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=Message&id=asm-mixed");

        // The most restrictive claim governs regardless of which citation is newest.
        Assert.Equal(ReferenceOutcome.Withheld, evidence.Outcome);
        Assert.Equal(2, evidence.CitationCount);
        Assert.Contains(evidence.Citations, citation => citation.AccessOutcome == EvidenceAccessOutcome.Withheld);
        Assert.Contains(evidence.Citations, citation => citation.AccessOutcome == EvidenceAccessOutcome.Available);
    }

    [Fact]
    public async Task A_missing_ledger_row_makes_a_message_reference_source_missing()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "asm-vanished",
            Kind = EvidenceKind.Message,
            ObservedAt = at,
            AccessOutcome = EvidenceAccessOutcome.Available,
        });

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var evidence = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=Message&id=asm-vanished");

        Assert.Equal(ReferenceOutcome.SourceMissing, evidence.Outcome);
        Assert.Null(evidence.Source);
    }

    [Fact]
    public async Task A_conversation_turn_does_not_borrow_a_message_source()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SeedAsync(host, Assessment(Acme, "shared-source", at, MailAction.Allow));
        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "shared-source",
            Kind = EvidenceKind.ConversationTurn,
            ObservedAt = at,
            AccessOutcome = EvidenceAccessOutcome.Available,
        });

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var evidence = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=ConversationTurn&id=shared-source");

        Assert.Null(evidence.Source);
        Assert.False(evidence.SourcePresent);
        Assert.Equal(ReferenceOutcome.Unverified, evidence.Outcome);
    }

    [Fact]
    public async Task Known_exclusions_survive_a_thin_responsiveness_window()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(
            "0123456789abcdef0123456789abcdef");
        var now = host.Clock.GetUtcNow();
        var start = now.AddDays(-30);

        for (var index = 0; index < 6; index++)
        {
            var inboundAt = start.AddDays(index);
            await SeedMessageAsync(
                host,
                Assessment(Acme, $"in{index}", inboundAt, MailAction.Allow),
                MailDirection.Inbound,
                $"person{index}@example.test",
                "owner@acme.test");

            await SeedMessageAsync(
                host,
                Assessment(
                    Acme,
                    $"reply{index}",
                    inboundAt.AddHours(2),
                    MailAction.Allow) with
                {
                    RecipientDispositions =
                    [
                        new RecipientDisposition
                        {
                            Recipient = $"person{index}@example.test",
                            RecipientRisk = 0,
                            Action = MailAction.Allow,
                            DeliveryState = DeliveryState.Queued,
                        },
                    ],
                },
                MailDirection.Outbound,
                "owner@acme.test",
                $"person{index}@example.test");

            if (index < 5)
            {
                CorruptPayload(host, $"reply{index}");
            }
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var evidence = await EvidenceAsync(
            reviewer,
            "/v1/evidence/kpi/responsiveness.address_reciprocity/entity/" + Principal);

        Assert.Equal(KpiState.NotEnoughHistory, evidence.Value.State);
        Assert.Equal(5, evidence.Value.Coverage.IncompleteExclusions);
        Assert.Equal(5, evidence.Signals.Single().Unavailable);
        Assert.Equal(1, evidence.Signals.Single().Answered);
    }

    [Fact]
    public async Task A_malformed_stored_array_is_named_invalid_not_a_storage_error()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "broken",
            Kind = EvidenceKind.Message,
            ObservedAt = at,
        });

        using (var connection = host.Services.GetRequiredService<HostDatabase>().Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE host_entity_property SET evidence_refs = $refs "
                + "WHERE tenant_id = $tenant;";
            command.Parameters.AddWithValue("$refs", "[{\"id\":\"broken\", \"kind\":}]");
            command.Parameters.AddWithValue("$tenant", Acme);
            command.ExecuteNonQuery();
        }

        RebuildEvidenceIndex(host);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.GetAsync("/v1/evidence/reference?kind=Message&id=broken");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {(int)response.StatusCode}: {body}");
        Assert.Equal(1, JsonDocument.Parse(body).RootElement.GetProperty("invalidCitationRows").GetInt32());
        Assert.Equal(
            ReferenceOutcome.Invalid,
            JsonSerializer.Deserialize<ReferenceEvidenceResponse>(body, HostJson.Options)!.Outcome);
    }

    [Fact]
    public async Task A_legacy_string_reference_is_other_and_not_a_message()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SeedAsync(host, Assessment(Acme, "legacy-source", at, MailAction.Allow));
        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "legacy-id",
            Kind = EvidenceKind.Other,
            ObservedAt = at,
        });

        using (var connection = host.Services.GetRequiredService<HostDatabase>().Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE host_entity_property SET evidence_refs = $refs "
                + "WHERE tenant_id = $tenant;";
            command.Parameters.AddWithValue("$refs", "[\"legacy-id\"]");
            command.Parameters.AddWithValue("$tenant", Acme);
            command.ExecuteNonQuery();
        }

        RebuildEvidenceIndex(host);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var asOther = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=Other&id=legacy-id");
        Assert.Single(asOther.Citations);
        Assert.Equal(ReferenceOutcome.Unverified, asOther.Outcome);

        // A legacy row records no kind, so a Message query cannot claim it.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await reviewer.GetAsync("/v1/evidence/reference?kind=Message&id=legacy-id")).StatusCode);
    }

    [Fact]
    public async Task Fifty_one_wrong_kind_claims_do_not_hide_a_message()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        // 51 newer Import claims with the same id, on 51 different entities.
        for (var index = 0; index < 51; index++)
        {
            await SetReferenceAsync(
                host,
                at.AddSeconds(index),
                new EvidenceReference
                {
                    Id = "hidden",
                    Kind = EvidenceKind.Import,
                    ObservedAt = at,
                    AccessOutcome = EvidenceAccessOutcome.Available,
                },
                scopeId: $"entity-{index}");
        }

        // One older true Message claim, withheld.
        await SetReferenceAsync(
            host,
            at.AddDays(-1),
            new EvidenceReference
            {
                Id = "hidden",
                Kind = EvidenceKind.Message,
                ObservedAt = at,
                AccessOutcome = EvidenceAccessOutcome.Withheld,
            },
            scopeId: "entity-true");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var evidence = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=Message&id=hidden");

        Assert.Equal(1, evidence.CitationCount);
        Assert.Equal(1, evidence.ReturnedCount);
        Assert.Equal(ReferenceOutcome.Withheld, evidence.Outcome);
    }

    [Fact]
    public async Task A_restrictive_claim_beyond_the_page_still_governs()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        // A live Message source so the "50 available + ledger" path would resolve if the page were
        // mistaken for the whole claim set.
        await SeedAsync(host, Assessment(Acme, "paged", at, MailAction.Allow));

        for (var index = 0; index < 51; index++)
        {
            await SetReferenceAsync(
                host,
                at.AddSeconds(index),
                new EvidenceReference
                {
                    Id = "paged",
                    Kind = EvidenceKind.Message,
                    ObservedAt = at,
                    // The oldest claim is restrictive; it is the one a newest-50 page drops.
                    AccessOutcome = index == 0
                        ? EvidenceAccessOutcome.Withheld
                        : EvidenceAccessOutcome.Available,
                },
                scopeId: $"entity-{index}");
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var evidence = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=Message&id=paged");

        Assert.Equal(51, evidence.CitationCount);
        Assert.Equal(50, evidence.ReturnedCount);
        Assert.True(evidence.CitationsTruncated);
        Assert.Equal("newest", evidence.Selection);
        Assert.Equal(ReferenceOutcome.Withheld, evidence.Outcome);
        Assert.True(evidence.SourcePresent);
    }

    [Fact]
    public async Task An_escaped_json_id_is_matched_by_value()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        // JsonSerializer escapes '+' as \u002b, so a raw LIKE cannot find this id.
        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "a+b",
            Kind = EvidenceKind.Message,
            ObservedAt = at,
            AccessOutcome = EvidenceAccessOutcome.Available,
        });

        await SeedAsync(host, Assessment(Acme, "a+b", at, MailAction.Allow));

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var evidence = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=Message&id=" + Uri.EscapeDataString("a+b"));

        Assert.Equal(1, evidence.CitationCount);
        Assert.Equal(ReferenceOutcome.Resolved, evidence.Outcome);
        Assert.True(evidence.SourcePresent);
    }

    [Fact]
    public async Task A_null_reference_element_is_named_invalid()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "nullref",
            Kind = EvidenceKind.Message,
            ObservedAt = at,
        });

        using (var connection = host.Services.GetRequiredService<HostDatabase>().Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE host_entity_property SET evidence_refs = $refs "
                + "WHERE tenant_id = $tenant;";
            command.Parameters.AddWithValue(
                "$refs",
                "[{\"id\":\"nullref\",\"kind\":\"Message\"},null]");
            command.Parameters.AddWithValue("$tenant", Acme);
            command.ExecuteNonQuery();
        }

        RebuildEvidenceIndex(host);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.GetAsync("/v1/evidence/reference?kind=Message&id=nullref");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {(int)response.StatusCode}: {body}");
        var evidence = JsonSerializer.Deserialize<ReferenceEvidenceResponse>(body, HostJson.Options)!;
        Assert.Equal(ReferenceOutcome.Invalid, evidence.Outcome);
        Assert.Equal(1, evidence.InvalidCitationRows);
    }

    [Fact]
    public async Task Duplicate_references_are_rejected_at_write_in_either_order()
    {
        using var host = new TestHost();
        using var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var orders = new[]
        {
            new object[]
            {
                new { id = "dup", kind = "Message", accessOutcome = "Available" },
                new { id = "dup", kind = "Message", accessOutcome = "Withheld" },
            },
            new object[]
            {
                new { id = "dup", kind = "Message", accessOutcome = "Withheld" },
                new { id = "dup", kind = "Message", accessOutcome = "Available" },
            },
        };

        foreach (var refs in orders)
        {
            var response = await operatorClient.PutAsJsonAsync(
                $"/v1/properties/entity/{Principal}/role",
                new { value = "vendor", evidenceRefs = refs });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("duplicate_evidence_ref", document.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task A_claim_without_a_time_reports_null_not_an_epoch()
    {
        using var host = new TestHost().WithClock();
        var at = host.Clock.GetUtcNow().AddDays(-10);

        await SetReferenceAsync(host, at, new EvidenceReference
        {
            Id = "no-time",
            Kind = EvidenceKind.Message,
            ObservedAt = null,
            AccessOutcome = EvidenceAccessOutcome.Available,
        });

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var evidence = await EvidenceAsync<ReferenceEvidenceResponse>(
            reviewer,
            "/v1/evidence/reference?kind=Message&id=no-time");

        Assert.Null(evidence.LatestClaim.ObservedAt);
        Assert.Null(Assert.Single(evidence.Citations).ObservedAt);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static async Task SetReferenceAsync(
        TestHost host,
        DateTimeOffset at,
        EvidenceReference reference,
        string key = PropertyKeys.Role,
        string value = "vendor",
        string scopeId = Principal)
    {
        await host.Services.GetRequiredService<IPropertyStore>().SetAsync(
            Acme,
            new PropertyWrite
            {
                Scope = new PropertyScope(PropertyScopeKind.Entity, scopeId),
                Key = key,
                Value = value,
                Source = PropertySource.Declared,
                Actor = "test",
                EvidenceRefs = [reference],
            },
            at,
            CancellationToken.None);
    }

    private static async Task SeedAsync(TestHost host, MailAssessment assessment)
        => await SeedMessageAsync(
            host,
            assessment,
            MailDirection.Inbound,
            "sender@example.test",
            "owner@acme.test");

    private static async Task SeedMessageAsync(
        TestHost host,
        MailAssessment assessment,
        MailDirection direction,
        string mailFrom,
        params string[] recipients)
    {
        await host.Services.GetRequiredService<IDecisionLedger>()
            .RecordAsync(assessment, CancellationToken.None);

        var queue = host.Services.GetRequiredService<QueueStore>();
        var accepted = await queue.AcceptAsync(
            new QueueSubmission
            {
                TenantId = assessment.TenantId,
                InternalMessageId = assessment.InternalMessageId,
                Direction = direction,
                TrustedPrincipalId = Principal,
                MailFrom = mailFrom,
                MimeDigest = "digest-" + Guid.NewGuid().ToString("N"),
                Payload = Encoding.UTF8.GetBytes("Subject: evidence\r\n\r\nbody"),
                Recipients = [.. recipients.Select(recipient => new RecipientAdmission { Recipient = recipient })],
                HopCount = 0,
            },
            CancellationToken.None);

        Assert.True(accepted.IsAccepted, accepted.Detail);
    }

    private static void RebuildEvidenceIndex(TestHost host)
    {
        using (var connection = host.Services.GetRequiredService<HostDatabase>().Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM host_entity_property_evidence;";
            command.ExecuteNonQuery();

            command.CommandText = "DELETE FROM host_schema_migration WHERE name = $name;";
            command.Parameters.AddWithValue("$name", HostDatabase.PropertyEvidenceMigrationName);
            command.ExecuteNonQuery();
        }

        host.Services.GetRequiredService<HostDatabase>().EnsureCreated();
    }

    private static void CorruptPayload(TestHost host, string assessmentId)
    {
        using var connection = host.Services.GetRequiredService<HostDatabase>().Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE host_decision_ledger SET payload = '{not-json' "
            + "WHERE tenant_id = $tenant AND assessment_id = $id;";
        command.Parameters.AddWithValue("$tenant", Acme);
        command.Parameters.AddWithValue("$id", assessmentId);
        command.ExecuteNonQuery();
    }

    private static MailAssessment Assessment(
        string tenantId,
        string assessmentId,
        DateTimeOffset at,
        MailAction action) => new()
        {
            AssessmentId = assessmentId,
            InternalMessageId = $"message-{assessmentId}",
            TenantId = tenantId,
            Channel = ChannelContext.Email,
            Evidence = [],
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
            RecipientDispositions = [],
            AssessedAt = at,
        };

    private static MailAssessment WithSignals(MailAssessment assessment, params Evidence[] evidence)
        => assessment with { Evidence = [.. assessment.Evidence, .. evidence] };

    private static Evidence Signal(string signalId, double value) => new()
    {
        SignalId = signalId,
        Origin = EvidenceOrigin.Semantic,
        Availability = EvidenceAvailability.Available,
        Value = value,
        SourceVersion = "test/1",
        ObservedAt = DateTimeOffset.UnixEpoch,
    };

    private static async Task<string> EvidenceJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {(int)response.StatusCode}: {body}");
        return body;
    }

    private static async Task<KpiEvidenceResponse> EvidenceAsync(HttpClient client, string url)
        => await EvidenceAsync<KpiEvidenceResponse>(client, url);

    private static async Task<T> EvidenceAsync<T>(HttpClient client, string url)
        where T : class
    {
        var body = await EvidenceJsonAsync(client, url);
        var value = JsonSerializer.Deserialize<T>(body, HostJson.Options);
        Assert.NotNull(value);
        return value!;
    }

    private static async Task<string> ReferenceJsonAsync(
        HttpClient client,
        string kind,
        string id)
        => await EvidenceJsonAsync(
            client,
            $"/v1/evidence/reference?kind={kind}&id={Uri.EscapeDataString(id)}");
}
