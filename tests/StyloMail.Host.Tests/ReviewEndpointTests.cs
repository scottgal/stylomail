using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Decisions;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// The review surface: listing, resolution, tenancy and the deadline path.
/// </summary>
public sealed class ReviewEndpointTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_review_lists_with_its_policy_record_and_deadline()
    {
        using var host = new TestHost().WithClock();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        await SeedAsync(host, policy: true);

        var page = await ReadAsync<ReviewListResponse>(await reviewer.GetAsync("/v1/policy/reviews"));

        var item = Assert.Single(page.Items);
        var policy = item.Policy;
        Assert.NotNull(policy);
        Assert.Equal("pack-commerce", policy!.PackId);
        Assert.Equal("rule-payment-redirection", policy.DecidingRuleId);
        Assert.True(policy.Shadow);
        Assert.Equal(Start.AddHours(1), item.Recipients[0].ReviewDeadline);
        Assert.False(item.Recipients[0].Overdue);
    }

    [Fact]
    public async Task A_null_policy_record_is_no_pack_not_permit()
    {
        using var host = new TestHost().WithClock();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        await SeedAsync(host, policy: false);

        var page = await ReadAsync<ReviewListResponse>(await reviewer.GetAsync("/v1/policy/reviews"));

        var item = Assert.Single(page.Items);
        Assert.Null(item.Policy);
        Assert.False(item.PolicyRecordMissing);
    }

    [Fact]
    public async Task Releasing_a_review_requires_a_reason_and_records_the_actor_and_event()
    {
        using var host = new TestHost().WithClock();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var queueId = await SeedAsync(host, policy: true);

        var missingReason = await reviewer.PostAsync(
            $"/v1/policy/reviews/{queueId}/release",
            JsonContent.Create(new ReviewResolutionRequest(), options: HostJson.Options));
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);
        Assert.Contains("reason_required", await missingReason.Content.ReadAsStringAsync());

        var released = await ReadAsync<ReviewDetailResponse>(await reviewer.PostAsync(
            $"/v1/policy/reviews/{queueId}/release",
            JsonContent.Create(new ReviewResolutionRequest { Reason = "recipient confirmed it" }, options: HostJson.Options)));

        Assert.Equal(DeliveryState.Queued, released.Item.Recipients[0].State);
        var audit = Assert.Single(released.Events, @event => @event.Event == "review.released");
        Assert.Equal(TestPrincipals.AcmeReviewerPrincipal, audit.Actor);
        Assert.Equal("recipient confirmed it", audit.Reason);
        Assert.Equal(
            "pack-commerce@1.0.0/rules-commerce@rules/1/rule-payment-redirection@1",
            audit.PolicyReference);
    }

    [Fact]
    public async Task Reject_requires_administer_while_quarantine_stays_review()
    {
        using var host = new TestHost().WithClock();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        using var administrator = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var review = await SeedAsync(host, policy: true);
        var other = await SeedAsync(host, policy: true);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await reviewer.PostAsync(
                $"/v1/policy/reviews/{review}/reject",
                JsonContent.Create(new ReviewResolutionRequest { Reason = "phishing" }, options: HostJson.Options))).StatusCode);

        var rejected = await ReadAsync<ReviewDetailResponse>(await administrator.PostAsync(
            $"/v1/policy/reviews/{review}/reject",
            JsonContent.Create(new ReviewResolutionRequest { Reason = "phishing" }, options: HostJson.Options)));
        Assert.Equal(DeliveryState.TerminalFailure, rejected.Item.Recipients[0].State);

        var quarantined = await ReadAsync<ReviewDetailResponse>(await reviewer.PostAsync(
            $"/v1/policy/reviews/{other}/quarantine",
            JsonContent.Create(new ReviewResolutionRequest { Reason = "suspicious" }, options: HostJson.Options)));
        Assert.Equal(DeliveryState.Quarantined, quarantined.Item.Recipients[0].State);
    }

    [Fact]
    public async Task Another_tenant_cannot_list_or_resolve_a_review()
    {
        using var host = new TestHost().WithClock();
        using var globex = host.ClientAs(TestPrincipals.GlobexReviewerKey);
        var queueId = await SeedAsync(host, policy: true);

        var page = await ReadAsync<ReviewListResponse>(await globex.GetAsync("/v1/policy/reviews"));
        Assert.Empty(page.Items);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await globex.GetAsync($"/v1/policy/reviews/{queueId}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await globex.PostAsync(
                $"/v1/policy/reviews/{queueId}/release",
                JsonContent.Create(new ReviewResolutionRequest { Reason = "wrong tenant" }, options: HostJson.Options))).StatusCode);
    }

    [Fact]
    public async Task Reading_reviews_needs_review_and_a_sender_is_refused()
    {
        using var host = new TestHost().WithClock();

        using var anonymous = host.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/policy/reviews")).StatusCode);

        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await sender.GetAsync("/v1/policy/reviews")).StatusCode);
    }

    [Fact]
    public async Task An_overdue_review_is_flagged_and_the_sweep_quarantines_it()
    {
        using var host = new TestHost().WithClock();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var queueId = await SeedAsync(host, policy: true);

        host.Clock.Advance(TimeSpan.FromHours(2));

        var overdue = await ReadAsync<ReviewDetailResponse>(
            await reviewer.GetAsync($"/v1/policy/reviews/{queueId}"));
        Assert.True(overdue.Item.Recipients[0].Overdue);

        var queue = host.Services.GetRequiredService<QueueStore>();
        Assert.Equal(1, await queue.SweepReviewDeadlinesAsync());

        var after = await ReadAsync<ReviewDetailResponse>(
            await reviewer.GetAsync($"/v1/policy/reviews/{queueId}"));
        Assert.Equal(DeliveryState.Quarantined, after.Item.Recipients[0].State);
        var expired = Assert.Single(after.Events, @event => @event.Event == "review.expired");
        Assert.Equal("policy-scheduler", expired.Actor);
        Assert.NotNull(after.Item.Opening);
        Assert.Equal("pack-commerce", after.Item.Opening!.PackId);
        Assert.Equal("rule-payment-redirection", after.Item.Opening.RuleId);
    }

    [Fact]
    public async Task A_pruned_ledger_row_leaves_the_opening_reference_readable()
    {
        using var host = new TestHost().WithClock();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var queueId = await SeedAsync(host, policy: true);

        var queue = host.Services.GetRequiredService<QueueStore>();
        var opening = await queue.GetReviewOpeningAsync(TestPrincipals.AcmeTenant, queueId);
        Assert.NotNull(opening);

        var database = host.Services.GetRequiredService<HostDatabase>();
        using (var connection = database.Open())
        {
            using var delete = connection.CreateCommand();
            delete.CommandText =
                "DELETE FROM host_decision_ledger WHERE tenant_id = $tenant AND assessment_id = $assessment;";
            delete.Parameters.AddWithValue("$tenant", TestPrincipals.AcmeTenant);
            delete.Parameters.AddWithValue("$assessment", opening!.Opening.AssessmentId);
            Assert.Equal(1, delete.ExecuteNonQuery());
        }

        var detail = await ReadAsync<ReviewDetailResponse>(
            await reviewer.GetAsync($"/v1/policy/reviews/{queueId}"));

        Assert.Null(detail.Item.Policy);
        Assert.True(detail.Item.PolicyRecordMissing);
        Assert.False(detail.Item.LegacyReviewOpeningMissing);
        Assert.NotNull(detail.Item.Opening);
        Assert.Equal("pack-commerce", detail.Item.Opening!.PackId);
        Assert.Equal("rule-payment-redirection", detail.Item.Opening.RuleId);
        Assert.Equal("gray zone", detail.Item.Opening.Reason);
    }

    [Fact]
    public async Task The_review_keeps_the_opening_decision_after_a_reassessment()
    {
        using var host = new TestHost().WithClock();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var queueId = await SeedAsync(host, policy: true);

        var queue = host.Services.GetRequiredService<QueueStore>();
        var ledger = host.Services.GetRequiredService<IDecisionLedger>();
        var item = await queue.GetItemForTenantAsync(queueId, TestPrincipals.AcmeTenant);
        Assert.NotNull(item);

        // A later assessment of the same message must not rewrite why the review was opened.
        await ledger.RecordAsync(
            Assessment(
                TestPrincipals.AcmeTenant,
                "asm-newer",
                item!.Envelope.InternalMessageId,
                withPolicy: true) with
            {
                Action = MailAction.Allow,
                Policy = PolicyRecord() with
                {
                    PackId = "pack-newer",
                    PackVersion = "9.0.0",
                    Verdict = PolicyVerdict.Permit,
                    RequiresHumanReview = false,
                },
            },
            CancellationToken.None);

        var detail = await ReadAsync<ReviewDetailResponse>(
            await reviewer.GetAsync($"/v1/policy/reviews/{queueId}"));

        Assert.NotNull(detail.Item.Opening);
        Assert.Equal("pack-commerce", detail.Item.Opening!.PackId);
        Assert.Equal("pack-commerce", detail.Item.Policy!.PackId);
        Assert.Equal("rule-payment-redirection", detail.Item.Opening.RuleId);
        Assert.Equal("gray zone", detail.Item.Opening.Reason);
        Assert.False(detail.Item.LegacyReviewOpeningMissing);
        Assert.False(detail.Item.PolicyRecordMissing);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static async Task<string> SeedAsync(TestHost host, bool policy)
    {
        var queue = host.Services.GetRequiredService<QueueStore>();
        var ledger = host.Services.GetRequiredService<IDecisionLedger>();
        var messageId = "msg-" + Guid.NewGuid().ToString("N");
        var assessmentId = "asm-" + Guid.NewGuid().ToString("N");

        await ledger.RecordAsync(
            Assessment(TestPrincipals.AcmeTenant, assessmentId, messageId, policy),
            CancellationToken.None);

        var accepted = await queue.AcceptAsync(new QueueSubmission
        {
            TenantId = TestPrincipals.AcmeTenant,
            InternalMessageId = messageId,
            Direction = MailDirection.Inbound,
            TrustedPrincipalId = "principal-1",
            MailFrom = "sender@example.test",
            MimeDigest = "digest-" + Guid.NewGuid().ToString("N"),
            Payload = Encoding.UTF8.GetBytes("Subject: review\r\n\r\nbody"),
            Recipients =
            [
                new RecipientAdmission
                {
                    Recipient = "user@acme.test",
                    State = DeliveryState.Review,
                    ReviewDeadline = Start.AddHours(1),
                },
            ],
            HopCount = 0,
            ReviewOpening = Opening(assessmentId),
        });

        Assert.True(accepted.IsAccepted, accepted.Detail);
        return accepted.QueueId!;
    }

    private static MailAssessment Assessment(
        string tenantId,
        string assessmentId,
        string messageId,
        bool withPolicy) => new()
        {
            AssessmentId = assessmentId,
            InternalMessageId = messageId,
            TenantId = tenantId,
            Channel = ChannelContext.Email,
            Evidence = [],
            RiskDimensions = [],
            RiskIndex = 0,
            RiskIndexDenominator = 0.0,
            CoveredWeightFraction = 1.0,
            Action = MailAction.Review,
            DeliveryTiming = DeliveryTiming.PreAcceptance,
            Reasons = [],
            Versions = new AssessmentVersions
            {
                PolicyVersion = "policy/1",
                QuestionSchemaVersion = "q/1",
                PreprocessingVersion = "p/1",
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
                ConversationContextMissing = false,
            },
            RecipientDispositions = [],
            AssessedAt = Start,
            Policy = withPolicy ? PolicyRecord() : null,
        };

    private static ReviewOpening Opening(string assessmentId) => new()
    {
        AssessmentId = assessmentId,
        PackId = "pack-commerce",
        PackVersion = "1.0.0",
        RuleSetId = "rules-commerce",
        RuleSetVersion = "rules/1",
        RuleId = "rule-payment-redirection",
        RuleVersion = 1,
        ReasonCode = "policy.payment_redirection_review",
        Reason = "gray zone",
        EvidenceSnapshotDigest = new string('a', 64),
        DecidedAt = Start,
    };

    private static PolicyDecisionRecord PolicyRecord() => new()
    {
        Source = PolicySource.Pack,
        Admission = AdmissionOutcome.Admitted,
        PackId = "pack-commerce",
        PackVersion = "1.0.0",
        RuleSetId = "rules-commerce",
        RuleSetVersion = "rules/1",
        ScorerVersion = "scorer/1",
        EvaluatorVersion = "deterministic-policy-evaluator/1",
        Verdict = PolicyVerdict.Review,
        RequiresHumanReview = true,
        DecidingRuleId = "rule-payment-redirection",
        DecidingRuleVersion = 1,
        DecidingThreshold = 0.35,
        DecidingReasonCode = "policy.payment_redirection_review",
        DecidingReason = "gray zone",
        MissingDimensions = [],
        Coverage = 1,
        BindingIds = [],
        PropertyFacts = [],
        MissingPropertyKeys = [],
        EvidenceSnapshotDigest = new string('a', 64),
        EvaluatedAt = Start,
        Shadow = true,
    };

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
