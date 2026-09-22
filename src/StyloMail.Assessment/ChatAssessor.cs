using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Signals;
using StyloMail.Chat;
using StyloMail.Core;
using StyloMail.Policy;

namespace StyloMail.Assessment;

/// <summary>
/// The chat assessment path: what can be established about a chat message, and the one thing it can
/// never claim.
/// </summary>
/// <remarks>
/// <para>
/// <b>Observe only.</b> Nothing here takes an action: the policy decision is recorded so the audit
/// trail exists to decide on later, and <see cref="MailAssessment.Action"/> stays
/// <see cref="MailAction.Allow"/> because no action was taken. A reader must never have to infer
/// that from the absence of an intervention.
/// </para>
/// <para>
/// <b>Everything is post-hoc.</b> Email lets this system decide before delivery; a normal Slack app
/// is told about a message after the platform has already delivered it. The assessment therefore
/// states <see cref="DeliveryTiming.PostDelivery"/> as a fact rather than leaving a reader to infer
/// that the system could have stopped something it only reacted to.
/// </para>
/// <para>
/// <b>No semantic evidence, explicitly.</b> The classifier is opt-in per workspace and local-only by
/// default, so every semantic dimension is recorded <see cref="EvidenceAvailability.Unavailable"/>
/// with a reason. An absent entry would let a reader infer a clean result and a zero would state one
/// outright; both would present an uninformed judgement as a confident one.
/// </para>
/// <para>
/// <b>This composes the same engines the mail path composes rather than sharing extracted steps.</b>
/// The risk scorer and the policy engine are called directly, and <see cref="BuildRiskDimensions"/>
/// mirrors the mail path's. That duplication is deliberate and its risk is that the two paths could
/// compose policy differently with nothing noticing, which is what the cross-path drift test exists
/// to pin.
/// </para>
/// </remarks>
public sealed class ChatAssessor : IChatAssessor
{
    private readonly MailAssessorOptions _options;

    public ChatAssessor(
        IAdaptiveProfileStore profileStore,
        MailAssessorOptions options,
        IEmergencyKillSwitch? killSwitch = null)
    {
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _recorder = new ChatObservationRecorder(profileStore, options);
        _options = options;
        _killSwitch = killSwitch ?? NeverEngagedKillSwitch.Instance;
    }

    private readonly ChatObservationRecorder _recorder;

    private readonly IEmergencyKillSwitch _killSwitch;

    public ValueTask<MailAssessment> AssessAsync(
        ChatAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var clock = context.TimeProvider;
        var now = clock.GetUtcNow();

        var evidence = new List<Evidence>();

        // What the message itself supports, from the shared deterministic producer.
        evidence.AddRange(ChatEvidenceProducer.Produce(input));

        var gap = SemanticGap(now);
        evidence.AddRange(gap.Evidence);

        var behavioural = Behavioural(input, context, clock);
        evidence.AddRange(behavioural.Evidence);

        var risk = CompositeRiskScorer.Compute(evidence, _options.Policy.DimensionWeights);

        // The direction is derived from the author's relationship to the workspace, never fixed for
        // the channel: a member is an authenticated principal and belongs in the outbound pool,
        // which is the pool compromised-account detection is read from.
        var decision = new MailPolicyEngine(_options.Policy, clock).Decide(new PolicyInput
        {
            Evidence = evidence,
            Risk = risk,

            // The rest of the operator state the mail path reads through
            // IAssessmentPolicyContextSource is typed on MailAnalysisInput, so it cannot serve chat,
            // and these are stated rather than defaulted so each one is a claim somebody can disagree
            // with. The kill switch is the exception: it does not depend on the message, so it is
            // read through its own port rather than stated.
            Context = new PolicyContext
            {
                // Read rather than stated, because the switch is a system-wide control and an
                // operator who pulls it believes the system has stopped. A version that quietly did
                // not reach this surface would be that claim, made false.
                EmergencyKillSwitchEngaged = _killSwitch.IsEngaged,

                // Chat has no delivery responsibility, so no principal is spending a sending budget.
                OutboundQuotaExhausted = false,

                // No verified-rule engine reads chat messages yet.
                VerifiedSecurityRuleViolations = [],

                // False because no allowlist covers chat senders yet, not because one was checked and
                // failed. The conservative direction is the honest one while nothing is wired.
                AllowlistEntryValid = false,

                BaselineFrozenForSuspectedCompromise = false,
            },
            Direction = input.Membership.Direction,
        });

        // Written after the assessment is made and regardless of what it decided. Observed state is
        // what velocity and drift are later read from, so it records every message that was
        // assessed rather than the ones that turned out to be interesting.
        _recorder.Record(input, context.TenantId, now);

        return ValueTask.FromResult(new MailAssessment
        {
            AssessmentId = BuildAssessmentId(context, input),
            InternalMessageId = input.EventId,
            TenantId = context.TenantId,
            Channel = input.Channel,
            Evidence = evidence,
            RiskDimensions = BuildRiskDimensions(risk, evidence),
            RiskIndex = risk.Index,

            // No action was taken. The proposal is kept beside it so the audit trail can support a
            // decision about interventions later, without this path having performed one.
            Action = MailAction.Allow,
            ProposedActionInShadow = decision.Action,

            DeliveryTiming = DeliveryTiming.PostDelivery,
            Reasons = [.. gap.Reasons, .. behavioural.Reasons, .. decision.Reasons],
            Versions = BuildVersions(),
            Coverage = Coverage(input),

            // No queue, no acceptance, no recipients: chat has no delivery responsibility, so there
            // is no submission to name and no disposition to record per recipient.
            RecipientDispositions = [],
            AssessedAt = now,
        });
    }

    private BehaviouralOutcome Behavioural(
        ChatAnalysisInput input,
        AssessmentContext context,
        TimeProvider clock)
    {
        // The keys come from the recorder, so the read and the write cannot disagree about which
        // profile a message belongs to.
        var keys = _recorder.KeysFor(input, context.TenantId);

        // No traffic class is declared for a chat channel yet, and the mail path is explicit that
        // judging every sender against an expectation nobody named bakes one class's behaviour into
        // every other's. So the fan-out question is not asked here; velocity and drift still are.
        var evaluator = new BehaviouralEvidenceEvaluator(clock, _options.Adaptive);

        var evidence = new List<Evidence>();
        foreach (var key in keys)
        {
            evidence.AddRange(evaluator.Evaluate(_recorder.Read(key), key.Scope.ToString()));
        }

        return new BehaviouralOutcome(evidence, [], keys);
    }

    /// <summary>
    /// Writes this attempt into observed state, unconditionally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unconditional on the outcome, and that is the property to carry across from the mail
    /// path.</b> Observed state is what velocity and drift are computed from, so it has to record
    /// every message that was assessed rather than the ones that turned out interesting. A write
    /// that only happened for flagged traffic would make the baseline a record of what we found
    /// suspicious, which is the opposite of a baseline.
    /// </para>
    /// <para>
    /// The mail path counts attempts rather than deliveries for the same reason, and writes after
    /// everything the attempt turned out to be is known.
    /// </para>
    /// </remarks>
    private sealed record BehaviouralOutcome(
        IReadOnlyList<Evidence> Evidence,
        IReadOnlyList<ReasonCode> Reasons,
        IReadOnlyList<ProfileKey> Keys);

    /// <summary>
    /// Every semantic dimension, recorded as unavailable with the reason the whole gap exists.
    /// </summary>
    /// <remarks>
    /// One entry per configured dimension rather than a single "no semantics" entry, because the
    /// risk scorer weights dimensions individually and a reader asking about one of them must find
    /// the answer to that question rather than an explanation covering all of them.
    /// </remarks>
    private static (IReadOnlyList<Evidence> Evidence, IReadOnlyList<ReasonCode> Reasons) SemanticGap(
        DateTimeOffset now)
    {
        var evidence = new List<Evidence>(SemanticDimensions.All.Count);
        var signalIds = new List<string>(SemanticDimensions.All.Count);

        foreach (var dimension in SemanticDimensions.All)
        {
            signalIds.Add(dimension.Id);
            evidence.Add(new Evidence
            {
                SignalId = dimension.Id,
                Origin = EvidenceOrigin.Semantic,
                Availability = EvidenceAvailability.Unavailable,

                // No value. A zero would read as a measured, calm answer, which is the fabrication
                // the availability model exists to prevent.
                Value = null,
                Confidence = null,
                SourceVersion = SemanticDimensions.QuestionSchemaVersion,
                ObservedAt = now,
                ObservedScope = "message",
            });
        }

        return (evidence, [new ReasonCode
        {
            Code = AssessmentReasonCodes.SemanticUnavailable,
            Message =
                "This deployment assesses chat locally, so no semantic evidence was obtained. "
                + "Every semantic dimension is recorded unavailable rather than zero, and this "
                + "decision is an uninformed one rather than a confident one.",
            EvidenceSignalIds = signalIds,
        }]);
    }

    /// <summary>
    /// The dimensions that contributed, then those the scorer masked, mirroring the mail path.
    /// </summary>
    /// <remarks>
    /// The duplication with the mail path is deliberate. If the two ever disagree, the cross-path
    /// drift test fails rather than a reader having to notice two implementations had diverged.
    /// </remarks>
    private static IReadOnlyList<RiskDimension> BuildRiskDimensions(
        RiskIndexResult risk,
        IReadOnlyList<Evidence> evidence)
    {
        var dimensions = new List<RiskDimension>(risk.ContributingSignalIds.Count + risk.Masked.Count);

        foreach (var signalId in risk.ContributingSignalIds)
        {
            var source = evidence.FirstOrDefault(item =>
                string.Equals(item.SignalId, signalId, StringComparison.Ordinal));

            dimensions.Add(new RiskDimension
            {
                Name = signalId,
                Score = source?.Value ?? 0.0,
                Availability = EvidenceAvailability.Available,
                EvidenceSignalIds = [signalId],
            });
        }

        foreach (var masked in risk.Masked)
        {
            dimensions.Add(new RiskDimension
            {
                Name = masked.SignalId,
                Score = 0.0,
                Availability = masked.Availability,
                EvidenceSignalIds = [],
            });
        }

        return dimensions;
    }

    private AssessmentVersions BuildVersions() => new()
    {
        PolicyVersion = _options.Policy.Version,
        QuestionSchemaVersion = SemanticDimensions.QuestionSchemaVersion,
        PreprocessingVersion = _options.PreprocessingVersion,

        // Null because no classifier ran. Reporting a model id here would name a model that never
        // saw this message.
        ClassifierModelVersion = null,
    };

    private static AnalysisCoverage Coverage(ChatAnalysisInput input) => new()
    {
        // The body was read. Everything else is genuinely absent rather than unknown: a chat message
        // has no HTML alternative, no attachments and no parser to exceed a limit.
        BodyParsed = true,
        HtmlPresent = false,
        HasAttachments = false,
        HtmlTextDisagreement = false,
        ParserLimitExceeded = false,
        ContentEncrypted = false,
        Truncated = false,
        ConversationContextMissing = input.ConversationContext is null,
    };

    private static string BuildAssessmentId(AssessmentContext context, ChatAnalysisInput input) =>
        $"asm_{context.CorrelationId}_{input.EventId}";
}
