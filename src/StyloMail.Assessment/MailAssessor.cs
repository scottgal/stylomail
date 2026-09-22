using System.Security.Cryptography;
using System.Text;
using StyloMail.Adaptive.Learning;
using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Signals;
using StyloMail.Adaptive.Temporal;
using StyloMail.Assessment.Campaign;
using StyloMail.Assessment.Learning;
using StyloMail.Assessment.Semantic;
using StyloMail.Core;
using StyloMail.Mime;
using StyloMail.Policy;
using StyloMail.Queue;

namespace StyloMail.Assessment;

/// <summary>Signal ids the composition root itself produces.</summary>
public static class AssessmentEvidenceIds
{
    /// <summary>
    /// Deterministic extraction did not run, because no original bytes were available for this
    /// message.
    /// </summary>
    /// <remarks>
    /// Reported rather than omitted. A message assessed without deterministic evidence is a weaker
    /// assessment than one with it, and the difference has to be visible in the ledger, an absent
    /// signal reads as "nothing wrong found", which is the opposite of "nothing was looked at".
    /// </remarks>
    public const string DeterministicExtractionUnavailable = "assessment.deterministic_extraction";

    /// <summary>A mandatory limit was breached, so the message was refused before being assessed.</summary>
    public const string HardLimitViolation = "assessment.hard_limit_violation";

    /// <summary>
    /// The semantic classifier judged this message without any behavioural context about its sender.
    /// </summary>
    /// <remarks>
    /// <b>Recorded so a reader can tell an informed judgement from an uninformed one.</b> A classifier
    /// that saw only the words cannot answer "is this unusual <em>for this sender</em>", and an
    /// assessment made without that is not a weaker version of the same verdict. It is a different
    /// question's answer, and the ledger has to say which was asked.
    ///
    /// <para>
    /// Emitted as unavailable rather than omitted, for the same reason as
    /// <see cref="DeterministicExtractionUnavailable"/>: an absent signal reads as "nothing wrong
    /// found", which is the opposite of "nothing was known".
    /// </para>
    /// </remarks>
    public const string BehaviouralContextUnavailable = "assessment.behavioural_context";

    /// <summary>Producer version for everything the composition root emits.</summary>
    public const string SourceVersion = "assessment/1";
}

/// <summary>
/// Counters for the operator surface. Nothing here influences a decision.
/// </summary>
/// <remarks>
/// The recipient budget is a containment control, it bounds how much a compromised account can
/// send after we have failed to detect it, so a divergence between what this pipeline believes it
/// reserved and what the ledger actually gave back is a fact somebody needs, not a detail.
/// </remarks>
public sealed class MailAssessorStatistics
{
    private long _budgetReservations;
    private long _budgetReleases;
    private long _budgetReleaseShortfall;

    /// <summary>Recipient budgets reserved for outbound attempts.</summary>
    public long BudgetReservations => Interlocked.Read(ref _budgetReservations);

    /// <summary>Reservations given back because the recipient was never dispatched.</summary>
    public long BudgetReleases => Interlocked.Read(ref _budgetReleases);

    /// <summary>
    /// Releases that returned less than was reserved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Should be zero. A non-zero count means the pipeline's tally and the ledger's have diverged,     /// more was given back than this pipeline ever took, and the number bounding escape volume is
    /// no longer trustworthy. Counted rather than thrown because the case is a reconciliation
    /// problem, not a reason to fail a message.
    /// </para>
    /// <para>
    /// <b>There is a second, benign cause, and it is worth knowing before assuming divergence.</b>
    /// The ledger's budget rolls over on a window, so a reservation can expire between the reserve
    /// and its release, which would make a correct release look short. That needs the window to be
    /// shorter than the time an assessment takes, so it cannot happen at the default of an hour, but
    /// a deployment that tuned the window down to seconds should check that before treating a
    /// non-zero count as a defect.
    /// </para>
    /// </remarks>
    public long BudgetReleaseShortfall => Interlocked.Read(ref _budgetReleaseShortfall);

    internal void RecordReservation() => Interlocked.Increment(ref _budgetReservations);

    internal void RecordRelease() => Interlocked.Increment(ref _budgetReleases);

    internal void RecordReleaseShortfall() => Interlocked.Increment(ref _budgetReleaseShortfall);
}

/// <summary>Reason codes the composition root contributes to a decision.</summary>
public static class AssessmentReasonCodes
{

    /// <summary>Acceptance was refused, so delivery responsibility never transferred.</summary>
    public const string AcceptanceRefused = "assessment.acceptance_refused";

    /// <summary>Acceptance was not attempted because no original payload was available to persist.</summary>
    public const string NoPayloadForAcceptance = "assessment.no_payload_for_acceptance";

    /// <summary>No semantic evidence was available at all, so responsibility was declined.</summary>
    public const string SemanticUnavailable = "assessment.semantic_unavailable";

    /// <summary>
    /// An existing durable queue row already covered this submission.
    /// </summary>
    /// <remarks>
    /// <b>An explanation, not the fact.</b> Whether a submission was created or matched is carried
    /// by <see cref="MailAssessment.Submission"/>, because a caller choosing between <c>201</c> and
    /// <c>200</c> needs a fact and digging one out of prose is how phrasing comes to carry a meaning
    /// it does not have. This code stays because a replay is genuinely worth a line in the ledger,     /// "we were asked for this twice and answered once" is something an operator wants to see, and
    /// it is read for that reason, never to answer the question the field answers.
    /// </remarks>
    public const string SubmissionDuplicate = "assessment.submission.duplicate";
}

/// <summary>
/// The composition root: one assessment, from an envelope to a decision and a delivery state.
/// </summary>
/// <remarks>
/// <para>
/// This type owns no policy, no evidence and no storage of its own. It sequences components that
/// each own one of those things, and its correctness is largely a question of what it does
/// <em>not</em> do: it never scores a message, never chooses an action, and never lets one
/// component's output stand in for another's.
/// </para>
///
/// <para>
/// <b>Clock.</b> Every timestamp comes from <see cref="AssessmentContext.TimeProvider"/>, and the
/// policy engine and the behavioural evaluator are constructed per request from that same clock.
/// Nothing here reads the wall clock. That is what lets a replay with a fixed clock reproduce a
/// decision rather than merely approximate it, and it is why those engines are built per request
/// rather than cached as singletons.
/// </para>
///
/// <para>
/// <b>Where the pipeline's steps actually land.</b> The sequence below is the pipeline order. The
/// one deliberate arrangement inside it is the observed-counter write, which lands at step
/// three-and-a-half, after the comparison, before policy. It is placed there for two reasons. The
/// comparison must see the profile as it was <em>before</em> this message, or the message's own
/// evidence dilutes the deviation it is being measured against. And the observation carries this
/// message's semantic vector, which does not exist until step four has run. Reserving the outbound
/// budget still happens in step three, ahead of any provider spend. The counter write itself
/// happens exactly once, atomically, carrying everything the attempt turned out to be, including
/// whether it was refused.
/// </para>
/// </remarks>
public sealed class MailAssessor : IMailAssessor
{
    private readonly IMimeMessageAnalyzer _mime;
    private readonly IContextualSemanticClassifier _classifier;
    private readonly ProfileCoordinator _profiles;
    private readonly IMessageAcceptanceQueue _acceptance;
    private readonly IRawMessageSource _rawMessages;
    private readonly IAssessmentPolicyContextSource _policyContext;
    private readonly CampaignNearDuplicateDetector _campaign;
    private readonly SendingQuotaLedger _quotaLedger;
    private readonly TrustedLearningGate _learningGate;
    private readonly MailAssessorOptions _options;
    private readonly IReadOnlyList<string> _dimensionIds;

    public MailAssessor(
        IMimeMessageAnalyzer mimeAnalyzer,
        IContextualSemanticClassifier semanticClassifier,
        IAdaptiveProfileStore profileStore,
        IMessageAcceptanceQueue acceptanceQueue,
        MailAssessorOptions options,
        IRawMessageSource? rawMessages = null,
        IAssessmentPolicyContextSource? policyContext = null,
        RecentCampaignWindow? campaignWindow = null,
        SendingQuotaLedger? quotaLedger = null,
        TrustedLearningGate? learningGate = null)
    {
        ArgumentNullException.ThrowIfNull(mimeAnalyzer);
        ArgumentNullException.ThrowIfNull(semanticClassifier);
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(acceptanceQueue);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        _mime = mimeAnalyzer;
        _classifier = semanticClassifier;
        _profiles = new ProfileCoordinator(profileStore);
        _acceptance = acceptanceQueue;
        _options = options;
        _rawMessages = rawMessages ?? NullRawMessageSource.Instance;
        _policyContext = policyContext ?? StaticPolicyContextSource.Instance;
        CampaignWindow = campaignWindow
            ?? new RecentCampaignWindow(options.CampaignWindowCapacity, options.CampaignWindowRetention);
        _campaign = new CampaignNearDuplicateDetector(CampaignWindow, options.Campaign);
        _quotaLedger = quotaLedger ?? new SendingQuotaLedger(
            options.OutboundRecipientBudgetPerPrincipal,
            options.OutboundRecipientBudgetWindow);
        _learningGate = learningGate ?? new TrustedLearningGate();
        _dimensionIds = [.. options.Dimensions.Select(dimension => dimension.Id)];
    }

    /// <summary>The recent-campaign window in use, for the operator surface and for tests.</summary>
    public RecentCampaignWindow CampaignWindow { get; }

    /// <summary>Counters for the operator surface. Nothing here influences a decision.</summary>
    public MailAssessorStatistics Statistics { get; } = new();

    /// <summary>
    /// How profile state is being written: appends versus whole-profile decisions.
    /// </summary>
    /// <remarks>
    /// Exposed because a counter nobody can read is not instrumentation, it is a comment with a
    /// number attached, the same defect as a method with no callers.
    ///
    /// <para>
    /// <b>What this detects:</b> <see cref="ProfileWriteStatistics.Mutated"/> climbing at message
    /// rates means whole-profile writes have drifted onto the ingest path, which happens if learning
    /// is being committed per message rather than per authorised outcome, a real operational fact,
    /// since learning is the highest-value thing to capture in this system.
    /// </para>
    /// <para>
    /// <b>What it does not detect:</b> a caller misusing the coordinator, as opposed to the
    /// coordinator itself being changed. The counters record which operation this class chose, not
    /// which store method it reached. The store-level assertion in
    /// <c>ProfileCoordinatorTests</c> covers that, and it is the stronger of the two, do not read a
    /// clean <see cref="ProfileWriteStatistics"/> as proof the split is intact.
    /// </para>
    /// </remarks>
    public ProfileWriteStatistics ProfileWrites => _profiles.Statistics;

    public async ValueTask<MailAssessment> AssessAsync(
        MailAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var envelope = input.Envelope;
        RequireTenantMatch(envelope, context);

        var clock = context.TimeProvider;
        var now = clock.GetUtcNow();
        var assessmentId = BuildAssessmentId(context, envelope);

        var evidence = new List<Evidence>();
        var coverage = input.Coverage;
        var analysis = input;

        // ---- Step 1: validate the envelope, authorisation, size and parsing limits.
        var violations = AssessmentValidation.Validate(input, context, _options);

        // ---- Step 2: parse an analysis copy and extract deterministic evidence.
        var payload = await _rawMessages.TryGetAsync(envelope, cancellationToken).ConfigureAwait(false);

        if (payload is { } rawBytes)
        {
            var parsed = _mime.Analyze(new MimeAnalysisRequest
            {
                Envelope = envelope,
                RawMessage = rawBytes,
                Authentication = input.Authentication,
                ConversationContext = input.ConversationContext,
                TimeProvider = clock,
            });

            evidence.AddRange(parsed.Evidence);
            coverage = parsed.Coverage;

            if (parsed.IsAnalysable)
            {
                // The parser's view is authoritative: it was derived from the original bytes by the
                // component that owns parsing, and a caller-supplied view is not a substitute for
                // its evidence, though it stands in for the view itself when no bytes are available.
                analysis = parsed.Message!;
            }
            else
            {
                violations.Add(
                    AssessmentRules.ParseRejectedPrefix
                    + (parsed.Rejection?.Reason ?? parsed.Disposition.ToString()));
            }
        }
        else
        {
            evidence.Add(DeterministicExtractionUnavailable(now, "no original payload was available"));
        }

        var policyState = await _policyContext
            .GetAsync(input, context, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<(ProfileTarget Target, AdaptiveProfile Snapshot)> profiles = [];
        IReadOnlyList<Evidence> semanticEvidence = [];
        CacheProvenance? semanticProvenance = null;
        string? resolvedModelVersion = null;
        var dimensionVector = EvidenceVectors.Semantic([], _dimensionIds);
        var quotaExhausted = policyState.OutboundQuotaExhausted;
        BudgetReservation? reservation = null;
        IReadOnlyDictionary<string, IReadOnlyList<string>> recipientScopedSignals =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        if (violations.Count == 0)
        {
            // ---- Step 3: read profile snapshots, and reserve this attempt's budget.
            var targets = BuildProfileTargets(analysis, context);
            profiles = ReadSnapshots(targets);

            if (!context.AssessmentOnly && analysis.Envelope.Direction == MailDirection.Outbound)
            {
                // Reserved before the provider call, not after: a provider outage must not hand an
                // exhausted principal a free pass through the ceiling.
                var principal = analysis.Envelope.TrustedPrincipalId;
                var recipients = analysis.Envelope.RcptTo.Count;

                // The instant comes from the assessment, not from a clock the ledger owns. A ledger
                // holding its own clock could be given a different one than the requests it serves,
                // and a fixed-clock replay would then move the window with the wall clock, a
                // documented requirement nobody could enforce. With the timestamp passed in there is
                // no clock to be wrong about.
                if (_quotaLedger.TryReserve(context.TenantId, principal, recipients, now))
                {
                    Statistics.RecordReservation();

                    // Held so it can be given back if nothing ends up dispatched. The ledger's own
                    // contract is that a release is for recipients that were *never sent*, and a
                    // message the pipeline declined is exactly that.
                    reservation = new BudgetReservation(context.TenantId, principal, recipients);
                }
                else
                {
                    // No spend happened, so there is nothing to give back later. Releasing an
                    // unsuccessful reservation would return budget the principal never parted with,
                    // and, because exhaustion is what produced the deferral, the quota would
                    // un-exhaust itself on every message and never bind at all.
                    quotaExhausted = true;
                }
            }

            // ---- Step 4: obtain semantic evidence, exact cache hit, provider, or explicit unavailable.
            var behaviouralProfile = BuildBehaviouralProfile(
                profiles, now, HashedRecipients(analysis.Envelope, context));

            // Two shapes mean the same thing to a reader: no profile at all, and a profile that
            // positively says we looked and found nothing. `overview-` is explicit that BOTH must be
            // recorded, because either way the classifier judged the words without knowing the
            // sender. Recording only the null case made this marker unreachable: the encoder
            // returns the unavailable shape rather than null for an unknown principal, so the ledger
            // would have claimed an informed judgement on every cold-start message.
            if (behaviouralProfile is null || !behaviouralProfile.ProfileAvailable)
            {
                evidence.Add(BehaviouralContextUnavailable(now));
            }

            SemanticAssessment semantic;
            try
            {
                semantic = await _classifier
                    .ClassifyAsync(
                        new SemanticMailInput
                        {
                            Message = analysis,
                            Dimensions = _options.Dimensions,
                            TaggedContext = null,
                            // Part of the classifier input, and therefore part of the semantic cache
                            // key. The canonicaliser encodes it, and a tripwire test fails if Core
                            // adds a field that the encoder does not carry.
                            Profile = behaviouralProfile,
                        },
                        clock,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The attempt still happened, and observed state counts every attempt, including
                // one whose assessment blew up, which is exactly the traffic whose rate must be
                // bounded. Recorded with masked dimensions, never with zeros, and the fault then
                // continues to its caller rather than being absorbed here.
                UpdateObservedState(analysis, context, profiles, dimensionVector, wasRejected: true, now);
                throw;
            }

            semanticEvidence = semantic.Evidence;
            semanticProvenance = semantic.Cache;
            resolvedModelVersion = semantic.ResolvedModelVersion;
            evidence.AddRange(semanticEvidence);

            dimensionVector = EvidenceVectors.Semantic(semanticEvidence, _dimensionIds);

            // ---- Step 5: compare against the profile snapshots and recent campaign windows.
            var behavioural = Behavioural(profiles, policyState, clock);
            evidence.AddRange(behavioural.Evidence);
            recipientScopedSignals = behavioural.ByRecipient;
            evidence.AddRange(Campaign(analysis, context, assessmentId, dimensionVector, now));
        }
        else
        {
            // A mandatory limit was breached. The message is refused on established facts, so there
            // is no reason to spend provider budget or profile state on it, but the ledger must
            // record that the evaluation was skipped, rather than leaving a reader to infer a clean
            // result from an absent signal.
            evidence.Add(ViolationEvidence(violations, now));
        }

        // ---- Step 6: versioned deterministic policy, the only place an action is chosen.
        var risk = CompositeRiskScorer.Compute(evidence, _options.Policy.DimensionWeights);

        var decision = new MailPolicyEngine(_options.Policy, clock).Decide(new PolicyInput
        {
            Evidence = evidence,
            Risk = risk,
            Context = new PolicyContext
            {
                EmergencyKillSwitchEngaged = policyState.EmergencyKillSwitchEngaged,
                OutboundQuotaExhausted = quotaExhausted,
                VerifiedSecurityRuleViolations =
                    [.. policyState.VerifiedSecurityRuleViolations, .. violations],
                AllowlistEntryValid = policyState.AllowlistEntryValid,
                RecipientPrefersThisTrafficClass = policyState.RecipientPrefersThisTrafficClass,
                BaselineFrozenForSuspectedCompromise = policyState.BaselineFrozenForSuspectedCompromise
                    || profiles.Any(entry => entry.Snapshot.Baseline.IsFrozen),
            },
            Direction = analysis.Envelope.Direction,
        });

        var composedAction = decision.Action;
        var outageReasons = new List<ReasonCode>();

        if (_options.DeclineResponsibilityOnSemanticOutage && IsSemanticOutage(semanticEvidence))
        {
            // The provider was asked and answered nothing usable, so the risk index below was
            // computed over no weight at all and is zero for want of evidence, not for want of risk.
            // Zero sits under every threshold, and letting that become an allow is precisely the
            // outage-turning-into-a-verdict the availability model exists to prevent. Declining
            // responsibility is temporary, recoverable, and honest about what we do not know; the
            // unavailable evidence is preserved untouched for the ledger.
            composedAction = MailAction.Defer;
            outageReasons.Add(new ReasonCode
            {
                Code = AssessmentReasonCodes.SemanticUnavailable,
                Message =
                    "Semantic evidence was entirely unavailable, so no assessment of this message "
                    + "could be made. Responsibility is declined rather than assumed; retry when the "
                    + "provider is reachable.",
                EvidenceSignalIds =
                [
                    .. semanticEvidence
                        .Where(item => item.Availability == EvidenceAvailability.Unavailable)
                        .Select(item => item.SignalId),
                ],
            });
        }

        MailAction? proposedInShadow = null;
        var action = composedAction;

        if (context.ShadowMode)
        {
            // Shadow is a mode, not an action. The proposed intervention is recorded and forwarding
            // proceeds; nothing about the message's handling changes.
            proposedInShadow = composedAction;
            action = MailAction.Allow;
        }

        // ---- Step 3b: write this attempt into observed state, now that everything it turned out
        // to be is known. Counters count attempts, not deliveries, so refused traffic is included.
        if (violations.Count == 0)
        {
            UpdateObservedState(analysis, context, profiles, dimensionVector, action != MailAction.Allow, now);
        }

        // ---- Step 7: for submissions, durably accept, or decline responsibility before acceptance.
        // The assessor is the only component that accepts, a second acceptor would double-deliver,
        // and no amount of care at either call site fixes two components each believing they own it.
        var acceptance = await Step7Async(
            analysis,
            context,
            payload,
            action,
            decision,
            recipientScopedSignals,
            cancellationToken).ConfigureAwait(false);

        if (!acceptance.IsAccepted && acceptance.Reasons.Count > 0)
        {
            // Responsibility did not transfer, so the truthful outcome is a deferral, whatever
            // policy would have authorised had we been able to take the message on. Reporting
            // policy's action here would claim a delivery state that does not exist.
            action = MailAction.Defer;
        }

        // Give back a reservation that never turned into a dispatch. Without this the budget is
        // consumed by traffic that was refused or deferred, and since the ledger has no rolling
        // window, a legitimate principal reaches its ceiling once and is deferred permanently,         // a self-inflicted outage in which every message the quota blocked is itself the reason the
        // quota stays blocked.
        ReleaseUndispatchedBudget(reservation, acceptance, now);

        // ---- Step 8: commit trusted learning, only where authority exists. On the assessment path
        // there is no authorized outcome and, unless an operator has named a rule, no permitted rule
        // either, so nothing is learned. The gate is what refuses it, not this method's restraint.
        CommitAssessmentLearning(context, profiles, dimensionVector, now);

        return new MailAssessment
        {
            AssessmentId = assessmentId,
            InternalMessageId = envelope.InternalMessageId,
            TenantId = context.TenantId,
            Channel = analysis.Channel,
            Evidence = evidence,
            RiskDimensions = BuildRiskDimensions(risk, evidence),
            RiskIndex = risk.Index,
            Action = action,
            ProposedActionInShadow = proposedInShadow,
            DeliveryTiming = DeliveryTiming.PreAcceptance,
            Reasons = [.. outageReasons, .. decision.Reasons, .. acceptance.Reasons],
            Versions = BuildVersions(profiles, resolvedModelVersion),
            Coverage = coverage,
            Cache = semanticProvenance,
            // Non-null exactly when a durable queue row exists. Null is a meaningful "we did not
            // take this", assessment-only, a policy decline, or a refused acceptance, and the
            // caller's obligation to return a queue id depends on being able to tell.
            SubmissionId = acceptance.SubmissionId,
            Submission = acceptance.Submission,
            RecipientDispositions = acceptance.Dispositions,
            AssessedAt = now,
        };
    }

    /// <summary>
    /// Commits a trusted label to a profile, if it has the authority to be committed.
    /// </summary>
    /// <remarks>
    /// The one door through which learning happens, exposed so the feedback path has somewhere to
    /// call that is not the assessment path. The gate refuses anything an assessment could supply:
    /// without an authorized outcome or an explicitly named rule nothing is learned, and a label
    /// whose provenance carries no training authority is refused again by the profile itself.
    /// </remarks>
    public Task<TrustedLearningOutcome> CommitTrustedOutcomeAsync(
        TrustedLearningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var authorisation = _learningGate.Authorise(request);

        if (!authorisation.Attempted)
        {
            // Returned before the store is touched. The store's update always writes, it advances
            // the profile's revision even when the delegate changes nothing, and it holds the
            // database's single writer for the duration. A request refused for want of authority is
            // the most common case here, so it should cost neither.
            return Task.FromResult(authorisation);
        }

        return Task.FromResult(
            _profiles.Mutate(
                request.Key,
                request.Sample.RecordedAt,
                profile => authorisation with { Promotion = profile.Promote(request.Sample) }));
    }

    // ---------------------------------------------------------------------------------------------
    // Steps 1-2
    // ---------------------------------------------------------------------------------------------

    private static void RequireTenantMatch(MailEnvelope envelope, AssessmentContext context)
    {
        if (string.Equals(envelope.TenantId, context.TenantId, StringComparison.Ordinal))
        {
            return;
        }

        // Refused loudly rather than resolved quietly. Profiles, counters, quotas and cache keys are
        // all tenant-scoped, so choosing either value would attribute one tenant's traffic to the
        // other, and there is no reading of the pair under which that is intended.
        throw new ArgumentException(
            $"The envelope belongs to tenant '{envelope.TenantId}' but the assessment context names "
            + $"'{context.TenantId}'.", nameof(envelope));
    }

    private static Evidence DeterministicExtractionUnavailable(DateTimeOffset now, string cause) => new()
    {
        SignalId = AssessmentEvidenceIds.DeterministicExtractionUnavailable,
        Origin = EvidenceOrigin.Deterministic,
        Availability = EvidenceAvailability.Unavailable,
        Value = null,
        SampleSupport = null,
        SourceVersion = AssessmentEvidenceIds.SourceVersion,
        ObservedAt = now,
        ObservedScope = "message",
        Attributes = [new EvidenceAttribute { Name = "reason", Value = cause }],
    };

    /// <summary>
    /// True when the semantic layer was asked and produced nothing usable.
    /// </summary>
    /// <remarks>
    /// Distinguishes an outage from a message that was never sent to the classifier. An empty list,     /// the message was refused on an established fact before step four, is not an outage, and
    /// treating it as one would turn every hard-limit rejection into a deferral. A list containing
    /// <see cref="EvidenceAvailability.NotApplicable"/> entries is also not an outage: nothing was
    /// asked of the provider, so nothing was lost.
    /// </remarks>
    private static bool IsSemanticOutage(IReadOnlyList<Evidence> semanticEvidence) =>
        semanticEvidence.Count > 0
        && semanticEvidence.Any(item => item.Availability == EvidenceAvailability.Unavailable)
        && semanticEvidence.All(item => item.Availability
            is not (EvidenceAvailability.Available or EvidenceAvailability.ReducedCoverage));

    /// <summary>
    /// The behavioural profile this message is judged against, or null when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Encoded from the <b>sender's</b> snapshot, and from the observed state rather than the trusted
    /// baseline alone, which is `adaptive-`'s rule for what this describes: the classifier is being
    /// told what this principal has been doing, not what we have approved.
    /// </para>
    ///
    /// <para>
    /// <b>Null only when there is no sender snapshot to encode.</b> Note that this is distinct from
    /// the profile the encoder returns for a principal we know nothing about: that comes back with
    /// <c>ProfileAvailable: false</c> and every observation null, which is the positive statement
    /// that we looked and found nothing. Both reach the classifier as behavioural context; only the
    /// first leaves the assessment with no context at all. Core keeps the two apart and so does this.
    /// </para>
    /// </remarks>
    private BehaviouralProfile? BuildBehaviouralProfile(
        IReadOnlyList<(ProfileTarget Target, AdaptiveProfile Snapshot)> profiles,
        DateTimeOffset now,
        IReadOnlyList<string> messageRecipients)
    {
        var sender = profiles.FirstOrDefault(entry =>
            entry.Target.Scope is ProfileScopeKind.OutboundSender or ProfileScopeKind.InboundSenderIdentity);

        return sender.Snapshot is null
            ? null
            // `messageRecipients` is what makes novelty answerable at all: "have we ever addressed this
            // person" is a question about this message's recipients, not a property of the profile.
            : BehaviouralProfileEncoder.Encode(
                sender.Snapshot,
                now,
                messageRecipients,
                _options.Adaptive);
    }

    private static Evidence BehaviouralContextUnavailable(DateTimeOffset now) => new()
    {
        SignalId = AssessmentEvidenceIds.BehaviouralContextUnavailable,
        Origin = EvidenceOrigin.Deterministic,
        Availability = EvidenceAvailability.Unavailable,
        Value = null,
        SourceVersion = AssessmentEvidenceIds.SourceVersion,
        ObservedAt = now,
        ObservedScope = "message",
        Attributes =
        [
            new EvidenceAttribute
            {
                Name = "reason",
                Value = "no behavioural profile was available to the classifier",
            },
        ],
    };

    private static Evidence ViolationEvidence(IReadOnlyList<string> violations, DateTimeOffset now) => new()
    {
        SignalId = AssessmentEvidenceIds.HardLimitViolation,
        Origin = EvidenceOrigin.Deterministic,
        Availability = EvidenceAvailability.Available,
        Value = violations.Count,
        SampleSupport = violations.Count,
        SourceVersion = AssessmentEvidenceIds.SourceVersion,
        ObservedAt = now,
        ObservedScope = "message",
        // One attribute per rule, not a joined string: "three rules" and "one rule whose name
        // happens to contain commas" are different facts, and a list preserves which.
        Attributes = [.. violations.Select(v => new EvidenceAttribute { Name = "rule", Value = v })],
    };

    // ---------------------------------------------------------------------------------------------
    // Step 3
    // ---------------------------------------------------------------------------------------------

    /// <summary>One profile the attempt is measured against and counted into.</summary>
    /// <summary>
    /// Stands in for the null sender when a profile key is derived.
    /// </summary>
    /// <remarks>
    /// <b>A null sender is not an identity, it is the absence of one</b>, used by DSNs, and
    /// `MailEnvelope.MailFrom` documents it as such. It still has to be profiled, because bounce
    /// traffic counts toward observed rates like anything else, and it cannot be hashed as a blank
    /// string: the pseudonymizer rejects an empty identity, so hashing one threw.
    ///
    /// <para>
    /// Every null-sender message therefore shares a single bucket. That is the truthful model rather
    /// than a compromise, there is no identity there to tell apart, and `&lt;&gt;` is the standard
    /// notation for it and is not a valid address, so it cannot collide with a real one.
    /// </para>
    /// </remarks>
    private const string NullSenderIdentity = "<>";

    /// <summary>Substitutes <see cref="NullSenderIdentity"/> for an absent identity.</summary>
    /// <remarks>
    /// One place, because the failure it prevents is a throw rather than a wrong answer: an empty
    /// identity makes the pseudonymizer refuse, so a missed call site is a crash on legitimate mail
    /// rather than a subtly wrong profile. Three sites had this defect; a helper is what stops a
    /// fourth from being added quietly.
    /// </remarks>
    private static string NullSender(string? identity) =>
        string.IsNullOrWhiteSpace(identity) ? NullSenderIdentity : identity;

    /// <summary>
    /// One profile this attempt is measured against, and, for a pair, the recipient it describes.
    /// </summary>
    /// <remarks>
    /// The recipient is carried so that behavioural evidence can be attributed back to the
    /// recipient it was computed for. Without it the evidence is a flat list and every disposition
    /// has to report no recipient-scoped signals at all, which is a contract field silently
    /// always-null rather than an honest absence.
    /// </remarks>
    private sealed record ProfileTarget(ProfileScopeKind Scope, ProfileKey Key, string? Recipient = null);

    private IReadOnlyList<(ProfileTarget Target, AdaptiveProfile Snapshot)> ReadSnapshots(
        IReadOnlyList<ProfileTarget> targets) =>
        [.. targets.Select(target => (target, _profiles.Read(target.Key)))];

    /// <summary>
    /// The profiles this attempt belongs to.
    /// </summary>
    /// <remarks>
    /// Per-recipient relationship profiles are bounded, because a message with a thousand
    /// recipients would otherwise mean a thousand store round trips for one message, and the
    /// recipient count is attacker-controlled. The sender profile is not bounded and still records
    /// every recipient, so rate and fan-out evidence stay complete even when per-pair drift stops
    /// being computed. The bound costs detail; it never costs a counter.
    /// </remarks>
    private IReadOnlyList<ProfileTarget> BuildProfileTargets(MailAnalysisInput message, AssessmentContext context)
    {
        var envelope = message.Envelope;
        var senderKey = SenderKey(message, context);
        var targets = new List<ProfileTarget> { new(ProfileScopeKind.OutboundSender, senderKey) };

        if (envelope.Direction == MailDirection.Inbound)
        {
            targets[0] = new ProfileTarget(ProfileScopeKind.InboundSenderIdentity, senderKey);
        }

        foreach (var recipient in envelope.RcptTo.Take(_options.MaxRelationshipsObserved))
        {
            var recipientKey = _options.ProfileKeyHasher.Hash(context.TenantId, recipient);

            targets.Add(new ProfileTarget(
                ProfileScopeKind.Relationship,
                ProfileScopes.Relationship(context.TenantId, envelope.Direction, senderKey.Key, recipientKey),
                recipient));
        }

        return targets;
    }

    /// <summary>
    /// The profile key for the message's sender.
    /// </summary>
    /// <remarks>
    /// Built from the authenticated principal where there is one, and from the envelope's
    /// <c>MAIL FROM</c> otherwise. The <c>From</c> header is never consulted: it is supplied by the
    /// sender, and an identity a message can assert about itself is not an identity.
    /// </remarks>
    private ProfileKey SenderKey(MailAnalysisInput message, AssessmentContext context)
    {
        var envelope = message.Envelope;

        var identity = envelope.Direction == MailDirection.Outbound
            ? envelope.TrustedPrincipalId
            : envelope.MailFrom;

        var pseudonym = _options.ProfileKeyHasher.Hash(context.TenantId, NullSender(identity));

        return envelope.Direction == MailDirection.Outbound
            ? ProfileScopes.OutboundSender(context.TenantId, pseudonym)
            : ProfileScopes.InboundSender(context.TenantId, pseudonym, message.Authentication);
    }

    /// <summary>
    /// Folds this attempt into observed state.
    /// </summary>
    /// <remarks>
    /// Skipped entirely for assessment-only calls, which participate in no live accounting by
    /// definition. That is not a courtesy: an assessment-only caller that could advance observed
    /// rates would be a way to move the behavioural evidence real traffic is judged against without
    /// ever being responsible for a message.
    /// </remarks>
    private void UpdateObservedState(
        MailAnalysisInput message,
        AssessmentContext context,
        IReadOnlyList<(ProfileTarget Target, AdaptiveProfile Snapshot)> profiles,
        DimensionVector dimensions,
        bool wasRejected,
        DateTimeOffset now)
    {
        if (context.AssessmentOnly || profiles.Count == 0)
        {
            return;
        }

        var observation = new ProfileObservation
        {
            ObservedAt = now,
            // Every recipient, including those beyond the relationship bound. Quotas and rates count
            // recipients, and one message with five hundred recipients is five hundred chances to
            // reach somebody regardless of how many pair profiles we keep.
            RecipientCount = message.Envelope.RcptTo.Count,
            WasRejected = wasRejected,
            Dimensions = dimensions,
            // Distinct *people*, not addresses: one message to fifty colleagues is not fan-out, and
            // the profile cannot tell the two apart from a count alone. Hashed with the same
            // tenant-scoped pseudonym as every other identifier, so the profile never holds a raw
            // recipient address.
            RecipientKeys = HashedRecipients(message.Envelope, context),
        };

        foreach (var (target, _) in profiles)
        {
            _profiles.Observe(target.Key, observation, now);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Step 5
    // ---------------------------------------------------------------------------------------------

    /// <summary>Behavioural evidence, flat for scoring and attributed for per-recipient reporting.</summary>
    private sealed record BehaviouralResult(
        IReadOnlyList<Evidence> Evidence,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ByRecipient);

    private BehaviouralResult Behavioural(
        IReadOnlyList<(ProfileTarget Target, AdaptiveProfile Snapshot)> profiles,
        PolicyContextInput policyState,
        TimeProvider clock)
    {
        TrafficClassExpectation? expectation = policyState.TrafficClassName is { Length: > 0 } className
            ? new TrafficClassExpectation
            {
                TrafficClass = className,
                ExpectedRecipientsPerSecond = policyState.ExpectedRecipientsPerSecond,
                NoveltyTolerance = policyState.NoveltyTolerance,
                MinimumSupport = policyState.MinimumFanOutSupport,
            }
            : null;

        // No declared traffic class means no fan-out question. Judging every sender against an
        // expectation nobody named would bake one traffic class's behaviour into every other's.
        var evaluator = new BehaviouralEvidenceEvaluator(clock, _options.Adaptive);

        var evidence = new List<Evidence>();
        var byRecipient = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (target, snapshot) in profiles)
        {
            foreach (var item in evaluator.Evaluate(snapshot, target.Scope.ToString(), expectation))
            {
                evidence.Add(item);

                if (target.Recipient is { } recipient)
                {
                    if (!byRecipient.TryGetValue(recipient, out var signals))
                    {
                        signals = [];
                        byRecipient[recipient] = signals;
                    }

                    signals.Add(item.SignalId);
                }
            }
        }

        return new BehaviouralResult(
            evidence,
            byRecipient.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)[.. pair.Value.Distinct(StringComparer.Ordinal)],
                StringComparer.Ordinal));
    }

    private IEnumerable<Evidence> Campaign(
        MailAnalysisInput message,
        AssessmentContext context,
        string assessmentId,
        DimensionVector vector,
        DateTimeOffset now)
    {
        var fingerprint = SecurityBearingFingerprint.Compute(message);
        // Same null-sender substitution as the profile key. This was the third site with the same
        // defect: `ProfileKeyHasher` rejects an empty identity, and a DSN carries exactly that, so
        // every inbound bounce crashed here, and fixing the first site simply moved the crash to the
        // next one. Grepped for all three rather than letting the tests walk me through them.
        var senderScope = _options.ProfileKeyHasher.Hash(
            context.TenantId,
            NullSender(message.Envelope.MailFrom));

        // Recorded for assessment-only calls too, and the distinction is worth stating. What
        // assessment-only excludes is live traffic _accounting_, the observed-rate counters and the
        // recipient budget, which are the things that gate delivery and that a caller could
        // otherwise move without taking responsibility for a message. This window gates nothing: it
        // is bounded per tenant, it holds vectors and digests rather than assessments, and the only
        // thing it can produce is one more piece of evidence for policy to weigh. Excluding
        // assessment-only traffic from it would make campaign detection, a capability the spec
        // asks for, simply absent on the assessment path.
        return _campaign.ObserveAndEvaluate(
            context.TenantId,
            assessmentId,
            message.Envelope.InternalMessageId,
            now,
            vector,
            fingerprint,
            senderScope);
    }

    // ---------------------------------------------------------------------------------------------
    // Step 7
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// What step seven produced.
    /// </summary>
    /// <remarks>
    /// A submission id and a refusal reason are mutually exclusive by construction rather than by
    /// care: they are set only through the two factories below, so no edit can report an acceptance
    /// and a decline at the same time, and no caller can mistake one for the other.
    /// </remarks>
    private sealed record AcceptanceOutcome
    {
        public required IReadOnlyList<RecipientDisposition> Dispositions { get; init; }

        public required IReadOnlyList<ReasonCode> Reasons { get; init; }

        /// <summary>The durable queue id, non-null exactly when responsibility transferred.</summary>
        public string? SubmissionId { get; init; }

        /// <summary>
        /// How this submission related to what the queue already held.
        /// </summary>
        /// <remarks>
        /// Null exactly when <see cref="SubmissionId"/> is null, and set through the same factory so
        /// the two cannot disagree. <c>Accepted</c> below takes both from one place deliberately: two
        /// fields that must agree are a smell, and the remedy is to make the agreement structural
        /// here and tested above, not hoped for.
        /// </remarks>
        public SubmissionAdmission? Submission { get; init; }

        public bool IsAccepted => SubmissionId is not null;

        /// <summary>Acceptance was not attempted: assessment-only, or policy declined first.</summary>
        public static AcceptanceOutcome NotAttempted { get; } = new() { Dispositions = [], Reasons = [] };

        public static AcceptanceOutcome Accepted(
            IReadOnlyList<RecipientDisposition> dispositions,
            string queueId,
            bool alreadyExisted) => new()
            {
                Dispositions = dispositions,
                SubmissionId = queueId,
                Submission = alreadyExisted
                    ? SubmissionAdmission.Duplicate
                    : SubmissionAdmission.Created,
                // Only the replay is explained. A created submission needs no explanation, it is
                // the ordinary case, and a reason on every acceptance would push the reasons that
                // actually justify a decision further down a list documented as most-significant
                // first. The fact is on MailAssessment.Submission; this is colour, and only where the
                // colour is informative.
                Reasons = alreadyExisted
                    ?
                    [
                        new ReasonCode
                        {
                            Code = AssessmentReasonCodes.SubmissionDuplicate,
                            Message =
                                "A durable queue row already covered this submission; no new delivery "
                                + "was created and the existing queue id was returned.",
                            EvidenceSignalIds = [],
                        },
                    ]
                    : [],
            };

        public static AcceptanceOutcome Refused(ReasonCode reason) => new()
        {
            Dispositions = [],
            Reasons = [reason],
            SubmissionId = null,
        };
    }

    private async Task<AcceptanceOutcome> Step7Async(
        MailAnalysisInput analysis,
        AssessmentContext context,
        ReadOnlyMemory<byte>? payload,
        MailAction action,
        PolicyDecision decision,
        IReadOnlyDictionary<string, IReadOnlyList<string>> recipientScopedSignals,
        CancellationToken cancellationToken)
    {
        var envelope = analysis.Envelope;

        // Assessment-only means assessment only. No delivery state is created and none is implied,
        // which the empty disposition list records, a disposition is a claim about where a
        // recipient's copy has got to, and there is no copy.
        if (context.AssessmentOnly)
        {
            return AcceptanceOutcome.NotAttempted;
        }

        // Declining responsibility is policy's answer and it is given before acceptance rather than
        // after, so there is nothing to have to undo.
        if (action is MailAction.Defer or MailAction.Reject)
        {
            return AcceptanceOutcome.NotAttempted;
        }

        // The durability invariant, asserted where it matters. A non-durable reference reaching the
        // acceptance path is a caller that wired the assessment path into the delivery path, and
        // refusing here is still safe, discovering it after a 250 is not.
        PayloadReferences.RequireDurable(envelope.PayloadReference);

        if (payload is not { Length: > 0 } bytes)
        {
            // We cannot durably persist what we do not have. Deferring declines responsibility
            // temporarily and is recoverable; accepting would manufacture mail we could not produce.
            //
            // A reference that passes the scheme check but names no stored payload is worth calling
            // out separately, because it is not an ordinary "no payload", it is a caller that
            // believes it spooled something. That difference cost an hour at the host seam once
            // already: `spool://pending` satisfies the durability check and resolves to nothing, so
            // every message deferred for a reason that looked like a storage fault.
            var namesNothing = PayloadReferences.IsDurable(envelope.PayloadReference);

            return AcceptanceOutcome.Refused(new ReasonCode
            {
                Code = AssessmentReasonCodes.NoPayloadForAcceptance,
                Message = namesNothing
                    ? $"The payload reference '{envelope.PayloadReference}' is durable in form but "
                      + "names no stored payload, so nothing was accepted and responsibility was "
                      + "declined. This is a caller that believes the payload was spooled: check the "
                      + "spool write, not the storage."
                    : "The original payload was not available, so acceptance could not be made "
                      + "durable and responsibility was declined instead.",
                EvidenceSignalIds = [],
            });
        }

        var admissions = envelope.RcptTo
            .Select(recipient => new RecipientAdmission
            {
                Recipient = recipient,
                State = action switch
                {
                    MailAction.Hold => DeliveryState.Held,
                    MailAction.Quarantine => DeliveryState.Quarantined,
                    _ => DeliveryState.Queued,
                },
                // A hold without a deadline is an indefinite retention, which the spec forbids.
                ReEvaluateBy = action == MailAction.Hold ? decision.ReEvaluateBy : null,
            })
            .ToList();

        QueueAcceptResult accepted;
        try
        {
            accepted = await _acceptance
                .AcceptAsync(
                    new QueueSubmission
                    {
                        TenantId = context.TenantId,
                        InternalMessageId = envelope.InternalMessageId,
                        Direction = envelope.Direction,
                        TrustedPrincipalId = envelope.TrustedPrincipalId,
                        MailFrom = envelope.MailFrom,
                        MimeDigest = envelope.MimeDigest,
                        Payload = bytes,
                        Recipients = admissions,
                        UntrustedMessageIdHeader = envelope.UntrustedMessageIdHeader,
                        // Copied faithfully, INCLUDING null, which is not the same as zero. Null means
                        // "no hop count was observed", and the queue records that as the loop backstop
                        // not having run; zero would be a claim that we looked and found no prior
                        // hops. Collapsing the two here would turn "we did not check" into "there were
                        // no hops", the failure this field exists to avoid.
                        //
                        // Observed by the ingress, which is the only layer that sees the wire. This
                        // class does not derive it: counting Received: headers would mean
                        // reimplementing parsing that mime- owns, from raw bytes the pipeline should
                        // not be reading.
                        HopCount = envelope.HopCount,
                        // The caller's key, passed through unchanged, never one we mint.
                        //
                        // This was `assessmentId`, and that was a bug rather than a naming choice:
                        // an assessment id is fresh on every attempt, so a client retrying a
                        // submission it had already sent created a *new* queue entry every time and
                        // the queue had nothing to dedupe against. The key is the caller's replay
                        // contract; minting one silently replaces that contract with a promise we
                        // cannot keep.
                        //
                        // Null means the caller supplied none, assessment-only traffic, or a caller
                        // not participating in replay. Deliberately no fallback: inventing a key here
                        // would look like replay protection while providing none, which is worse than
                        // an honest absence.
                        IdempotencyKey = context.ClientIdempotencyKey,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SpoolUnavailableException)
        {
            // Durable storage was unavailable. That is exactly the condition under which accepting
            // would destroy mail, so the queue raised rather than returning, and responsibility is
            // declined here rather than reported as accepted.
            return AcceptanceOutcome.Refused(
                AcceptanceRefused("Durable storage was unavailable, so the message was not accepted."));
        }

        if (!accepted.IsAccepted)
        {
            return AcceptanceOutcome.Refused(AcceptanceRefused(
                $"The queue declined acceptance ({accepted.Admission}): "
                + $"{accepted.Detail ?? "no detail supplied"}."));
        }

        // IsAccepted is true for a duplicate replay too, and QueueId is then the *existing* item's
        // id, which is exactly what the caller must be handed back. Returning the id of the copy we
        // did not create would point the client at a queue item that does not exist.
        return AcceptanceOutcome.Accepted(
            BuildDispositions(admissions, action, decision, recipientScopedSignals),
            accepted.QueueId!,
            alreadyExisted: accepted.Admission == QueueAdmission.DuplicateSubmission);
    }

    private static ReasonCode AcceptanceRefused(string message) => new()
    {
        Code = AssessmentReasonCodes.AcceptanceRefused,
        Message = message + " Responsibility did not transfer and the caller may retry.",
        EvidenceSignalIds = [],
    };

    private static IReadOnlyList<RecipientDisposition> BuildDispositions(
        IReadOnlyList<RecipientAdmission> admissions,
        MailAction action,
        PolicyDecision decision,
        IReadOnlyDictionary<string, IReadOnlyList<string>> recipientScopedSignals) =>
    [
        .. admissions.Select(admission => new RecipientDisposition
        {
            Recipient = admission.Recipient,
            // Recipient-scoped risk is not differentiated yet: there is no per-recipient scoring
            // model in the pipeline, and inventing one here would put a second, unversioned scoring
            // path beside the one policy owns. The message-level index is reported, which is the
            // honest value rather than a fabricated per-recipient one.
            RecipientRisk = decision.RiskIndex,
            Action = action,
            DeliveryState = admission.State,
            ReEvaluateBy = admission.ReEvaluateBy,
            // The novelty, relationship and interaction signals that were computed for this pair.
            // Null rather than empty when this recipient has no pair profile, the relationship
            // bound means a message past it has none, because "no recipient-scoped signals were
            // computed" and "we computed none for this recipient" are different facts.
            RecipientScopedSignalIds = recipientScopedSignals.TryGetValue(admission.Recipient, out var signals)
                ? signals
                : null,
        }),
    ];

    // ---------------------------------------------------------------------------------------------
    // Step 8
    // ---------------------------------------------------------------------------------------------

    private void CommitAssessmentLearning(
        AssessmentContext context,
        IReadOnlyList<(ProfileTarget Target, AdaptiveProfile Snapshot)> profiles,
        DimensionVector dimensions,
        DateTimeOffset now)
    {
        if (context.AssessmentOnly || profiles.Count == 0)
        {
            return;
        }

        var ruleId = _options.AssessmentPathLearningRuleId;

        if (string.IsNullOrWhiteSpace(ruleId))
        {
            // The ordinary case, and the safe one: an assessment carries no authorized outcome, so
            // it teaches nothing. Recorded here as an explicit decision rather than as an absent
            // call, so the default is visible at the point the mission's step eight lives.
            return;
        }

        var sample = new TrustedSample
        {
            Dimensions = dimensions,
            Provenance = LabelProvenance.AuthorizedRule,
            RecordedAt = now,
            Label = ruleId,
        };

        foreach (var (target, snapshot) in profiles)
        {
            // The vector promoted is the window's feature vector, not this message's semantic
            // readings. A trusted baseline has to model the sender's behaviour over time, and the
            // `rate.*` features exist ONLY in the bucket synthesis: promoting semantic dimensions
            // alone leaves every rate unmodelled, so the baseline encodes as null and the classifier
            // is told "40 messages this hour" with nothing to compare it against.
            //
            // The snapshot's series is the pre-event one, which is deliberate on two counts: the
            // message being judged must not vouch for its own trust, and the window it contributes to
            // is the history it was measured against rather than one it just moved.
            var authorisation = _learningGate.Authorise(new TrustedLearningRequest
            {
                Key = target.Key,
                Sample = sample,
                AuthorizedOutcomePresent = false,
                AuthorizedRuleId = ruleId,
                AssessmentOnly = context.AssessmentOnly,
                ClaimedProvenance = LabelProvenance.AuthorizedRule,
            });

            if (!authorisation.Attempted)
            {
                continue;
            }

            _profiles.Mutate(
                target.Key,
                now,
                profile => profile.Promote(sample with { Dimensions = LearningVector(snapshot, now) }));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Result assembly
    // ---------------------------------------------------------------------------------------------

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
                // The dimension's own value, not the aggregate index. Reporting the index once per
                // dimension would present one number twelve times and call it twelve findings.
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
                // Masked dimensions carry no score at all. A zero here would read as "measured, and
                // calm", which is the fabrication the availability model exists to prevent.
                Score = 0.0,
                Availability = masked.Availability,
                EvidenceSignalIds = [],
            });
        }

        return dimensions;
    }

    private AssessmentVersions BuildVersions(
        IReadOnlyList<(ProfileTarget Target, AdaptiveProfile Snapshot)> profiles,
        string? resolvedModelVersion) => new()
        {
            PolicyVersion = _options.Policy.Version,
            // The resolved model, never the configured alias. When no semantic call was made, a
            // message refused on a hard limit, there is no resolved version to report, and the
            // configured one would be a claim about a call that never happened.
            ClassifierModelVersion = resolvedModelVersion,
            QuestionSchemaVersion = _options.SemanticCache.QuestionSchemaVersion,
            PreprocessingVersion = _options.PreprocessingVersion,
            ProfileVersions = profiles.Count == 0
                ? null
                : profiles
                    .Select((entry, index) => new KeyValuePair<string, string>(
                        $"{entry.Target.Scope}#{index}",
                        entry.Snapshot.Baseline.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            RegimeId = profiles.Count > 0 ? profiles[0].Snapshot.CurrentRegimeId : null,
        };

    /// <summary>
    /// Builds a trusted sample for a sender, with the dimensions a baseline actually needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Use this rather than assembling <see cref="TrustedSample.Dimensions"/> yourself.</b> The
    /// `rate.*` features are synthesised per bucket and appear in no per-message evidence, so a sample
    /// built from what the pipeline observed for one message carries none of them. The consequence is
    /// invisible: the baseline simply reports those dimensions as null, no fan-out movement is ever
    /// named, and the classifier is told a sender sent forty messages this hour with nothing to
    /// compare it against.
    /// </para>
    ///
    /// <para>
    /// <b>This does not decide what to teach.</b> Provenance and label are the caller's, and they are
    /// the fields that carry authority: this supplies only the measurement, correctly. A helper that
    /// chose provenance would be a helper that could open the learning gate.
    /// </para>
    ///
    /// <para>
    /// The key is built the way the pipeline builds it: `ProfileScopes.OutboundSender` or
    /// `ProfileScopes.InboundSender` over a `ProfileKeyHasher` pseudonym. Passing a key assembled some
    /// other way finds no profile and returns a sample with no dimensions, which is safe but teaches
    /// nothing.
    /// </para>
    /// </remarks>
    /// <param name="key">The sender profile to teach. Not a recipient or relationship key.</param>
    /// <param name="at">The instant the sample is recorded at.</param>
    /// <param name="provenance">Where this label comes from. The caller's decision, never inferred.</param>
    /// <param name="label">Free-form detail for the ledger. Never message content.</param>
    /// <summary>
    /// Builds a trusted sample for an outbound sender, without the caller needing a profile key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Prefer this overload.</b> Profiles are keyed on a tenant-scoped pseudonym produced by
    /// <see cref="ProfileKeyHasher"/>, not on the address, so a caller who assembles a key from the
    /// raw sender identity names a profile that does not exist, and gets a sample with no dimensions
    /// that teaches nothing. Silently. That is the same failure shape as omitting the rate features,
    /// one layer down, and the answer is the same: do not make the caller know an internal detail.
    /// </para>
    /// <para>
    /// Outbound only. An inbound sender key is qualified by authentication provenance, so two
    /// messages from the same address that authenticated differently are different profiles: a
    /// caller cannot name that profile without supplying the authentication context, and guessing one
    /// here would silently teach the wrong profile. Use the <see cref="ProfileKey"/> overload, or
    /// `ProfileScopes.InboundSender`, when you have it.
    /// </para>
    /// </remarks>
    public TrustedSample BuildTrustedSample(
        string tenantId,
        MailDirection direction,
        string senderIdentity,
        DateTimeOffset at,
        LabelProvenance provenance,
        string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderIdentity);

        if (direction != MailDirection.Outbound)
        {
            throw new ArgumentException(
                "An inbound sender profile is qualified by authentication provenance, so it cannot be "
                + "named from the identity alone. Use the ProfileKey overload with a key built by "
                + "ProfileScopes.InboundSender.",
                nameof(direction));
        }

        return BuildTrustedSample(
            ProfileScopes.OutboundSender(
                tenantId,
                _options.ProfileKeyHasher.Hash(tenantId, NullSender(senderIdentity))),
            at,
            provenance,
            label);
    }

    /// <summary>Builds a trusted sample for an already-resolved profile key.</summary>
    public TrustedSample BuildTrustedSample(
        ProfileKey key,
        DateTimeOffset at,
        LabelProvenance provenance,
        string? label = null) => new()
        {
            Dimensions = LearningVector(_profiles.Read(key), at),
            Provenance = provenance,
            RecordedAt = at,
            Label = label,
        };

    /// <summary>
    /// The vector a trusted sample carries: the sender's observed window, rate features included.
    /// </summary>
    /// <remarks>
    /// <b>Falls back to the message's semantic readings only when there is no bucket to read</b>, which
    /// is the first observation of a profile. That is a weaker sample and is labelled as such by its
    /// content rather than hidden: it carries no rate features, so it cannot teach a baseline what
    /// normal volume looks like, which is the honest state of affairs for a sender we have watched
    /// once.
    ///
    /// <para>
    /// The `rate.*` ids are synthesised per bucket and appear nowhere else, so a sample built from
    /// evidence alone can never model them. That asymmetry is invisible from either side: the encoder
    /// reports the baselines as null, and nothing here looks wrong.
    /// </para>
    /// </remarks>
    private DimensionVector LearningVector(AdaptiveProfile snapshot, DateTimeOffset now)
    {
        var window = _options.Adaptive.Burst;

        if (!snapshot.Series.TryGetValue(window.Name, out var series) || series.Buckets.Count == 0)
        {
            return EvidenceVectors.Semantic([], _dimensionIds);
        }

        return series.Buckets[^1].FeatureVector(now, window.MinimumSamplesPerBucket);
    }

    /// <summary>
    /// The message's recipients as tenant-scoped pseudonyms.
    /// </summary>
    /// <remarks>
    /// Hashed rather than passed through, on the same convention as profile keys: the profile records
    /// how many <em>distinct people</em> a sender has addressed, and it can answer that without ever
    /// holding an address.
    /// </remarks>
    private IReadOnlyList<string> HashedRecipients(MailEnvelope envelope, AssessmentContext context) =>
        [.. envelope.RcptTo.Select(recipient =>
            _options.ProfileKeyHasher.Hash(context.TenantId, recipient))];

    /// <summary>One reserved recipient budget, held until dispatch or release.</summary>
    private sealed record BudgetReservation(string TenantId, string PrincipalId, int Recipients);

    /// <summary>
    /// Returns budget for a reservation whose recipients were never dispatched.
    /// </summary>
    /// <remarks>
    /// Only for a reservation that actually succeeded, and only when acceptance did not happen.
    /// Both halves matter: releasing an unsuccessful reservation would return budget that was never
    /// taken (and, because exhaustion is what caused the deferral, would un-exhaust the quota on
    /// every message so it never binds), while releasing an accepted one would hand back budget for
    /// recipients that are about to be delivered.
    /// </remarks>
    private void ReleaseUndispatchedBudget(
        BudgetReservation? reservation,
        AcceptanceOutcome acceptance,
        DateTimeOffset now)
    {
        if (reservation is null || acceptance.IsAccepted)
        {
            return;
        }

        // The same instant the reservation was made with, so the elapsed time between them is
        // exactly zero and no reservation can age out of the window between its reserve and its
        // release. That is what keeps the shortfall counter measuring divergence rather than
        // arithmetic.
        var returned = _quotaLedger.Release(
            reservation.TenantId,
            reservation.PrincipalId,
            reservation.Recipients,
            now);

        Statistics.RecordRelease();

        if (returned != reservation.Recipients)
        {
            // The ledger clamps rather than throwing, deliberately, releasing twice on a retry path
            // is legitimate and should not crash a hot path. But a shortfall means this pipeline's
            // tally and the ledger's have diverged, and the number bounding escape is the one place
            // that must not drift unnoticed. Counted, not thrown; somebody has to reconcile.
            Statistics.RecordReleaseShortfall();
        }
    }

    /// <summary>
    /// A stable identifier for this assessment, derived from the correlation id and the message.
    /// </summary>
    /// <remarks>
    /// Deterministic rather than random so that replaying the same request produces the same
    /// identifiers, which is what makes two runs comparable at all. It is also what the campaign
    /// window excludes by when looking for near-duplicates, so it must be unique per message.
    /// </remarks>
    private static string BuildAssessmentId(AssessmentContext context, MailEnvelope envelope)
    {
        var seed = $"{context.CorrelationId} {envelope.TenantId} {envelope.InternalMessageId} {envelope.MimeDigest}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return $"asm_{Convert.ToHexStringLower(digest.AsSpan(0, 16))}";
    }
}
