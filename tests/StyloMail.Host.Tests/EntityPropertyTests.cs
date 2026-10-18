using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StyloMail.Host.Contracts;
using StyloMail.Host.Endpoints;
using StyloMail.Host.Serialization;
using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Tests;

/// <summary>
/// Entity properties: effective resolution, provenance, the human actions over them and the audit
/// trail behind both.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests are about what the system will let policy believe</b>, not about JSON shapes. A
/// value that resolves to the wrong scope, or that presents a model guess as a human decision, is a
/// safety defect even when every field is present and every response is 200.
/// </para>
/// <para>
/// Each control is written so that disabling the rule it names makes the test fail, rather than so
/// that it passes on a system where the rule is absent: the unset case is asserted as
/// <c>Unknown</c> rather than as a null, the protective case asserts both directions, and the
/// conversation case asserts the entity read as well as the conversation read.
/// </para>
/// </remarks>
public sealed class EntityPropertyTests
{
    private const string Entity = "user-acme-sender";
    private const string Domain = "example.com";
    private const string Company = "co_acme";
    private const string Conversation = "conv_acme_1";

    // ---------------------------------------------------------------------------------------------
    // Privilege split and tenancy
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reading_properties_needs_review_and_changing_them_needs_administer()
    {
        using var host = new TestHost();

        using var anonymous = host.Anonymous();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/v1/properties/entity/{Entity}")).StatusCode);

        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await sender.GetAsync($"/v1/properties/entity/{Entity}")).StatusCode);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        Assert.Equal(
            HttpStatusCode.OK,
            (await reviewer.GetAsync($"/v1/properties/entity/{Entity}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await reviewer.PutAsJsonAsync(
                $"/v1/properties/entity/{Entity}/age_band",
                new { value = "18_24" })).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await reviewer.PostAsync($"/v1/properties/entity/{Entity}/age_band/confirm", null)).StatusCode);

        using var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        Assert.Equal(
            HttpStatusCode.OK,
            (await operatorClient.PutAsJsonAsync(
                $"/v1/properties/entity/{Entity}/age_band",
                new { value = "18_24" })).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_properties_are_unknown_and_its_history_is_empty()
    {
        using var host = new TestHost();
        using var acme = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await acme.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            new { value = "13_15", reason = "acme set this" }));

        using var globex = host.ClientAs(TestPrincipals.GlobexOperatorKey);

        var read = await ReadAsync(globex, $"/v1/properties/entity/{Entity}?keys=age_band");
        var age = Property(read, PropertyKeys.AgeBand);

        // Nothing, not a refusal and not a filtered row: a caller that names another tenant's scope
        // has named a scope that does not exist for it.
        Assert.Equal(PropertySurfaceState.Unknown, age.State);
        Assert.Null(age.Value);

        var history = await ReadAsync<PropertyHistoryResponse>(
            await globex.GetAsync($"/v1/properties/entity/{Entity}/age_band/history"));

        Assert.Empty(history.Events);

        // And globex's clear at the same identifiers must not reach acme's row.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await globex.DeleteAsync($"/v1/properties/entity/{Entity}/age_band")).StatusCode);

        var reread = await ReadAsync(acme, $"/v1/properties/entity/{Entity}?keys=age_band");
        Assert.Equal("13_15", Property(reread, PropertyKeys.AgeBand).Value);
    }

    // ---------------------------------------------------------------------------------------------
    // The read contract: never a bare value, never a silent absence
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unset_property_reads_as_explicitly_unknown_and_is_still_present()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var read = await ReadAsync(client, $"/v1/properties/entity/{Entity}");

        // All four v1 keys are present even though none has a value. Omitting them would make "not
        // asked" and "nothing recorded" indistinguishable.
        Assert.Equal(PropertyKeys.All.Count, read.Properties.Count);
        Assert.All(read.Properties, property =>
        {
            Assert.Equal(PropertySurfaceState.Unknown, property.State);
            Assert.Null(property.Value);
            Assert.Null(property.Source);
            Assert.Empty(property.Candidates);

            // "Not set" is a named absence, not an empty candidate list the console has to guess at.
            Assert.Equal(AvailabilityState.NotProduced, property.Availability.State);
            Assert.Equal(AvailabilityReasons.NoUnexpiredValue, property.Availability.Reason);
        });
    }

    [Fact]
    public async Task Every_effective_value_carries_its_provenance()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/role",
            new { value = "customer", reason = "known account" }));

        var read = await ReadAsync(client, $"/v1/properties/entity/{Entity}?keys=role");
        var role = Property(read, PropertyKeys.Role);

        Assert.Equal("customer", role.Value);
        Assert.Equal(PropertySurfaceState.Declared, role.State);
        Assert.Equal(PropertySource.Declared, role.Source);
        Assert.Equal(PropertyScopeKind.Entity, role.Scope!.Kind);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, role.SetBy);
        Assert.NotNull(role.SetAt);
        Assert.False(string.IsNullOrEmpty(role.Key));

        // The candidate list shows the binding, not just the winner.
        var candidate = Assert.Single(role.Candidates);
        Assert.True(candidate.Selected);
        Assert.True(candidate.Eligible);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, candidate.Row.SetBy);
    }

    // ---------------------------------------------------------------------------------------------
    // Manual set, confirm, clear, and the audit trail
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Setting_a_property_records_the_actor_and_the_change()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var written = await ReadAsync<PropertyWriteResponse>(
            await client.PutAsJsonAsync(
                $"/v1/properties/entity/{Entity}/age_band",
                new { value = "18_24", reason = "told us in onboarding" }));

        Assert.Equal("18_24", written.Effective.Value);
        Assert.Equal(PropertySurfaceState.Declared, written.Effective.State);

        var history = await ReadAsync<PropertyHistoryResponse>(
            await client.GetAsync($"/v1/properties/entity/{Entity}/age_band/history"));

        var set = Assert.Single(history.Events);
        Assert.Equal(PropertyAuditAction.Set, set.Action);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, set.Actor);
        Assert.Null(set.OldValue);
        Assert.Equal("18_24", set.NewValue);
        Assert.Equal("told us in onboarding", set.Reason);
    }

    [Fact]
    public async Task An_imported_country_is_declared_and_history_names_the_import_principal()
    {
        using var host = new TestHost();
        var importKey = host.MintKey("import:hr-system", TestPrincipals.AcmeTenant, "Administer");

        using var importer = host.ClientAs(importKey);

        // Country has no import source: an import writes as declared with an identifiable actor and
        // a reason naming the system, so jurisdiction traces to something accountable.
        await AssertSuccess(await importer.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/country",
            new
            {
                value = "gb",
                source = "declared",
                reason = "Imported from the HR system of record.",
            }));

        // The import principal may write but need not read, so the assertions use a reviewer.
        using var reader = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var read = await ReadAsync(reader, $"/v1/properties/entity/{Entity}?keys=country");
        var country = Property(read, PropertyKeys.Country);

        Assert.Equal("GB", country.Value);
        Assert.Equal(PropertySurfaceState.Declared, country.State);
        Assert.Equal("import:hr-system", country.SetBy);

        var history = await ReadAsync<PropertyHistoryResponse>(
            await reader.GetAsync($"/v1/properties/entity/{Entity}/country/history"));

        var set = Assert.Single(history.Events);
        Assert.Equal("import:hr-system", set.Actor);
        Assert.Equal("GB", set.NewValue);
    }

    [Fact]
    public async Task Confirming_an_imported_value_changes_the_pill_and_not_the_source()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/role",
            new { value = "vendor", source = "import", reason = "system of record" }));

        var before = await ReadAsync(client, $"/v1/properties/entity/{Entity}?keys=role");
        Assert.Equal(PropertySurfaceState.Suggested, Property(before, PropertyKeys.Role).State);

        var confirmed = await ReadAsync<PropertyWriteResponse>(
            await client.PostAsync(
                $"/v1/properties/entity/{Entity}/role/confirm",
                JsonContent.Create(new { reason = "operator checked the contract" })));

        Assert.Equal(PropertySurfaceState.Declared, confirmed.Effective.State);
        Assert.Equal(PropertySource.Import, confirmed.Effective.Source);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, confirmed.Effective.ConfirmedBy);

        var history = await ReadAsync<PropertyHistoryResponse>(
            await client.GetAsync($"/v1/properties/entity/{Entity}/role/history"));

        Assert.Contains(
            history.Events,
            entry => entry.Action == PropertyAuditAction.Confirm
                && entry.NewConfirmedBy == TestPrincipals.AcmeOperatorPrincipal
                && entry.OldConfirmedBy is null);
    }

    [Fact]
    public async Task Confirmation_is_never_inferred_from_an_inherited_value()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        // A value at the company scope. The entity has no row of its own.
        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/company/{Company}/age_band",
            new { value = "25_plus" }));

        // Confirming at the entity scope must not copy the inherited value down and call it a human
        // decision about the entity.
        var response = await client.PostAsync($"/v1/properties/entity/{Entity}/age_band/confirm", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("property_not_set", await ErrorCode(response));

        var read = await ReadAsync(
            client,
            $"/v1/properties/entity/{Entity}?company={Company}&keys=age_band");

        var age = Property(read, PropertyKeys.AgeBand);
        Assert.Equal("25_plus", age.Value);
        Assert.Equal(PropertyScopeKind.Company, age.Scope!.Kind);
        Assert.Null(age.ConfirmedBy);
    }

    [Fact]
    public async Task Replacing_a_confirmed_value_drops_the_confirmation_with_the_old_value()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/role",
            new { value = "vendor", reason = "operator first pass" }));
        await AssertSuccess(await client.PostAsync(
            $"/v1/properties/entity/{Entity}/role/confirm",
            JsonContent.Create(new { reason = "checked" })));

        // A confirmation is about the value a human saw, not about the slot it occupied. Replacing
        // the value must not inherit the old human authority.
        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/role",
            new { value = "customer", source = "import", reason = "system of record changed" }));

        var read = await ReadAsync(client, $"/v1/properties/entity/{Entity}?keys=role");
        var role = Property(read, PropertyKeys.Role);

        Assert.Equal("customer", role.Value);
        Assert.Equal(PropertySurfaceState.Suggested, role.State);
        Assert.Null(role.ConfirmedBy);
        Assert.Null(role.ConfirmedAt);
    }

    [Fact]
    public async Task Clearing_returns_the_effective_value_to_the_next_scope_and_keeps_the_history()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/company/{Company}/age_band",
            new { value = "25_plus" }));
        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            new { value = "16_17", reason = "operator override" }));

        var cleared = await ReadAsync<PropertyWriteResponse>(
            await client.DeleteAsync($"/v1/properties/entity/{Entity}/age_band"));

        Assert.Null(cleared.Stored);

        var read = await ReadAsync(
            client,
            $"/v1/properties/entity/{Entity}?company={Company}&keys=age_band");

        var age = Property(read, PropertyKeys.AgeBand);
        Assert.Equal("25_plus", age.Value);
        Assert.Equal(PropertyScopeKind.Company, age.Scope!.Kind);

        var history = await ReadAsync<PropertyHistoryResponse>(
            await client.GetAsync($"/v1/properties/entity/{Entity}/age_band/history"));

        var clear = Assert.Single(history.Events, entry => entry.Action == PropertyAuditAction.Clear);
        Assert.Equal("16_17", clear.OldValue);
        Assert.Null(clear.NewValue);
        Assert.Equal("operator override", history.Events.Single(entry => entry.Action == PropertyAuditAction.Set).Reason);
    }

    [Fact]
    public async Task Clearing_a_scope_with_nothing_set_is_refused_rather_than_reported_as_done()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var response = await client.DeleteAsync($"/v1/properties/entity/{Entity}/age_band");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("property_not_set", await ErrorCode(response));
    }

    // ---------------------------------------------------------------------------------------------
    // Precedence
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_verified_value_outranks_a_declared_one_even_from_a_wider_scope()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/domain/{Domain}/age_band",
            new { value = "25_plus", source = "declared" }));
        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            new { value = "25_plus", source = "verified" }));

        var read = await ReadAsync(
            client,
            $"/v1/properties/entity/{Entity}?domain={Domain}&keys=age_band");

        var age = Property(read, PropertyKeys.AgeBand);
        Assert.Equal(PropertySource.Verified, age.Source);
        Assert.Equal(PropertyScopeKind.Entity, age.Scope!.Kind);
        Assert.Equal(PropertySurfaceState.Verified, age.State);
    }

    [Fact]
    public async Task A_narrower_scope_breaks_a_tie_between_equal_sources()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/domain/{Domain}/role",
            new { value = "vendor" }));
        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/role",
            new { value = "customer" }));

        var read = await ReadAsync(
            client,
            $"/v1/properties/entity/{Entity}?domain={Domain}&keys=role");

        var role = Property(read, PropertyKeys.Role);
        Assert.Equal("customer", role.Value);
        Assert.Equal(PropertyScopeKind.Entity, role.Scope!.Kind);
    }

    [Fact]
    public async Task A_domain_scope_is_matched_regardless_of_case()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        // A stored "Example.COM" beside "example.com" would be two bindings for one domain, and an
        // override that silently failed to apply.
        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/domain/{Domain.ToUpperInvariant()}/role",
            new { value = "vendor" }));

        var read = await ReadAsync(
            client,
            $"/v1/properties/entity/{Entity}?domain={Domain}&keys=role");

        var role = Property(read, PropertyKeys.Role);
        Assert.Equal("vendor", role.Value);
        Assert.Equal(Domain, role.Scope!.Id);
    }

    [Fact]
    public async Task A_conversation_override_never_leaks_up_to_the_entity()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/role",
            new { value = "vendor" }));
        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/conversation/{Conversation}/role",
            new { value = "customer", reason = "this thread is a customer enquiry" }));

        var inConversation = await ReadAsync(
            client,
            $"/v1/properties/conversation/{Conversation}?entity={Entity}&keys=role");

        var conversationRole = Property(inConversation, PropertyKeys.Role);
        Assert.Equal("customer", conversationRole.Value);
        Assert.Equal(PropertyScopeKind.Conversation, conversationRole.Scope!.Kind);

        var atEntity = await ReadAsync(client, $"/v1/properties/entity/{Entity}?keys=role");
        var entityRole = Property(atEntity, PropertyKeys.Role);

        Assert.Equal("vendor", entityRole.Value);
        Assert.Equal(PropertyScopeKind.Entity, entityRole.Scope!.Kind);
    }

    [Fact]
    public async Task A_conversation_cannot_be_consulted_from_a_wider_read()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var response = await client.GetAsync($"/v1/properties/domain/{Domain}?entity={Entity}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("scope_not_ancestor", await ErrorCode(response));
    }

    // ---------------------------------------------------------------------------------------------
    // Protective precedence, and what an inference may not do
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_inferred_minor_beats_a_declared_adult()
    {
        using var host = new TestHost().Configure("StyloMail:Properties:AllowInferredWrites", "true");
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            new { value = "25_plus", source = "declared" }));

        await AssertSuccess(await InferredAgeAsync(client, Domain, "13_15", 0.55));

        var read = await ReadAsync(
            client,
            $"/v1/properties/entity/{Entity}?domain={Domain}&keys=age_band");

        var age = Property(read, PropertyKeys.AgeBand);

        // Protective precedence, not confidence: the minor band wins even though it is a guess and
        // the adult band is a human declaration.
        Assert.Equal("13_15", age.Value);
        Assert.Equal(PropertySource.Inferred, age.Source);
        Assert.Equal(PropertyScopeKind.Domain, age.Scope!.Kind);
        Assert.Equal(PropertySurfaceState.Suggested, age.State);
    }

    [Fact]
    public async Task An_inferred_adult_cannot_lower_a_declared_minor()
    {
        using var host = new TestHost().Configure("StyloMail:Properties:AllowInferredWrites", "true");
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            new { value = "13_15", source = "declared" }));

        await AssertSuccess(await InferredAgeAsync(client, Domain, "25_plus", 0.95));

        var read = await ReadAsync(
            client,
            $"/v1/properties/entity/{Entity}?domain={Domain}&keys=age_band");

        var age = Property(read, PropertyKeys.AgeBand);
        Assert.Equal("13_15", age.Value);
        Assert.Equal(PropertyScopeKind.Entity, age.Scope!.Kind);
        Assert.Equal(PropertySurfaceState.Declared, age.State);
    }

    [Fact]
    public async Task An_inference_cannot_override_a_more_protective_default_hard_floor()
    {
        using var host = new TestHost().Configure("StyloMail:Properties:AllowInferredWrites", "true");
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        // A deployment whose default is a hard floor. A model guess must not switch it off.
        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/company/{Company}/age_band",
            new { value = "under_13", source = "default" }));
        await AssertSuccess(await InferredAgeAsync(client, Entity, "25_plus", 0.99));

        var read = await ReadAsync(
            client,
            $"/v1/properties/entity/{Entity}?company={Company}&keys=age_band");

        var age = Property(read, PropertyKeys.AgeBand);
        Assert.Equal("under_13", age.Value);
        Assert.Equal(PropertyScopeKind.Company, age.Scope!.Kind);
        Assert.Equal(PropertySource.Default, age.Source);
    }

    [Fact]
    public async Task Country_refuses_an_inferred_value_even_when_inference_is_enabled()
    {
        using var host = new TestHost().Configure("StyloMail:Properties:AllowInferredWrites", "true");
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var response = await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/country",
            new
            {
                value = "gb",
                source = "inferred",
                confidence = 0.8,
                model = new
                {
                    id = "nimble-country",
                    version = "1",
                    promptContract = "country/v1",
                    spans = HeaderSpan,
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("source_not_permitted", await ErrorCode(response));
    }

    [Fact]
    public async Task An_inferred_write_is_refused_unless_the_deployment_enabled_it()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var response = await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            InferredAgeBody("13_15", 0.6));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("inference_not_enabled", await ErrorCode(response));
    }

    [Fact]
    public async Task An_inferred_value_must_name_its_model_version_contract_and_confidence()
    {
        using var host = new TestHost().Configure("StyloMail:Properties:AllowInferredWrites", "true");
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var noModel = await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            new { value = "13_15", source = "inferred", confidence = 0.6 });
        Assert.Equal("model_contribution_required", await ErrorCode(noModel));

        var noConfidence = await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            new
            {
                value = "13_15",
                source = "inferred",
                model = new { id = "m", version = "1", promptContract = "age/v1" },
            });
        Assert.Equal("confidence_required", await ErrorCode(noConfidence));
    }

    [Fact]
    public async Task An_inferred_write_without_coverage_is_refused()
    {
        using var host = new TestHost().Configure("StyloMail:Properties:AllowInferredWrites", "true");
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var response = await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            new
            {
                value = "13_15",
                source = "inferred",
                confidence = 0.6,
                model = new { id = "m", version = "1", promptContract = "age/v1" },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("coverage_required", await ErrorCode(response));
    }

    [Fact]
    public async Task A_declared_value_cannot_claim_a_sample()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var response = await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/role",
            new
            {
                value = "vendor",
                coverage = new { analysed = 9, total = 12 },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("coverage_not_applicable", await ErrorCode(response));
    }

    [Fact]
    public async Task A_declared_value_reports_no_model_and_no_denominator_rather_than_zero()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/role",
            new { value = "vendor" }));

        var read = await ReadAsync(client, $"/v1/properties/entity/{Entity}?keys=role");
        var role = Property(read, PropertyKeys.Role);

        Assert.Equal(AvailabilityState.Available, role.Availability.State);

        // Not "not produced": a human declaration has no model contribution and no sample to give.
        Assert.Equal(AvailabilityState.NotApplicable, role.ModelAvailability.State);
        Assert.Equal(AvailabilityReasons.SourceIsNotModelDerived, role.ModelAvailability.Reason);
        Assert.Equal(CoverageState.NotApplicable, role.Coverage.State);
        Assert.Equal(AvailabilityReasons.SourceIsNotEvidenceDerived, role.Coverage.Reason);
        Assert.Null(role.Coverage.Analysed);
    }

    [Fact]
    public async Task Evidence_metadata_round_trips_without_content()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/role",
            new
            {
                value = "vendor",
                evidenceRefs = new object[]
                {
                    new
                    {
                        id = "msg_abc",
                        kind = "Message",
                        label = "Message received 2026-10-01",
                        observedAt = CoverageEnd,
                        retention = "Scheduled",
                        retentionUntil = CoverageEnd.AddDays(30),
                        accessOutcome = "Withheld",
                    },
                },
            }));

        var read = await ReadAsync(client, $"/v1/properties/entity/{Entity}?keys=role");
        var reference = Assert.Single(Property(read, PropertyKeys.Role).EvidenceRefs);

        Assert.Equal("msg_abc", reference.Id);
        Assert.Equal(EvidenceKind.Message, reference.Kind);
        Assert.Equal("Message received 2026-10-01", reference.Label);
        Assert.Equal(CoverageEnd, reference.ObservedAt);
        Assert.Equal(EvidenceRetention.Scheduled, reference.Retention);
        Assert.Equal(CoverageEnd.AddDays(30), reference.RetentionUntil);

        // Withheld is reported as stated. The property store does not evaluate access and must not
        // upgrade an unevaluated reference to "available".
        Assert.Equal(EvidenceAccessOutcome.Withheld, reference.AccessOutcome);
    }

    [Fact]
    public async Task An_inferred_row_exposes_model_version_contract_confidence_and_spans()
    {
        using var host = new TestHost().Configure("StyloMail:Properties:AllowInferredWrites", "true");
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await InferredAgeAsync(client, Entity, "16_17", 0.42));

        var read = await ReadAsync(client, $"/v1/properties/entity/{Entity}?keys=age_band");
        var age = Property(read, PropertyKeys.AgeBand);

        Assert.Equal(0.42, age.Confidence!.Value, 3);
        Assert.NotNull(age.Model);
        Assert.Equal("nimble-age", age.Model!.Id);
        Assert.Equal("2026-10-01", age.Model.Version);
        Assert.Equal("age-band/v1", age.Model.PromptContract);
        Assert.Equal(AgeSpan, age.Model.Spans);

        Assert.Equal(AvailabilityState.Available, age.ModelAvailability.State);

        var evidence = Assert.Single(age.EvidenceRefs);
        Assert.Equal("msg_123", evidence.Id);
        Assert.Equal(EvidenceKind.Message, evidence.Kind);
        Assert.Equal("Submission msg_123", evidence.Label);
        Assert.Equal(EvidenceRetention.Retained, evidence.Retention);
        Assert.Equal(EvidenceAccessOutcome.NotEvaluated, evidence.AccessOutcome);

        Assert.Equal(CoverageState.Available, age.Coverage.State);
        Assert.Equal(9, age.Coverage.Analysed);
        Assert.Equal(12, age.Coverage.Total);
        Assert.Equal(CoverageStart, age.Coverage.WindowStart);
        Assert.Equal(CoverageEnd, age.Coverage.WindowEnd);

        Assert.Equal(PropertySurfaceState.Suggested, age.State);
    }

    // ---------------------------------------------------------------------------------------------
    // Expiry
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_expired_value_falls_back_to_the_next_scope_and_history_keeps_both_events()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/company/{Company}/age_band",
            new { value = "25_plus" }));

        var expires = host.Clock.GetUtcNow().AddHours(1);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            new { value = "13_15", expiresAt = expires }));

        host.Clock.Advance(TimeSpan.FromHours(2));

        var read = await ReadAsync(
            client,
            $"/v1/properties/entity/{Entity}?company={Company}&keys=age_band");

        var age = Property(read, PropertyKeys.AgeBand);
        Assert.Equal("25_plus", age.Value);
        Assert.Equal(PropertyScopeKind.Company, age.Scope!.Kind);

        var history = await ReadAsync<PropertyHistoryResponse>(
            await client.GetAsync($"/v1/properties/entity/{Entity}/age_band/history"));

        var expire = Assert.Single(history.Events, entry => entry.Action == PropertyAuditAction.Expire);
        Assert.Equal("13_15", expire.OldValue);
        Assert.Null(expire.NewValue);
        Assert.Contains(history.Events, entry => entry.Action == PropertyAuditAction.Set);
    }

    [Fact]
    public async Task A_confirmed_time_bound_value_expires_and_the_confirm_and_expire_both_remain_readable()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/company/{Company}/age_band",
            new { value = "18_24" }));

        var expires = host.Clock.GetUtcNow().AddHours(1);
        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            new { value = "16_17", expiresAt = expires }));
        await AssertSuccess(await client.PostAsync(
            $"/v1/properties/entity/{Entity}/age_band/confirm",
            JsonContent.Create(new { reason = "checked against the account record" })));

        host.Clock.Advance(TimeSpan.FromHours(2));

        var read = await ReadAsync(
            client,
            $"/v1/properties/entity/{Entity}?company={Company}&keys=age_band");

        var age = Property(read, PropertyKeys.AgeBand);
        Assert.Equal("18_24", age.Value);
        Assert.Equal(PropertyScopeKind.Company, age.Scope!.Kind);

        var history = await ReadAsync<PropertyHistoryResponse>(
            await client.GetAsync($"/v1/properties/entity/{Entity}/age_band/history"));

        Assert.Contains(history.Events, entry => entry.Action == PropertyAuditAction.Confirm
            && entry.NewConfirmedBy == TestPrincipals.AcmeOperatorPrincipal);
        Assert.Contains(history.Events, entry => entry.Action == PropertyAuditAction.Expire
            && entry.OldValue == "16_17"
            && entry.OldConfirmedBy == TestPrincipals.AcmeOperatorPrincipal);
    }

    [Fact]
    public async Task Expiry_returns_an_unshadowed_value_to_unknown()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var expires = host.Clock.GetUtcNow().AddHours(1);
        await AssertSuccess(await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/language",
            new { value = "en-GB", expiresAt = expires }));

        host.Clock.Advance(TimeSpan.FromHours(2));

        var read = await ReadAsync(client, $"/v1/properties/entity/{Entity}?keys=language");
        var language = Property(read, PropertyKeys.Language);

        Assert.Equal(PropertySurfaceState.Unknown, language.State);
        Assert.Null(language.Value);

        var history = await ReadAsync<PropertyHistoryResponse>(
            await client.GetAsync($"/v1/properties/entity/{Entity}/language/history"));

        Assert.Contains(history.Events, entry => entry.Action == PropertyAuditAction.Expire);
    }

    // ---------------------------------------------------------------------------------------------
    // Refusals
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_value_the_key_does_not_understand_is_refused_by_name()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var badValue = await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/age_band",
            new { value = "toddler" });
        Assert.Equal(HttpStatusCode.BadRequest, badValue.StatusCode);
        Assert.Equal("invalid_property_value", await ErrorCode(badValue));

        var badKey = await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/favourite_colour",
            new { value = "blue" });
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);
        Assert.Equal("unknown_property_key", await ErrorCode(badKey));

        var badScope = await client.GetAsync("/v1/properties/planet/mars");
        Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);
        Assert.Equal("unknown_scope", await ErrorCode(badScope));

        var badSource = await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/role",
            new { value = "vendor", source = "vibes" });
        Assert.Equal(HttpStatusCode.BadRequest, badSource.StatusCode);
        Assert.Equal("unknown_source", await ErrorCode(badSource));

        var pastExpiry = await client.PutAsJsonAsync(
            $"/v1/properties/entity/{Entity}/role",
            new { value = "vendor", expiresAt = host.Clock.GetUtcNow().AddDays(-1) });
        Assert.Equal(HttpStatusCode.BadRequest, pastExpiry.StatusCode);
        Assert.Equal("expiry_not_in_future", await ErrorCode(pastExpiry));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly string[] AgeSpan = ["body:120-180"];
    private static readonly string[] HeaderSpan = ["header:received"];
    private static readonly DateTimeOffset CoverageStart =
        new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CoverageEnd =
        new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static object InferredAgeBody(string value, double confidence) => new
    {
        value,
        source = "inferred",
        confidence,
        model = new
        {
            id = "nimble-age",
            version = "2026-10-01",
            promptContract = "age-band/v1",
            spans = AgeSpan,
        },
        evidenceRefs = new object[]
        {
            new
            {
                id = "msg_123",
                kind = "Message",
                label = "Submission msg_123",
                observedAt = CoverageEnd,
                retention = "Retained",
                retentionUntil = (DateTimeOffset?)null,
                accessOutcome = "NotEvaluated",
            },
        },
        coverage = new
        {
            analysed = 9,
            total = 12,
            windowStart = CoverageStart,
            windowEnd = CoverageEnd,
        },
    };

    private static async Task<HttpResponseMessage> InferredAgeAsync(
        HttpClient client,
        string scopeId,
        string value,
        double confidence)
    {
        var scope = scopeId == Entity ? "entity" : "domain";
        return await client.PutAsJsonAsync(
            $"/v1/properties/{scope}/{scopeId}/age_band",
            InferredAgeBody(value, confidence));
    }

    private static async Task AssertSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        Assert.Fail(
            $"Expected success, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<PropertyReadResponse> ReadAsync(HttpClient client, string url)
        => await ReadAsync<PropertyReadResponse>(await client.GetAsync(url));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
        where T : class
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {(int)response.StatusCode}: {body}");

        var value = JsonSerializer.Deserialize<T>(body, HostJson.Options);
        Assert.NotNull(value);
        return value!;
    }

    private static PropertyResponse Property(PropertyReadResponse read, string key)
        => Assert.Single(read.Properties, property => property.Key == key);

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ErrorResponse>(body, HostJson.Options)?.Error;
    }
}
