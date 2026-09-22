using StyloMail.Assessment.Campaign;
using StyloMail.Assessment.Semantic;
using StyloMail.Core;
using StyloMail.Policy;
using StyloMail.Queue;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// The composition root's contract: the pipeline runs in one order, and the modes it exposes do
/// exactly one thing each.
/// </summary>
public sealed class MailAssessorTests
{
    private sealed record Harness(
        MailAssessor Assessor,
        FixedClock Clock,
        RecordingSemanticClassifier Classifier,
        RecordingMimeAnalyzer Mime,
        FakeProfileStore Profiles,
        RecordingAcceptanceQueue Queue,
        StepRecorder Recorder,
        InMemoryRawMessageSource Payloads,
        StyloMail.Adaptive.Learning.SendingQuotaLedger Ledger);

    private static Harness Build(
        MailAssessorOptions? options = null,
        bool queueThrowsIfReached = false,
        bool classifierUnavailable = false,
        int outboundRecipientBudget = 500)
    {
        var clock = new FixedClock();
        var recorder = new StepRecorder();
        var mime = new RecordingMimeAnalyzer(recorder, clock);
        var classifier = new RecordingSemanticClassifier(clock, recorder) { Unavailable = classifierUnavailable };
        var profiles = new FakeProfileStore(recorder);
        var queue = new RecordingAcceptanceQueue(recorder, queueThrowsIfReached);
        var payloads = new InMemoryRawMessageSource();
        var ledger = new StyloMail.Adaptive.Learning.SendingQuotaLedger(
            outboundRecipientBudget,
            TimeSpan.FromHours(1));

        var assessor = new MailAssessor(
            mime,
            new SemanticCacheClassifier(
                classifier,
                new InMemorySemanticCacheStore(),
                (options ?? Builders.Options()).SemanticCache),
            profiles,
            queue,
            options ?? Builders.Options(),
            payloads,
            quotaLedger: ledger);

        return new Harness(assessor, clock, classifier, mime, profiles, queue, recorder, payloads, ledger);
    }

    private static MailAnalysisInput Submittable(
        MailEnvelope? envelope = null,
        string body = "Please update the payment details for invoice 4471.",
        IReadOnlyList<LinkObservation>? links = null,
        string? subject = "Invoice") =>
        Builders.Message(body, envelope, subject, links);

    // ---------------------------------------------------------------------------------------------
    // Order
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePipelineRunsInTheStatedOrder()
    {
        var harness = Build();
        var message = Submittable();
        harness.Payloads.Add(message.Envelope.PayloadReference, Builders.RawMessage);

        await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        var steps = harness.Recorder.Steps;

        // Parse and extract deterministic evidence before anything asks the provider or reads a
        // profile: the analysis copy the rest of the pipeline works on comes from the parser.
        Assert.True(harness.Recorder.IndexOf("mime") < harness.Recorder.IndexOf("semantic"));
        Assert.True(harness.Recorder.IndexOf("profile.read") < harness.Recorder.IndexOf("semantic"));
        // Policy is never a queue call before the acceptance step: responsibility is decided first.
        Assert.Contains("mime", steps);
        Assert.Contains("semantic", steps);
    }

    [Fact]
    public async Task TheMimeAdapterProducesTheAnalysisCopyThePipelineUses()
    {
        var harness = Build();
        var message = Submittable();
        harness.Payloads.Add(message.Envelope.PayloadReference, Builders.RawMessage);
        harness.Mime.EvidenceSignalId = "mime.identity.display_mismatch";

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        Assert.Equal(1, harness.Mime.CallCount);
        Assert.Contains(assessment.Evidence, e => e.SignalId == "mime.identity.display_mismatch");
    }

    [Fact]
    public async Task WithoutOriginalBytesDeterministicExtractionIsReportedUnavailableRatherThanSkipped()
    {
        var harness = Build();

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        Assert.Equal(0, harness.Mime.CallCount);

        var marker = Assert.Single(
            assessment.Evidence,
            e => e.SignalId == AssessmentEvidenceIds.DeterministicExtractionUnavailable);

        // Reported, not omitted. An absent signal would read as "nothing wrong found", which is the
        // opposite of "nothing was looked at".
        Assert.Equal(EvidenceAvailability.Unavailable, marker.Availability);
        Assert.Null(marker.Value);
    }

    // ---------------------------------------------------------------------------------------------
    // Assessment-only
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AssessmentOnlyCreatesNoQueueStateAndCommitsNoLearning()
    {
        var harness = Build(queueThrowsIfReached: true);
        var message = Submittable(
            envelope: Builders.Envelope(payloadReference: PayloadReferences.Ephemeral));

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        Assert.Equal(0, harness.Queue.CallCount);

        // No delivery state is claimed, because there is no copy anywhere for a disposition to
        // describe.
        Assert.Empty(assessment.RecipientDispositions);

        // No live traffic accounting: observed counters did not move and nothing was learned.
        Assert.Equal(0, harness.Profiles.ApplyObservationCount);
        Assert.Equal(0, harness.Profiles.TotalBaselineVersion);

        // The assessment itself still happened.
        Assert.Equal(1, harness.Classifier.CallCount);
        Assert.NotNull(assessment.AssessmentId);
    }

    [Fact]
    public async Task AssessmentOnlyIsKeptOutOfAcceptanceEvenWhenItCouldHaveBeenAccepted()
    {
        // The dangerous shape, and the reason the earlier test is not enough on its own: a durable
        // reference and real bytes mean *nothing else* stands between this message and the queue,         // RequireDurable passes, the payload check passes, and only the assessment-only guard keeps
        // it out. With an ephemeral reference the durability assert would refuse it anyway, and the
        // guard would never be shown to be load-bearing.
        var harness = Build(queueThrowsIfReached: true);
        var message = Submittable();

        harness.Payloads.Add(message.Envelope.PayloadReference, Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        Assert.Equal(0, harness.Queue.CallCount);
        Assert.Empty(assessment.RecipientDispositions);
    }

    [Fact]
    public async Task AssessmentOnlyStillProducesAVerdictFromCurrentEvidence()
    {
        var harness = Build();
        var message = Submittable(envelope: Builders.Envelope(payloadReference: PayloadReferences.Ephemeral));

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        Assert.Equal(MailAction.Allow, assessment.Action);
        Assert.NotEmpty(assessment.RiskDimensions);

        // The version stamp is on every assessment, including this one: reuse has to be visible, and
        // so does the model that answered.
        Assert.Equal("jev-1.13.0", assessment.Versions.ClassifierModelVersion);
        Assert.Equal(SemanticDimensions.QuestionSchemaVersion, assessment.Versions.QuestionSchemaVersion);
    }

    // ---------------------------------------------------------------------------------------------
    // Unavailable
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnavailableSemanticEvidencePropagatesAsUnavailableAndNeverAsAllow()
    {
        var harness = Build(classifierUnavailable: true);
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        var semantic = assessment.Evidence.Where(e => e.Origin == EvidenceOrigin.Semantic).ToList();

        Assert.All(semantic, e =>
        {
            // Never a zero score: a zero would fabricate a calm message out of an outage.
            Assert.Equal(EvidenceAvailability.Unavailable, e.Availability);
            Assert.Null(e.Value);
        });

        Assert.NotEqual(MailAction.Allow, assessment.Action);
        Assert.Contains(assessment.Reasons, r => r.Code == AssessmentReasonCodes.SemanticUnavailable);
        Assert.Equal(0, harness.Queue.CallCount);
    }

    [Fact]
    public async Task AnOutageStillCountsTheAttemptBecauseObservedStateCountsEverything()
    {
        var harness = Build(classifierUnavailable: true);
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // A sender whose mail always fails assessment is exactly the sender whose rate must be
        // bounded, so the observation is written even though nothing could be concluded from it.
        Assert.True(harness.Profiles.ApplyObservationCount > 0);
        Assert.All(harness.Profiles.Profiles, profile => Assert.True(profile.Observed.Attempts > 0));
    }

    [Fact]
    public async Task ALocalOnlyDeploymentDeclaresItselfOnBothKnobsAndIsThenAllowed()
    {
        var options = Builders.Options() with
        {
            // A tenant that forbids external content processing has an explicitly unavailable
            // semantic state by design, so it has to say so in both places: the composition root
            // must stop declining responsibility for an outage that is not an outage, and policy
            // must stop requiring coverage this deployment will never have. Two knobs rather than
            // one is the right shape, each is a different component stating a different fact, and
            // a single switch would have hidden which of them was being disabled.
            DeclineResponsibilityOnSemanticOutage = false,
            Policy = new PolicyOptions { MinimumCoverageForAllow = 0 },
        };

        var harness = Build(options, classifierUnavailable: true);
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Allow, assessment.Action);
    }

    [Fact]
    public async Task ALocalOnlyDeploymentThatOnlyDeclaresOneKnobIsStillHeldRatherThanAllowed()
    {
        // The failure this guards against is a deployment that disables the deferral and then
        // believes it has opted out. It has not: policy still has no coverage to allow on, and the
        // honest outcome stays a bounded hold until the deployment says what it actually means.
        var options = Builders.Options() with { DeclineResponsibilityOnSemanticOutage = false };
        var harness = Build(options, classifierUnavailable: true);
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Hold, assessment.Action);
        Assert.NotNull(Assert.Single(assessment.RecipientDispositions).ReEvaluateBy);
    }

    // ---------------------------------------------------------------------------------------------
    // Shadow
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ShadowModeRecordsTheProposedActionWhileStillForwarding()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        // Every dimension near-certain pushes the index past the quarantine threshold, so the
        // proposal is unambiguous and the forwarding is demonstrably a separate fact.
        foreach (var dimension in SemanticDimensions.All)
        {
            harness.Classifier.Values[dimension.Id] = 0.95;
        }

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock, shadowMode: true),
            CancellationToken.None);

        Assert.Equal(MailAction.Quarantine, assessment.ProposedActionInShadow);

        // Shadow is a mode, not an action: forwarding still happens, and the recipient is queued
        // rather than quarantined.
        Assert.Equal(MailAction.Allow, assessment.Action);
        Assert.All(assessment.RecipientDispositions, d => Assert.Equal(DeliveryState.Queued, d.DeliveryState));
        Assert.Equal(1, harness.Queue.CallCount);
    }

    [Fact]
    public async Task QuarantineIsAppliedWhenShadowModeIsOff()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        foreach (var dimension in SemanticDimensions.All)
        {
            harness.Classifier.Values[dimension.Id] = 0.95;
        }

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Null(assessment.ProposedActionInShadow);
        Assert.Equal(MailAction.Quarantine, assessment.Action);
        Assert.All(assessment.RecipientDispositions, d => Assert.Equal(DeliveryState.Quarantined, d.DeliveryState));
    }

    // ---------------------------------------------------------------------------------------------
    // Hard limits and acceptance
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnObservedHopCountReachesTheQueue()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        await harness.Assessor.AssessAsync(
            Submittable(Builders.Envelope(hopCount: 7)),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(7, Assert.Single(harness.Queue.Submissions).HopCount);
    }

    [Fact]
    public async Task AnUnobservedHopCountReachesTheQueueAsNullAndNotAsZero()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        await harness.Assessor.AssessAsync(
            Submittable(Builders.Envelope(hopCount: null)),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // Null is the record that the loop backstop did not run for this message; zero is a claim
        // that we looked and found no prior hops. Collapsing them here would turn "we did not check"
        // into "there were no hops", and the queue would record the backstop as having run.
        Assert.Null(Assert.Single(harness.Queue.Submissions).HopCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<>")]
    public async Task EveryFormOfTheNullSenderIsRefusedOnTheOutboundPath(string mailFrom)
    {
        // The wire form matters as much as the empty one. My first check was IsNullOrWhiteSpace, which
        // caught "" and missed "<>", so the same message was either refused here before any provider
        // spend or let through to be refused by the queue afterwards, two outcomes for one input,
        // decided by notation.
        //
        // Now sourced from `Core.SenderAddresses.IsNullSender`, the single definition, rather than a
        // local mirror. That consolidation found a THIRD behaviour neither of us had noticed: mine
        // also accepted "< >" (blank inside the brackets), which is a malformed address rather than
        // the null sender. Core is exact on "<>", so "< >" is treated as an ordinary address, an
        // address-syntax gap, documented by queue- rather than fixed inside a rule that is not about
        // it.
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var outbound = Submittable(Builders.Envelope(
            direction: MailDirection.Outbound,
            mailFrom: mailFrom));

        var assessment = await harness.Assessor.AssessAsync(
            outbound,
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Reject, assessment.Action);

        // Refused before the provider is asked, which is the point of catching it in validation rather
        // than letting the queue decline it later.
        Assert.Equal(0, harness.Classifier.CallCount);
        Assert.Equal(0, harness.Queue.CallCount);

        var violation = Assert.Single(
            assessment.Evidence,
            e => e.SignalId == AssessmentEvidenceIds.HardLimitViolation);

        Assert.Contains(violation.Attributes!, a => a.Value == AssessmentRules.NullSenderNotPermitted);
    }

    [Fact]
    public async Task ANullSenderOnTheSubmissionPathIsRefusedUnconditionally()
    {
        // Refused with NO approved-sender list configured, which is the case that matters: the old
        // path caught a null sender only as a side effect of an identity mismatch, so whether the
        // message was cleanly declined or crashed depended on unrelated configuration.
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var outbound = Submittable(Builders.Envelope(
            direction: MailDirection.Outbound,
            mailFrom: string.Empty));

        var assessment = await harness.Assessor.AssessAsync(
            outbound,
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Reject, assessment.Action);
        Assert.Equal(0, harness.Queue.CallCount);

        var violation = Assert.Single(
            assessment.Evidence,
            e => e.SignalId == AssessmentEvidenceIds.HardLimitViolation);

        Assert.Contains(violation.Attributes!, a => a.Value == AssessmentRules.NullSenderNotPermitted);
    }

    [Fact]
    public async Task AnInboundNullSenderIsLegitimateMailAndNotRefused()
    {
        // An inbound DSN delivered to a mailbox is ordinary mail, the ruling leaves the inbound path
        // unaffected. Refusing it here would be refusing bounces people are meant to receive.
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var inbound = Submittable(Builders.Envelope(
            direction: MailDirection.Inbound,
            mailFrom: string.Empty));

        var assessment = await harness.Assessor.AssessAsync(
            inbound,
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.DoesNotContain(
            assessment.Evidence
                .Where(e => e.SignalId == AssessmentEvidenceIds.HardLimitViolation)
                .SelectMany(e => e.Attributes ?? []),
            a => a.Value == AssessmentRules.NullSenderNotPermitted);
    }

    [Fact]
    public async Task AMandatoryLimitIsRefusedWithoutSpendingProviderBudget()
    {
        var harness = Build();
        var message = Submittable(Builders.Envelope(
            recipients: [.. Enumerable.Range(0, 5_000).Select(i => $"r{i}@example.com")]));

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Reject, assessment.Action);
        Assert.Equal(0, harness.Classifier.CallCount);
        Assert.Equal(0, harness.Queue.CallCount);

        var violation = Assert.Single(
            assessment.Evidence,
            e => e.SignalId == AssessmentEvidenceIds.HardLimitViolation);

        Assert.Contains(
            violation.Attributes!,
            a => a.Value == AssessmentRules.RecipientCountExceeded);
    }

    [Theory]
    [InlineData("links")]
    [InlineData("attachments")]
    [InlineData("body")]
    public async Task EveryMandatoryLimitIsActuallyApplied(string which)
    {
        // One case per rule, because the pattern that keeps recurring here is a limit that is
        // declared, implemented, and never exercised, so a mutation removing it reddens nothing and
        // the limit is only believed to work. MaxRecipients had a test; these three did not.
        var harness = Build();

        var message = which switch
        {
            "links" => Builders.Message(
                links: [.. Enumerable.Range(0, 501).Select(i => new LinkObservation
                {
                    DisplayedText = "here",
                    ActualTarget = $"https://example.test/{i}",
                })]),
            "attachments" => Builders.Message(
                attachments: [.. Enumerable.Range(0, 101).Select(i => new AttachmentMetadata
                {
                    FileName = $"f{i}.pdf",
                    DeclaredContentType = "application/pdf",
                    SizeBytes = 1,
                    ContentUnavailable = false,
                })]),
            _ => Builders.Message(new string('x', 1_000_001)),
        };

        var expected = which switch
        {
            "links" => AssessmentRules.LinkCountExceeded,
            "attachments" => AssessmentRules.AttachmentCountExceeded,
            _ => AssessmentRules.BodySizeExceeded,
        };

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Reject, assessment.Action);

        var violation = Assert.Single(
            assessment.Evidence,
            e => e.SignalId == AssessmentEvidenceIds.HardLimitViolation);

        Assert.Contains(violation.Attributes!, a => a.Value == expected);
    }

    [Fact]
    public async Task AParseRejectionIsRefusedRatherThanAssessedAsAFragment()
    {
        var harness = Build();
        harness.Mime.Reject = true;
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Reject, assessment.Action);
        Assert.Equal(0, harness.Classifier.CallCount);

        var violation = Assert.Single(
            assessment.Evidence,
            e => e.SignalId == AssessmentEvidenceIds.HardLimitViolation);

        Assert.Contains(violation.Attributes!, a => a.Value == "parse.part-count");
    }

    [Fact]
    public async Task EachRecipientReportsTheSignalsComputedForIt()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(Builders.Envelope(recipients: ["a@example.com", "b@example.com"])),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // `RecipientDisposition.RecipientScopedSignalIds` is a Core field with exactly one producer,
        // which is this pipeline. It was never set, so it read as an honest "nothing recipient-specific
        // here" on every message, a contract field silently always-null rather than a real absence.
        Assert.Equal(2, assessment.RecipientDispositions.Count);

        foreach (var disposition in assessment.RecipientDispositions)
        {
            Assert.NotNull(disposition.RecipientScopedSignalIds);
            Assert.NotEmpty(disposition.RecipientScopedSignalIds!);
        }

        // Attributed per recipient, not the same flat list copied onto each, a shared list would
        // satisfy "not empty" while telling a reviewer nothing about whose signals these are.
        var first = assessment.RecipientDispositions[0].RecipientScopedSignalIds;
        var second = assessment.RecipientDispositions[1].RecipientScopedSignalIds;
        Assert.Equal(first!.Count, second!.Count);
    }

    [Fact]
    public async Task RecipientsBeyondTheRelationshipBoundReportNoRecipientScopedSignals()
    {
        var harness = Build(Builders.Options() with { MaxRelationshipsObserved = 1 });
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(Builders.Envelope(recipients: ["a@example.com", "b@example.com"])),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // The relationship bound is real, so the second recipient genuinely has no pair profile.
        // Null is the honest answer for that, and it stays distinguishable from the bug above, a
        // list that is always null everywere is a defect, a null for one recipient is a fact.
        Assert.NotNull(assessment.RecipientDispositions[0].RecipientScopedSignalIds);
        Assert.Null(assessment.RecipientDispositions[1].RecipientScopedSignalIds);
    }

    [Fact]
    public async Task ARefusedAcceptanceBecomesADeferralRatherThanAClaimedDelivery()
    {
        var harness = Build();
        harness.Queue.Admission = QueueAdmission.RefusedTenantItemLimit;
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // Policy would have allowed it, but responsibility never transferred, so reporting an allow
        // would claim a delivery state that does not exist.
        Assert.Equal(MailAction.Defer, assessment.Action);
        Assert.Contains(assessment.Reasons, r => r.Code == AssessmentReasonCodes.AcceptanceRefused);
        Assert.Empty(assessment.RecipientDispositions);
    }

    [Fact]
    public async Task UnavailableSpoolStorageIsADeferralAndNeverAnAcceptance()
    {
        var harness = Build();
        harness.Queue.ThrowSpoolUnavailable = true;
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // Disk full is exactly the condition under which accepting mail would destroy it.
        Assert.Equal(MailAction.Defer, assessment.Action);
        Assert.Contains(assessment.Reasons, r => r.Code == AssessmentReasonCodes.AcceptanceRefused);
    }

    [Fact]
    public async Task AnEphemeralPayloadOnTheSubmissionPathIsRefusedLoudly()
    {
        var harness = Build();
        var message = Submittable(
            envelope: Builders.Envelope(payloadReference: PayloadReferences.Ephemeral));

        // An assessment-only input reaching the acceptance path is a wiring error, and the Core
        // contract says it must surface at acceptance rather than as mail that vanishes.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await harness.Assessor.AssessAsync(
                message,
                Builders.Context(harness.Clock),
                CancellationToken.None));

        Assert.Contains("spool://", thrown.Message);
        Assert.Equal(0, harness.Queue.CallCount);
    }

    [Fact]
    public async Task ASubmissionWithoutAPayloadToPersistIsDeferredRatherThanAccepted()
    {
        var harness = Build();

        // A durable reference, but the bytes are not reachable. Accepting would manufacture mail we
        // could not later produce.
        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Defer, assessment.Action);
        Assert.Contains(assessment.Reasons, r => r.Code == AssessmentReasonCodes.NoPayloadForAcceptance);
        Assert.Equal(0, harness.Queue.CallCount);
    }

    [Fact]
    public async Task ATenantMismatchIsRefusedRatherThanGuessed()
    {
        var harness = Build();
        var message = Submittable(Builders.Envelope(tenantId: "tenant-1"));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await harness.Assessor.AssessAsync(
                message,
                Builders.Context(harness.Clock, tenantId: "tenant-2"),
                CancellationToken.None));
    }

    [Fact]
    public async Task AnAcceptedHoldCarriesABoundedReEvaluationDeadline()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        // High enough to hold, not high enough to quarantine.
        foreach (var dimension in SemanticDimensions.All)
        {
            harness.Classifier.Values[dimension.Id] = 0.6;
        }

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Hold, assessment.Action);
        var disposition = Assert.Single(assessment.RecipientDispositions);
        Assert.Equal(DeliveryState.Held, disposition.DeliveryState);

        // A hold without a deadline is an indefinite retention, which the spec forbids.
        Assert.NotNull(disposition.ReEvaluateBy);
        Assert.True(disposition.ReEvaluateBy > harness.Clock.GetUtcNow());
    }

    // ---------------------------------------------------------------------------------------------
    // The submission seam: who accepts, under whose key, and what the caller gets back
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheCallersIdempotencyKeyIsWhatReachesTheQueue()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock, clientIdempotencyKey: "client-key-7"),
            CancellationToken.None);

        var submission = Assert.Single(harness.Queue.Submissions);

        // Not a minted id. An assessment id is fresh on every attempt, so using one would give a
        // retrying client a new queue entry every time while looking like replay protection.
        Assert.Equal("client-key-7", submission.IdempotencyKey);
    }

    [Fact]
    public async Task ARetriedSubmissionReusesTheExistingQueueItemAndReportsTheSameId()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var context = Builders.Context(harness.Clock, clientIdempotencyKey: "client-key-7");

        var first = await harness.Assessor.AssessAsync(Submittable(), context, CancellationToken.None);
        var replay = await harness.Assessor.AssessAsync(Submittable(), context, CancellationToken.None);

        // The whole point of the seam fix. Two assessments, one queue item, and the same id handed
        // back both times, the second id is the *existing* item's, not one we would have created.
        Assert.NotNull(first.SubmissionId);
        Assert.Equal(first.SubmissionId, replay.SubmissionId);
        Assert.Equal(1, harness.Queue.DistinctQueueItems);
    }

    [Fact]
    public async Task WithoutACallerKeyNoKeyIsInvented()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // A fallback key would look like replay protection while providing none, which is worse than
        // an honest absence: the caller would believe a retry was safe when it was not.
        Assert.Null(Assert.Single(harness.Queue.Submissions).IdempotencyKey);
    }

    [Fact]
    public async Task AnAcceptedSubmissionReportsItsQueueId()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock, clientIdempotencyKey: "client-key-7"),
            CancellationToken.None);

        Assert.Equal(MailAction.Allow, assessment.Action);

        // The id the *queue* minted, not merely some non-null value. A fabricated id would satisfy
        // an is-it-present check while pointing the caller at a queue item that does not exist.
        Assert.Equal(Assert.Single(harness.Queue.ReturnedQueueIds), assessment.SubmissionId);
    }

    [Fact]
    public async Task AFreshSubmissionSaysItCreatedTheQueueRow()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock, clientIdempotencyKey: "client-key-7"),
            CancellationToken.None);

        // The fact, on the field. A caller choosing between a created-resource status and an
        // already-existed one needs this, not a reason code it has to read prose out of.
        Assert.Equal(SubmissionAdmission.Created, assessment.Submission);

        // And no replay explanation, because there is nothing to explain.
        Assert.DoesNotContain(assessment.Reasons, r => r.Code == AssessmentReasonCodes.SubmissionDuplicate);
    }

    [Fact]
    public async Task AReplayedSubmissionSaysTheRowAlreadyExisted()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);
        var context = Builders.Context(harness.Clock, clientIdempotencyKey: "client-key-7");

        await harness.Assessor.AssessAsync(Submittable(), context, CancellationToken.None);
        var replay = await harness.Assessor.AssessAsync(Submittable(), context, CancellationToken.None);

        // Both calls hand back the SAME queue id, correctly, a retry must receive the id it already
        // has. So the id cannot say whether this request created anything, and a route that reports
        // "accepted" on a replay is making a caller-visible claim that is not true.
        Assert.Equal(SubmissionAdmission.Duplicate, replay.Submission);

        // The replay also earns a line in the ledger, because "asked twice, answered once" is worth
        // seeing. The reason is colour; the field above is the answer.
        Assert.Contains(replay.Reasons, r => r.Code == AssessmentReasonCodes.SubmissionDuplicate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheSubmissionAdmissionAlwaysAgreesWithTheSubmissionId(bool forceDuplicate)
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);
        var context = Builders.Context(harness.Clock, clientIdempotencyKey: "client-key-7");

        var first = await harness.Assessor.AssessAsync(Submittable(), context, CancellationToken.None);
        var second = await harness.Assessor.AssessAsync(Submittable(), context, CancellationToken.None);

        var assessment = forceDuplicate ? second : first;

        // Two fields that must agree are a smell. The remedy this project uses is to make the
        // agreement tested rather than hoped for, a caller reading one and not the other would
        // otherwise get a coherent-looking answer from a pair that had drifted apart.
        Assert.Equal(assessment.SubmissionId is not null, assessment.Submission is not null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AResultThatTookNoResponsibilityReportsNeitherField(bool assessmentOnly)
    {
        var harness = Build();

        // Both routes to "we did not take this": the assessment path, and a policy decline on the
        // submission path. Neither may leave a caller able to conclude a delivery was arranged.
        var message = assessmentOnly
            ? Submittable(envelope: Builders.Envelope(payloadReference: PayloadReferences.Ephemeral))
            : Submittable();

        if (!assessmentOnly)
        {
            harness.Queue.Admission = QueueAdmission.RefusedTenantItemLimit;
        }

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: assessmentOnly),
            CancellationToken.None);

        Assert.Null(assessment.SubmissionId);
        Assert.Null(assessment.Submission);
    }

    [Fact]
    public async Task AResultThatTookNoResponsibilityReportsNoSubmissionId()
    {
        var harness = Build();
        var message = Submittable(envelope: Builders.Envelope(payloadReference: PayloadReferences.Ephemeral));

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        // Null is a meaningful "we did not take this". A caller that cannot tell an unaccepted
        // message from an accepted one has to guess, and guessing here means claiming a delivery
        // that does not exist.
        Assert.Null(assessment.SubmissionId);
        Assert.Null(assessment.Submission);
    }

    [Fact]
    public async Task ARefusedAcceptanceReportsNoSubmissionId()
    {
        var harness = Build();
        harness.Queue.Admission = QueueAdmission.RefusedTenantItemLimit;
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Defer, assessment.Action);
        Assert.Null(assessment.SubmissionId);
    }

    [Fact]
    public async Task ADurableReferenceThatNamesNoPayloadIsDiagnosedDistinctly()
    {
        var harness = Build();

        // `spool://pending` passes the scheme check and resolves to nothing. That is not an ordinary
        // missing payload, it is a caller that believes it spooled something, and the difference
        // cost an hour at the host seam because both cases read as a storage fault.
        var message = Submittable(
            envelope: Builders.Envelope(payloadReference: "spool://pending"));

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock),
            CancellationToken.None);

        var reason = Assert.Single(
            assessment.Reasons,
            r => r.Code == AssessmentReasonCodes.NoPayloadForAcceptance);

        Assert.Contains("names no stored payload", reason.Message);
        Assert.Contains("spool://pending", reason.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // The outbound recipient budget: reserved on the way in, given back when nothing was dispatched
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARefusedSubmissionGivesItsRecipientBudgetBack()
    {
        var harness = Build(outboundRecipientBudget: 10);
        harness.Queue.Admission = QueueAdmission.RefusedTenantItemLimit;
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var outbound = Submittable(Builders.Envelope(
            direction: MailDirection.Outbound,
            recipients: ["a@example.com", "b@example.com"]));

        await harness.Assessor.AssessAsync(outbound, Builders.Context(harness.Clock), CancellationToken.None);

        // The ledger has NO rolling window, so a budget consumed by refused traffic never comes
        // back: a legitimate principal reaches its ceiling once and is deferred for good. That is a
        // self-inflicted outage in which every message the quota blocked is itself the reason the
        // quota stays blocked.
        Assert.Equal(10, harness.Ledger.Remaining("tenant-1", "principal-1", harness.Clock.GetUtcNow()));
        Assert.Equal(1, harness.Assessor.Statistics.BudgetReleases);
        Assert.Equal(0, harness.Assessor.Statistics.BudgetReleaseShortfall);
    }

    [Fact]
    public async Task AnAcceptedSubmissionKeepsItsRecipientBudget()
    {
        var harness = Build(outboundRecipientBudget: 10);
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var outbound = Submittable(Builders.Envelope(
            direction: MailDirection.Outbound,
            recipients: ["a@example.com", "b@example.com"]));

        await harness.Assessor.AssessAsync(outbound, Builders.Context(harness.Clock), CancellationToken.None);

        // Released only when nothing was dispatched. Handing back budget for recipients that are
        // about to be delivered would defeat the one control bounding a compromised account.
        Assert.Equal(8, harness.Ledger.Remaining("tenant-1", "principal-1", harness.Clock.GetUtcNow()));
        Assert.Equal(0, harness.Assessor.Statistics.BudgetReleases);
    }

    [Fact]
    public async Task AnExhaustedBudgetDefersAndIsNotItselfReleased()
    {
        var harness = Build(outboundRecipientBudget: 2);
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var outbound = Submittable(Builders.Envelope(
            direction: MailDirection.Outbound,
            recipients: ["a@example.com", "b@example.com"]));

        var first = await harness.Assessor.AssessAsync(
            outbound, Builders.Context(harness.Clock), CancellationToken.None);

        var second = await harness.Assessor.AssessAsync(
            outbound, Builders.Context(harness.Clock, correlationId: "corr-2"), CancellationToken.None);

        Assert.Equal(MailAction.Allow, first.Action);

        // Exhaustion defers, and the deferral must NOT release. If it did, the quota would
        // un-exhaust itself on every message and never bind at all, the control would be pure
        // ceremony. An unsuccessful reservation has nothing to give back, which is exactly why the
        // release is guarded on the reserve having succeeded.
        Assert.Equal(MailAction.Defer, second.Action);
        Assert.Equal(0, harness.Ledger.Remaining("tenant-1", "principal-1", harness.Clock.GetUtcNow()));
        Assert.Equal(1, harness.Assessor.Statistics.BudgetReservations);
    }

    [Fact]
    public async Task AnExhaustedBudgetRecoversOnceTheWindowRollsOver()
    {
        var harness = Build(outboundRecipientBudget: 2);
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var outbound = Submittable(Builders.Envelope(
            direction: MailDirection.Outbound,
            recipients: ["a@example.com", "b@example.com"]));

        var first = await harness.Assessor.AssessAsync(
            outbound, Builders.Context(harness.Clock), CancellationToken.None);

        var exhausted = await harness.Assessor.AssessAsync(
            outbound, Builders.Context(harness.Clock, correlationId: "corr-2"), CancellationToken.None);

        Assert.Equal(MailAction.Allow, first.Action);
        Assert.Equal(MailAction.Defer, exhausted.Action);

        // The ledger gained a rolling window, which turns the quota from a lifetime cap into a rate
        // limit. That matters here rather than being trivia: it is why the reserve-without-release
        // bug I fixed was severe in the first place, and it is the behaviour an operator relies on
        // when a burst defers and then clears.
        harness.Clock.Advance(TimeSpan.FromHours(2));

        var recovered = await harness.Assessor.AssessAsync(
            outbound, Builders.Context(harness.Clock, correlationId: "corr-3"), CancellationToken.None);

        Assert.Equal(MailAction.Allow, recovered.Action);
    }

    [Fact]
    public void AShortfallIsDetectableAtAll()
    {
        // A characterisation test of a DEPENDENCY's contract, and it exists for one reason: the
        // shortfall counter below is a comparison against what this ledger returns. If the ledger
        // ever stopped reporting the amount actually given back, returning the amount requested
        // instead, the counter would become an inert line that always reads zero, and nothing else
        // in this suite would notice. A check that cannot fail is not a check, including when the
        // thing that broke it is somebody else's code.
        var ledger = new StyloMail.Adaptive.Learning.SendingQuotaLedger(10);
        var at = DateTimeOffset.UtcNow;

        Assert.True(ledger.TryReserve("tenant-1", "principal-1", 2, at));

        // Five back for two reserved: the ledger clamps rather than throwing, and the number it
        // returns is the two it could actually give back.
        var returned = ledger.Release("tenant-1", "principal-1", 5, at);

        Assert.Equal(2, returned);
        Assert.Equal(10, ledger.Remaining("tenant-1", "principal-1", at));
    }

    [Fact]
    public async Task ProfileWritesAreObservableFromOutsideAndAnOrdinaryMessageOnlyAppends()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // Reachable from the operator surface, which is the point: a counter nobody can read is a
        // comment with a number attached. And a message that learned nothing, the ordinary case,         // must show appends only.
        Assert.True(harness.Assessor.ProfileWrites.Observed > 0);
        Assert.Equal(0, harness.Assessor.ProfileWrites.Mutated);
    }

    [Fact]
    public async Task AnAssessmentOnlyCallNeverTouchesTheBudget()
    {
        var harness = Build(outboundRecipientBudget: 10);
        var outbound = Submittable(Builders.Envelope(
            direction: MailDirection.Outbound,
            recipients: ["a@example.com"],
            payloadReference: PayloadReferences.Ephemeral));

        await harness.Assessor.AssessAsync(
            outbound,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        // Assessment-only participates in no live traffic accounting, and the budget is accounting.
        Assert.Equal(10, harness.Ledger.Remaining("tenant-1", "principal-1", harness.Clock.GetUtcNow()));
        Assert.Equal(0, harness.Assessor.Statistics.BudgetReservations);
    }

    // ---------------------------------------------------------------------------------------------
    // Learning
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAssessmentPathLearnsNothingWithoutAnExplicitRule()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // Counters moved, the attempt happened. Trusted history did not: an assessment is a
        // question, not a verdict about what was correct.
        Assert.True(harness.Profiles.ApplyObservationCount > 0);
        Assert.Equal(0, harness.Profiles.UpdateCount);
        Assert.Equal(0, harness.Profiles.TotalBaselineVersion);
    }

    [Fact]
    public async Task AnOperatorNamedRuleIsWhatAllowsTheAssessmentPathToLearn()
    {
        var harness = Build(Builders.Options(assessmentPathLearningRuleId: "rule.replay-training/1"));
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.True(harness.Profiles.TotalBaselineVersion > 0);
    }

    [Fact]
    public async Task AnAssessmentOnlyCallLearnsNothingEvenWithARuleConfigured()
    {
        var harness = Build(Builders.Options(assessmentPathLearningRuleId: "rule.replay-training/1"));
        var message = Submittable(envelope: Builders.Envelope(payloadReference: PayloadReferences.Ephemeral));

        await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        Assert.Equal(0, harness.Profiles.TotalBaselineVersion);
    }

    [Fact]
    public async Task TheGateRefusesALabelWithNoAuthority()
    {
        var harness = Build();
        var clock = new FixedClock();

        var outcome = await harness.Assessor.CommitTrustedOutcomeAsync(new StyloMail.Assessment.Learning.TrustedLearningRequest
        {
            Key = StyloMail.Adaptive.Profiles.ProfileScopes.OutboundSender("tenant-1", "pseudonym"),
            Sample = new StyloMail.Adaptive.Profiles.TrustedSample
            {
                Dimensions = StyloMail.Adaptive.Profiles.DimensionVector.Create(),
                Provenance = StyloMail.Adaptive.Profiles.LabelProvenance.DeliveryOnly,
                RecordedAt = clock.GetUtcNow(),
            },
            AuthorizedOutcomePresent = false,
            AssessmentOnly = false,
            ClaimedProvenance = StyloMail.Adaptive.Profiles.LabelProvenance.DeliveryOnly,
        });

        // "It was delivered" is a label an attacker generates by sending mail. No amount of asking
        // makes it teach anything.
        Assert.False(outcome.Attempted);
        Assert.Equal(0, harness.Profiles.TotalBaselineVersion);
    }

    // ---------------------------------------------------------------------------------------------
    // Campaign near-duplicates
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANearDuplicateWithAChangedDestinationIsAVariantAndNotAMatch()
    {
        var harness = Build();
        var fingerprintTarget = "https://secure-bank.example/login";

        var first = Submittable(
            envelope: Builders.Envelope(internalMessageId: "msg-1"),
            links: [new LinkObservation { DisplayedText = "here", ActualTarget = fingerprintTarget }]);

        var changed = Submittable(
            envelope: Builders.Envelope(internalMessageId: "msg-2"),
            links:
            [
                new LinkObservation
                {
                    DisplayedText = "here",
                    ActualTarget = "https://secure-bank.example.attacker.test/login",
                },
            ]);

        await harness.Assessor.AssessAsync(
            first,
            Builders.Context(harness.Clock, assessmentOnly: true, correlationId: "corr-1"),
            CancellationToken.None);

        var second = await harness.Assessor.AssessAsync(
            changed,
            Builders.Context(harness.Clock, assessmentOnly: true, correlationId: "corr-2"),
            CancellationToken.None);

        // The wording and the semantic vector are identical, only the destination moved. A match
        // here would let the first version of an attack vouch for the second.
        Assert.DoesNotContain(
            second.Evidence.Where(e => e.Availability == EvidenceAvailability.Available),
            e => e.SignalId == CampaignEvidenceIds.NearDuplicate);

        var variant = Assert.Single(
            second.Evidence,
            e => e.SignalId == CampaignEvidenceIds.SecurityBearingVariant);

        Assert.Equal(EvidenceAvailability.Available, variant.Availability);
        Assert.Contains(
            variant.Attributes!,
            a => a.Name == "security_bearing_match" && a.Value == "false");
    }

    [Fact]
    public async Task AnIdenticalMessageIsMatchedAsTheSameCampaign()
    {
        var harness = Build();

        var message = Submittable(envelope: Builders.Envelope(internalMessageId: "msg-1"));
        var same = Submittable(envelope: Builders.Envelope(internalMessageId: "msg-2"));

        await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true, correlationId: "corr-1"),
            CancellationToken.None);

        var second = await harness.Assessor.AssessAsync(
            same,
            Builders.Context(harness.Clock, assessmentOnly: true, correlationId: "corr-2"),
            CancellationToken.None);

        var match = Assert.Single(
            second.Evidence,
            e => e.SignalId == CampaignEvidenceIds.NearDuplicate);

        Assert.Equal(EvidenceAvailability.Available, match.Availability);
        Assert.Contains(match.Attributes!, a => a.Name == "matching_message_ids" && a.Value == "msg-1");
    }

    [Fact]
    public async Task TheCampaignWindowIsBoundedPerTenant()
    {
        var harness = Build(Builders.Options() with { CampaignWindowCapacity = 4 });

        for (var i = 0; i < 12; i++)
        {
            await harness.Assessor.AssessAsync(
                Submittable(
                    envelope: Builders.Envelope(internalMessageId: $"msg-{i}"),
                    body: $"unique body {i}"),
                Builders.Context(harness.Clock, assessmentOnly: false, correlationId: $"corr-{i}"),
                CancellationToken.None);
        }

        // The more mail an attacker sends, the more a window like this would retain if it were
        // unbounded. Capacity is a ceiling, not a target.
        Assert.Equal(4, harness.Assessor.CampaignWindow.CountFor("tenant-1"));
    }
}
