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
        Assert.Equal("asm-ref-1", evidence.Reference.Id);
        Assert.Equal(EvidenceKind.Message, evidence.Reference.Kind);
        Assert.Equal("Submission asm-ref-1", evidence.Reference.Label);

        var citation = Assert.Single(evidence.Citations);
        Assert.Equal(PropertyScopeKind.Entity, citation.Scope.Kind);
        Assert.Equal(PropertyKeys.Role, citation.Key);
        Assert.Equal("vendor", citation.Value);

        Assert.NotNull(evidence.Source);
        Assert.Equal("asm-ref-1", evidence.Source!.AssessmentId);
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

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static async Task SetReferenceAsync(
        TestHost host,
        DateTimeOffset at,
        EvidenceReference reference)
    {
        await host.Services.GetRequiredService<IPropertyStore>().SetAsync(
            Acme,
            new PropertyWrite
            {
                Scope = new PropertyScope(PropertyScopeKind.Entity, Principal),
                Key = PropertyKeys.Role,
                Value = "vendor",
                Source = PropertySource.Declared,
                Actor = "test",
                EvidenceRefs = [reference],
            },
            at,
            CancellationToken.None);
    }

    private static async Task SeedAsync(TestHost host, MailAssessment assessment)
    {
        await host.Services.GetRequiredService<IDecisionLedger>()
            .RecordAsync(assessment, CancellationToken.None);

        var queue = host.Services.GetRequiredService<QueueStore>();
        var accepted = await queue.AcceptAsync(
            new QueueSubmission
            {
                TenantId = assessment.TenantId,
                InternalMessageId = assessment.InternalMessageId,
                Direction = MailDirection.Inbound,
                TrustedPrincipalId = Principal,
                MailFrom = "sender@example.test",
                MimeDigest = "digest-" + Guid.NewGuid().ToString("N"),
                Payload = Encoding.UTF8.GetBytes("Subject: evidence\r\n\r\nbody"),
                Recipients = [new RecipientAdmission { Recipient = "owner@acme.test" }],
                HopCount = 0,
            },
            CancellationToken.None);

        Assert.True(accepted.IsAccepted, accepted.Detail);
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
