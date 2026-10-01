using StyloMail.Assessment.Campaign;
using StyloMail.Assessment.Semantic;
using StyloMail.Adaptive.Profiles;
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

    /// <param name="neverAsks">
    /// Wires the production never-asking tier in place of the recording stub, which is what a
    /// deployment with <c>AssessmentProvider.NeverAsks</c> actually runs. The shape is the
    /// production class's on purpose: a harness that fabricated it could keep this suite green while
    /// the composition emitted something else. <b>It follows that <see cref="Harness.Classifier"/>
    /// is NOT in the chain for such a test</b>, so its counters and its inputs say nothing about what
    /// ran, and an assertion on them there would be an assertion about a component the assessor never
    /// reached.
    /// </param>
    private static Harness Build(
        MailAssessorOptions? options = null,
        bool queueThrowsIfReached = false,
        bool classifierUnavailable = false,
        int outboundRecipientBudget = 500,
        bool neverAsks = false)
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

        ISemanticMailClassifier tier = neverAsks
            ? new NeverAskingSemanticClassifier(clock)
            : classifier;

        var assessor = new MailAssessor(
            mime,
            new SemanticCacheClassifier(
                tier,
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
    public async Task AFirstMessageFromAnUnknownSenderReportsNoBehaviouralContext()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // Recorded rather than omitted. A classifier that saw only the words cannot answer "is this
        // unusual for this sender", and an assessment made without that is a different question's
        // answer, not a weaker version of the same one. The ledger has to say which was asked.
        //
        // Fires on the FIRST message from a principal, which is the case it was written for: the
        // profile row is created by the observation in step three, so by step four there is a
        // snapshot to encode, but it carries no history yet, and the encoder reports it as
        // ProfileAvailable: false rather than as a quiet sender.
        var marker = Assert.Single(
            assessment.Evidence,
            e => e.SignalId == AssessmentEvidenceIds.BehaviouralContextUnavailable);

        Assert.Equal(EvidenceAvailability.Unavailable, marker.Availability);
        Assert.Null(marker.Value);
    }

    [Fact]
    public async Task ASenderWithHistoryReachesTheClassifierAndStopsTheMarkerFiring()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        // First message: establishes the profile. Second: the sender now has history, so the
        // classifier is told about it and the "we were not informed" marker must not fire.
        await harness.Assessor.AssessAsync(
            Submittable(), Builders.Context(harness.Clock, correlationId: "corr-1"), CancellationToken.None);

        var second = await harness.Assessor.AssessAsync(
            Submittable(Builders.Envelope(internalMessageId: "msg-2")),
            Builders.Context(harness.Clock, correlationId: "corr-2"),
            CancellationToken.None);

        // A profile reached the classifier, so the ledger no longer claims the judgement was
        // uninformed. This is the assertion that would catch the encoder being wired but its result
        // dropped on the floor somewhere between step three and step four.
        Assert.DoesNotContain(
            second.Evidence,
            e => e.SignalId == AssessmentEvidenceIds.BehaviouralContextUnavailable);

        // And it was the classifier that got it, not just the ledger that stopped saying otherwise.
        Assert.True(harness.Classifier.LastInput?.Profile is not null);
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
    // Caller-supplied bytes: the second source, and the order between the two
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AByteSuppliedByTheCallerIsParsedWhenNoDurablePayloadResolves()
    {
        // The assessment-only route's case. It writes nothing durable, so its reference is ephemeral
        // by design and resolves to nothing, and the bytes it has already decoded are the only input
        // left. Without this the parse never happens and the tier reports extraction unavailable for
        // every message the route ever assesses.
        var harness = Build();
        var message = Submittable(envelope: Builders.Envelope(payloadReference: PayloadReferences.Ephemeral));
        byte[] supplied = "Subject: supplied\r\n\r\nbody"u8.ToArray();

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None,
            supplied);

        // Parsed once, and these were the bytes. The supply is not a mode and not a hint: it is the
        // input the assessor would have read for itself, reaching the same component.
        Assert.Equal(1, harness.Mime.CallCount);
        Assert.Equal(supplied, harness.Mime.ParsedBytes.Single().ToArray());
        Assert.Contains(assessment.Evidence, e => e.SignalId == harness.Mime.EvidenceSignalId);

        // And the marker that says nothing was looked at is absent, which is the difference the
        // whole change exists to make: "nothing was looked at" must not be reported about a message
        // that was looked at.
        Assert.DoesNotContain(assessment.Evidence, e =>
            e.SignalId == AssessmentEvidenceIds.DeterministicExtractionUnavailable);
    }

    [Fact]
    public async Task TheDurablePayloadIsParsedAndTheCallersBytesAreNeverRead()
    {
        // Precedence, at the only place it can be observed. Both sources are populated with
        // different bytes, so which one the parser was handed is decidable; a test that read only
        // the resulting evidence could not tell them apart, because the same component parses both.
        var harness = Build();
        var message = Submittable();
        harness.Payloads.Add(message.Envelope.PayloadReference, Builders.RawMessage);
        byte[] supplied = "Subject: supplied\r\n\r\nbody"u8.ToArray();

        await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None,
            supplied);

        // One call, with the spool's bytes. A caller's bytes are an alternative to the durable
        // payload rather than a second opinion on it: they are not merged, not preferred, and not
        // compared, so a call that resolved its own payload spends nothing on the parameter.
        Assert.Equal(1, harness.Mime.CallCount);
        Assert.Equal(Builders.RawMessage, harness.Mime.ParsedBytes.Single().ToArray());
        Assert.DoesNotContain(harness.Mime.ParsedBytes, bytes => bytes.Span.SequenceEqual(supplied));
    }

    [Fact]
    public async Task SupplyingTheBytesReachesTheDecisionTheDurablePathWouldHaveMade()
    {
        // The consequence, one level below the route. AssessmentOnlyStillProducesAVerdictFromCurrent-
        // Evidence pins the same message holding when there are no bytes; the identical message with
        // the bytes supplied must not hold for that reason, or the route has gained a parameter it
        // does not act on.
        var harness = Build();
        var message = Submittable(envelope: Builders.Envelope(payloadReference: PayloadReferences.Ephemeral));
        byte[] supplied = "Subject: supplied\r\n\r\nbody"u8.ToArray();

        var withoutBytes = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        var withBytes = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None,
            supplied);

        Assert.Equal(MailAction.Hold, withoutBytes.Action);

        // The gate is what moves, and it moves because there is now a checkable row to corroborate
        // the index with. Asserted on the reason rather than on the action, so this stays true if
        // the verdict above the threshold changes for a reason of its own.
        Assert.Contains(withoutBytes.Reasons, r =>
            r.Code == "policy.allow_uncorroborated_by_deterministic_evidence");
        Assert.DoesNotContain(withBytes.Reasons, r =>
            r.Code == "policy.allow_uncorroborated_by_deterministic_evidence");
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

        // The point of the test is that a verdict is produced from current evidence, and it still is.
        // The verdict is now a hold, and the reason is the point: the payload is ephemeral, so the
        // deterministic tier reports extraction unavailable and the only available evidence left is
        // the model's own answer. A probabilistic negative cannot authorise delivery on its own, so
        // the tier holds rather than allowing on a calm reading of a message it could not read.
        Assert.Equal(MailAction.Hold, assessment.Action);
        Assert.Contains(assessment.Reasons, r =>
            r.Code == "policy.allow_uncorroborated_by_deterministic_evidence");

        // And the reason the gate fired is visible in the evidence itself: the deterministic tier
        // said it could not extract, which is why no checkable row was there to corroborate.
        Assert.Contains(assessment.Evidence, e =>
            e.SignalId == AssessmentEvidenceIds.DeterministicExtractionUnavailable);
        Assert.NotEmpty(assessment.RiskDimensions);

        // The version stamp is on every assessment, including this one: reuse has to be visible, and
        // so does the model that answered.
        Assert.Equal("jev-1.13.0", assessment.Versions.ClassifierModelVersion);
        Assert.Equal(SemanticDimensions.QuestionSchemaVersion, assessment.Versions.QuestionSchemaVersion);
    }

    [Fact]
    public async Task ADecisionCarriesTheInputsToItsOwnArithmetic()
    {
        // Decision 37. A decision publishes an index and a row per dimension, and the two extra
        // facts exist so a consumer can check the first from the rest instead of reconstructing a
        // different index from the same rows. That reconstruction is not hypothetical: while the
        // denominator and the counted flag were missing it returned the pre-decision-31 value, and
        // returned it with confidence, because a masked row and a measured zero rendered alike.
        var harness = Build();
        var message = Submittable();
        harness.Payloads.Add(message.Envelope.PayloadReference, Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        // `is true` rather than a bare read: the flag is nullable so that a decision stored before the
        // arithmetic existed can say it was not recorded (decision 37). A decision this build just made
        // always records it, so the difference is not exercised here, and reading through the nullable
        // is what keeps the test compiling against a row shape it does produce.
        var counted = assessment.RiskDimensions.Where(d => d.Counted is true).ToList();

        // Non-vacuous: a test that asserted the sums over an empty set would pass on a decision
        // whose index came from somewhere else entirely.
        Assert.NotEmpty(counted);
        Assert.True(assessment.RiskIndexDenominator > 0);

        // A decision this build just made records the arithmetic on every row. Null would mean the
        // assessor published a decision the index arithmetic was never written onto, which is this
        // test's own defect rather than the legacy-row case the nullable exists for. Stated here so
        // the reads below are of a shape that was checked, not forgiven.
        Assert.NotNull(assessment.RiskIndexDenominator);
        Assert.All(assessment.RiskDimensions, d =>
        {
            Assert.NotNull(d.Weight);
            Assert.NotNull(d.Counted);
        });

        // The denominator is exactly the weight of the rows that were counted, so which rows are in
        // the arithmetic is read from the response rather than guessed at.
        Assert.Equal(
            assessment.RiskIndexDenominator.Value,
            counted.Sum(d => d.Weight!.Value),
            precision: 12);

        // And the index is what those rows produce, clamped the way the scorer clamps.
        var numerator = counted.Sum(d => Math.Clamp(d.Score, 0.0, 1.0) * d.Weight!.Value);
        Assert.Equal(
            assessment.RiskIndex,
            numerator / assessment.RiskIndexDenominator.Value,
            precision: 12);

        // Every row left out is left out for a stated reason: not measured, or measured and excluded
        // on purpose. An Available row that was not counted and says nothing is the defect this test
        // exists to catch, because no reader can tell it from a measured zero.
        foreach (var excluded in assessment.RiskDimensions.Where(d => d.Counted is not true))
        {
            Assert.True(
                excluded.Availability != EvidenceAvailability.Available || excluded.ExclusionReason is not null,
                $"{excluded.Name} is Available, was not counted, and gives no reason.");
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(1000.0)]
    public async Task The_served_count_row_reproduces_the_scorers_step_for_the_same_input(double count)
    {
        // Decision 42's arithmetic, asked of the served row rather than of the function. The scorer
        // normalises a count with `DeterministicFindings.Normalise` (a bounded step, 1.0 at one or
        // more) while the row it publishes carries the signal's own value, so the two numbers a
        // reader has are the magnitude and the weight. Reproducing the index from them means
        // clamping, exactly as `ADecisionCarriesTheInputsToItsOwnArithmetic` does, and this test is
        // the reason that clamp is not decoration: at a count of two the published score is 2.0 and
        // the contribution the scorer used is 1.0 * weight, so an unclamped product is double.
        //
        // The step and the clamp agree over this whole domain, which is why the assertion below can
        // hold, and they agree only because a count is a non-negative integer. `0.5` is deliberately
        // absent from the cases: there they diverge (0.5 against 0.0) and no message can produce it,
        // which is pinned at the function in `DeterministicFindingUnitTests` rather than pretended
        // at the route here.
        var harness = Build();
        harness.Mime.EvidenceSignalId = DeterministicFindings.TrustedAuthenticationFailure;
        harness.Mime.EvidenceValue = count;

        var message = Submittable();
        harness.Payloads.Add(message.Envelope.PayloadReference, Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        var row = Assert.Single(
            assessment.RiskDimensions,
            d => d.Name == DeterministicFindings.TrustedAuthenticationFailure);

        // Counted at zero as well as at one: a finding that is measured and says "none" is a
        // measurement, and dropping the row instead would make the denominator depend on the
        // answer rather than on the question.
        Assert.True(row.Counted is true);
        Assert.NotNull(row.Weight);

        // The value the row publishes, which is the input to the reader's model and not the step.
        Assert.Equal(count, row.Score, precision: 12);

        Assert.Equal(
            Math.Clamp(row.Score, 0.0, 1.0) * row.Weight!.Value,
            DeterministicFindings.Normalise(count, SignalUnit.Count) * row.Weight!.Value,
            precision: 12);
    }

    [Fact]
    public async Task AnAssessmentThatMeasuredNothingPublishesAnEmptyArithmeticRatherThanAQuietOne()
    {
        // The outage shape, and the reason the denominator is published as a number rather than
        // left implicit. Every weighted dimension is unmeasured, so the index is 0.0, and a reader
        // that divides by the published denominator is dividing by zero rather than reading a calm
        // message. The distinction only exists if the denominator travels.
        var harness = Build(classifierUnavailable: true);
        var message = Submittable();
        harness.Payloads.Add(message.Envelope.PayloadReference, Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        Assert.DoesNotContain(assessment.RiskDimensions, d => d.Counted is true);
        Assert.Equal(0.0, assessment.RiskIndexDenominator);
        Assert.Equal(0.0, assessment.RiskIndex);
    }

    // THE CONTROL for the three assertions above, added after an audit of this suite's negatives.
    // All three are also satisfied by a decision that published no dimensions at all: an empty list
    // has nothing counted, and an empty arithmetic divides to zero and publishes zero. That is the
    // quiet shape this test's name claims to tell apart from a measured one, so the dimensions have
    // to be present before their not being counted means anything.
    //
    // Kept as a separate test rather than folded in, because the property is about the shape of the
    // outage rather than about the arithmetic, and a reader looking for "did anything get asked"
    // should be able to run that question on its own.
    [Fact]
    public async Task AnOutageLeavesTheDimensionsPresentSoTheZeroIsMeasuredRatherThanEmpty()
    {
        var harness = Build(classifierUnavailable: true);
        var message = Submittable();
        harness.Payloads.Add(message.Envelope.PayloadReference, Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            message,
            Builders.Context(harness.Clock, assessmentOnly: true),
            CancellationToken.None);

        var semantic = assessment.RiskDimensions
            .Where(d => d.Name.StartsWith("semantic.", StringComparison.Ordinal))
            .ToList();

        // Every configured question is present, and every one of them says the provider did not
        // answer rather than that the question did not apply. The second half is what separates a
        // blackout from a deployment that never asks.
        Assert.Equal(SemanticDimensions.All.Count, semantic.Count);
        Assert.All(semantic, d => Assert.False(d.Counted is true));

        // What is NOT claimed here, deliberately: that the deterministic rows are counted in this
        // shape. Written that way first and it was false, which is what running it is for. This test
        // pins the presence of the questions, because absence is the reading a zero cannot tell
        // apart from a blackout; the deterministic contribution under an outage is a different
        // question and belongs to the test that measures it.
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

        // THE CONTROL: Assert.All over an empty list proves nothing, and "unavailable evidence
        // propagates as unavailable" is exactly what an empty list would satisfy for free. The
        // assertions below only mean something once there is evidence for them to be about.
        Assert.NotEmpty(semantic);

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
    public async Task ALocalOnlyDeploymentThatDeclaresItNeverAsksIsAllowedOnItsOwnEvidence()
    {
        // A tenant that forbids external content processing has no semantic state to report, and the
        // claim that it may still deliver needs three parts, not two. The composition root stops
        // declining responsibility for an outage that is not an outage; the coverage floor is
        // stated; and the composition says, in the evidence, that the questions were never asked.
        // The third is the one this test exists for, and it is what the first two were standing in
        // for.
        var options = Builders.Options() with
        {
            // Part one. This knob is inert once nothing is Unavailable, which is itself the point:
            // the deferral exists for an outage and this deployment has none.
            DeclineResponsibilityOnSemanticOutage = false,

            // Part two, and the reason it is written down rather than dropped. Zero was once enough
            // on its own to let a local-evidence deployment through, and it is not any more: policy
            // will not convert an unanswered semantic question into a delivery whatever the floor
            // says, which is the rule that holds every deployment whose provider is down. The floor
            // is set here so this test records that the allow no longer rests on it.
            Policy = new PolicyOptions { MinimumCoverageForAllow = 0 },
        };

        // Part three. The production tier, not the stub in another state.
        var harness = Build(options, neverAsks: true);
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Allow, assessment.Action);

        // The shape that carries it, asserted in both directions because only the pair is a proof:
        // every semantic question is NotApplicable (it exists and was never put) and none is
        // Unavailable (asked, and nothing came back). "Not Unavailable" alone would also pass a
        // deployment that emitted no semantic rows at all, and the two rows mean opposite things.
        var semantic = assessment.Evidence
            .Where(e => e.Origin == EvidenceOrigin.Semantic)
            .ToList();

        Assert.NotEmpty(semantic);
        Assert.All(semantic, row => Assert.Equal(EvidenceAvailability.NotApplicable, row.Availability));

        // And the gate that holds a deployment whose provider is down is not what produced this.
        // That gate is the whole of the difference between the two deployments, so stating that it
        // did not fire is stating which deployment this was.
        Assert.DoesNotContain(assessment.Reasons, r => r.Code == "policy.allow_without_a_semantic_answer");
        Assert.Contains(assessment.Reasons, r => r.Code == "policy.risk_below_threshold");
    }

    [Fact]
    public async Task TheNeverAskingDeclarationAloneDoesNotClearTheCoverageFloor()
    {
        // The boundary of what declaring it never asks buys, and it is a boundary about measurement
        // rather than about the declaration. Ruling (i) removes a semantic NotApplicable row from
        // the denominator along with the numerator, and this fixture's only weighted rows are
        // semantic, so the counted set is empty: not "asked and unanswered" but "never posed".
        // Coverage is 0 over 0, reported as 0, and the hold is coverage rather than the semantic
        // blackout, which is the difference that matters: it is not being mistaken for an outage.
        //
        // The assertions here do not separate that rule from the one it replaced, and saying so is
        // worth more than leaving it for a reader to discover. Before (i) this fixture held too: the
        // numerator was empty either way, and the backbone sitting in the denominator moved the
        // fraction without moving the outcome. The declaration's effect is only visible once a
        // deterministic row has been measured, which is the test below.
        //
        // Written down because the other reading ("it declares never-asks, so it may deliver") is the
        // one a later reader would otherwise assume, and would then look for the floor's absence as a
        // bug.
        var harness = Build(neverAsks: true);
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        Assert.Equal(MailAction.Hold, assessment.Action);
        Assert.Contains(assessment.Reasons, r => r.Code == "policy.insufficient_coverage_to_allow");
        Assert.DoesNotContain(assessment.Reasons, r => r.Code == "policy.allow_without_a_semantic_answer");
    }

    [Fact]
    public async Task ANeverAskingDeploymentClearsTheAllowFloorOnAMeasuredDeterministicRowAlone()
    {
        // What the declaration buys, and the movement ruling (i) makes. This is the boundary test
        // above with one change: the MIME analyzer emits a real weighted finding id instead of the
        // stub's own, so one deterministic question is in the weight table and was answered. That
        // single row is the whole difference between the two tests.
        //
        // Before (i) the 7.3 semantic backbone stayed in the denominator whatever its availability,
        // so coverage was 1.0 out of 8.3 and the deployment held at the default floor. That 8.3 is
        // applied arithmetic over the weight table and this fixture's rows, in the successor-arm
        // scratch run, and not a run of the pre-(i) binary: that rule
        // is no longer in the tree to run. After (i) the
        // semantic rows leave, the denominator falls to the deterministic weight alone, coverage is
        // 1.0 out of 1.0, and the default floor is cleared by a row that was measured and came back
        // clean. This is the product claim the ruling was taken for, and it is why the floor no
        // longer needs to be operator-settable: a deployment that declares it never asks can clear
        // the allow floor on deterministic evidence alone.
        //
        // The value is a measured zero on purpose. A mismatch at 1.0 would produce the same coverage
        // and a different action, which would leave this test resting on the scorer's threshold
        // rather than on the coverage floor it is about.
        var harness = Build(neverAsks: true);
        harness.Mime.EvidenceSignalId = DeterministicFindings.LinkDisplayMismatch;
        harness.Mime.EvidenceValue = 0.0;
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        var assessment = await harness.Assessor.AssessAsync(
            Submittable(),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // The counted set is that one row and nothing else. This is what "the denominator is the
        // posed deterministic weight alone" means, read off the published rows rather than
        // inferred: each row carries the weight it was configured with and whether it was counted.
        var counted = assessment.RiskDimensions.Where(row => row.Counted == true).ToList();
        var only = Assert.Single(counted);
        Assert.Equal(DeterministicFindings.LinkDisplayMismatch, only.Name);
        Assert.Equal(1.0, only.Weight);
        Assert.Equal(EvidenceAvailability.Available, only.Availability);

        // And the rows the ruling removed are still published, which is the half a fix that merely
        // stopped counting them would drop: every semantic dimension is present, marked uncounted,
        // and NotApplicable rather than Unavailable, so "never asked" has not decayed into "asked and
        // nothing came back".
        var semantic = assessment.RiskDimensions
            .Where(row => row.Name.StartsWith("semantic.", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(semantic);
        Assert.All(semantic, row => Assert.Equal(EvidenceAvailability.NotApplicable, row.Availability));
        Assert.All(semantic, row => Assert.False(row.Counted));

        Assert.Equal(MailAction.Allow, assessment.Action);
        Assert.DoesNotContain(assessment.Reasons, r => r.Code == "policy.insufficient_coverage_to_allow");
        Assert.DoesNotContain(assessment.Reasons, r => r.Code == "policy.allow_without_a_semantic_answer");
    }

    [Fact]
    public async Task ALocalOnlyDeploymentThatOnlyDeclaresOneKnobIsStillHeldRatherThanAllowed()
    {
        // The failure this guards against is a deployment that disables the deferral and then
        // believes it has opted out. It has not, and the reason is not the coverage floor: the
        // provider was asked here and answered nothing, so each semantic question arrived
        // Unavailable, and policy reads an unanswered question as a reason to hold whatever the
        // floor says. The honest outcome stays a bounded hold until the deployment declares that it
        // never asks, which is the tier the test above wires.
        //
        // Stated at the gate rather than at the symptom, because a comment naming coverage would
        // send a reader to change a number that cannot move this.
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
    public async Task ApprovedLearningPromotesRateFeaturesAndNotJustSemanticOnes()
    {
        var harness = Build(Builders.Options(assessmentPathLearningRuleId: "rule.replay-training/1"));
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        // Two messages, so the sender has a bucket with something in it by the time a sample is
        // promoted. The rate features exist ONLY in the bucket synthesis, so a sample built from this
        // message's semantic evidence alone can never model them, and the consequence is invisible:
        // the encoder reports the baselines as null and nothing here looks wrong.
        await harness.Assessor.AssessAsync(
            Submittable(Builders.Envelope(direction: MailDirection.Outbound)),
            Builders.Context(harness.Clock, correlationId: "corr-1"),
            CancellationToken.None);

        // The clock must move. `FeatureVector` adds the rate features only once some time has elapsed
        // in the bucket, because a rate over zero elapsed time is undefined rather than zero. Real
        // traffic advances the clock; a frozen test clock does not, and would silently produce a
        // sample with no rates and a green-looking assertion about nothing.
        harness.Clock.Advance(TimeSpan.FromSeconds(30));

        await harness.Assessor.AssessAsync(
            Submittable(Builders.Envelope(direction: MailDirection.Outbound, internalMessageId: "msg-2")),
            Builders.Context(harness.Clock, correlationId: "corr-2"),
            CancellationToken.None);

        var sender = harness.Profiles.Profiles.Single(p => p.Key.Scope == ProfileScopeKind.OutboundSender);

        // The baseline modelled the rate features, which is the whole point: without them the
        // classifier is told "40 messages this hour" with nothing to compare it against.
        Assert.True(
            sender.Baseline.Dimensions.ContainsKey(StyloMail.Adaptive.Temporal.FeatureIds.MessagesPerSecond),
            "the promoted baseline has no rate.messages_per_second, so no sender ever has a modelled "
            + "normal volume.");

        Assert.True(
            sender.Baseline.Dimensions.ContainsKey(StyloMail.Adaptive.Temporal.FeatureIds.RecipientsPerSecond));
    }

    [Fact]
    public async Task TheObservationCarriesPseudonymisedRecipientsAndNeverAddresses()
    {
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        await harness.Assessor.AssessAsync(
            Submittable(Builders.Envelope(
                direction: MailDirection.Outbound,
                recipients: ["alice@example.com", "bob@example.com"])),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        // Distinct *people*, not addresses. Passing these is what lets the profile answer novelty at
        // all; passing nothing leaves RecipientsNovelToSender null forever, and the encoder will not
        // invent a claim it cannot support.
        // Every profile this attempt touched records the same recipients: the sender's, and each
        // relationship's. The set is a property of the message, not of which profile is looking.
        Assert.All(harness.Profiles.Observations, o => Assert.Equal(2, o.RecipientKeys?.Count ?? 0));

        var keys = harness.Profiles.Observations[0].RecipientKeys!;

        // Pseudonymised, not raw. A profile that holds recipient addresses cannot honour a deletion
        // request without knowing every derived copy.
        Assert.DoesNotContain(keys, k => k.Contains("example.com", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.Contains("alice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ThePublicHelperBuildsASampleWithRateFeaturesForACaller()
    {
        // The helper exists because path 2 is the path that runs in production, and documenting a
        // pitfall is not the same as removing it: a caller who assembles their own vector omits rates
        // exactly as path 1 did, and gets a silently unmodelled baseline.
        var harness = Build();
        harness.Payloads.Add("spool://tenant-1/msg-1", Builders.RawMessage);

        await harness.Assessor.AssessAsync(
            Submittable(Builders.Envelope(direction: MailDirection.Outbound)),
            Builders.Context(harness.Clock),
            CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromSeconds(30));

        // By identity, not by key. A caller who assembles the key from the raw address names a
        // profile that does not exist and gets a dimension-less sample that teaches nothing:
        // silently. The overload exists so the caller never has to know about the pseudonym.
        var sample = harness.Assessor.BuildTrustedSample(
            "tenant-1",
            MailDirection.Outbound,
            "principal-1",
            harness.Clock.GetUtcNow(),
            LabelProvenance.AuthenticatedOperator,
            label: "operator review");

        Assert.NotEmpty(sample.Dimensions.Dimensions);
        Assert.Contains(
            sample.Dimensions.Dimensions,
            d => d.DimensionId == StyloMail.Adaptive.Temporal.FeatureIds.MessagesPerSecond);

        // And it did not decide what to teach. Provenance carries authority and stays the caller's.
        Assert.Equal(LabelProvenance.AuthenticatedOperator, sample.Provenance);
        Assert.Equal("operator review", sample.Label);
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
