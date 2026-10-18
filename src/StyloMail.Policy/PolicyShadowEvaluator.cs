using StyloMail.Core;

namespace StyloMail.Policy;

/// <summary>Whether the pack shadow runs, and for which declared jurisdiction.</summary>
public sealed record PolicyShadowOptions
{
    /// <summary>Off by default. A deployment opts into recording a second opinion.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The deployment's declared jurisdiction. Required for the shadow to run: there is no safe
    /// default, because a pack selected for the wrong legal context is worse than no record.
    /// </summary>
    public string? Jurisdiction { get; init; }
}

/// <summary>
/// Runs the pack evaluator beside a decision and returns its record, never an action.
/// </summary>
public interface IPolicyShadowEvaluator
{
    /// <summary>
    /// Returns null when no pack is active, or a record. It never throws into the assessment path
    /// and never changes the action the legacy engine chose.
    /// </summary>
    Task<PolicyDecisionRecord?> EvaluateAsync(
        string tenantId,
        IReadOnlyList<Evidence> evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Phase 2 shadow: the pack's deterministic evaluation, recorded beside the legacy decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>Second opinion, not authority.</b> The returned record has <c>Shadow = true</c> and the caller
/// keeps the legacy action. The evaluator reads the effective pack and its live rule set, runs
/// <see cref="DeterministicPolicyEvaluator"/> over the same evidence, and copies the explanation.
/// </para>
/// <para>
/// <b>Null means no pack was active.</b> A configured pack that cannot be read is a Review record
/// with a named reason, never null: an outage and a missing configuration are different facts, and
/// the console must be able to tell them apart.
/// </para>
/// <para>
/// <b>Failures are recorded, not thrown.</b> A store or evaluator failure becomes
/// <c>policy.pack_store_unavailable</c> or <c>policy.evaluator_failed</c> on a Review record. The
/// assessment path must complete; a shadow that could fail an assessment would be an availability
/// regression disguised as an audit improvement. Cancellation is the one exception and propagates.
/// </para>
/// </remarks>
public sealed class PolicyShadowEvaluator : IPolicyShadowEvaluator
{
    private readonly IPolicyPackReader _packs;
    private readonly IPolicyRuleSetReader _ruleSets;
    private readonly PolicyShadowOptions _options;
    private readonly DeterministicPolicyEvaluator _evaluator;

    public PolicyShadowEvaluator(
        IPolicyPackReader packs,
        IPolicyRuleSetReader ruleSets,
        PolicyShadowOptions options,
        DeterministicPolicyEvaluator? evaluator = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(ruleSets);
        ArgumentNullException.ThrowIfNull(options);

        _packs = packs;
        _ruleSets = ruleSets;
        _options = options;
        _evaluator = evaluator ?? new DeterministicPolicyEvaluator();
    }

    public async Task<PolicyDecisionRecord?> EvaluateAsync(
        string tenantId,
        IReadOnlyList<Evidence> evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(evidence);

        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Jurisdiction))
        {
            return null;
        }

        PolicyPack? pack;

        try
        {
            pack = await _packs
                .GetEffectiveAsync(tenantId, _options.Jurisdiction, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A shadow must not fail an assessment; the failure is recorded below.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Unavailable(
                packId: string.Empty,
                packVersion: string.Empty,
                ruleSetId: string.Empty,
                ruleSetVersion: string.Empty,
                scorerVersion: string.Empty,
                "policy.pack_store_unavailable",
                "The effective pack could not be read, so the pack was not evaluated. This is a record, not an action.",
                now);
        }

        if (pack is null)
        {
            return null;
        }

        PolicyRuleSet? ruleSet;

        try
        {
            ruleSet = await _ruleSets
                .FindLiveByVersionAsync(tenantId, pack.RuleSetVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Recorded, never thrown into the assessment path.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Unavailable(
                pack.Id,
                pack.Version,
                string.Empty,
                pack.RuleSetVersion,
                pack.ScorerVersion,
                "policy.pack_store_unavailable",
                "The pack's rule set could not be read, so the pack was not evaluated. This is a record, not an action.",
                now);
        }

        if (ruleSet is null)
        {
            return Unavailable(
                pack.Id,
                pack.Version,
                string.Empty,
                pack.RuleSetVersion,
                pack.ScorerVersion,
                "policy.rule_set_not_live",
                $"Rule set '{pack.RuleSetVersion}' is not live, so the pack could not be evaluated. This is a record, not an action.",
                now);
        }

        try
        {
            var evaluation = _evaluator.Evaluate(new PolicyEvaluationInput
            {
                TenantId = tenantId,
                Pack = pack,
                RuleSet = ruleSet,
                Evidence = evidence,
            });

            return new PolicyDecisionRecord
            {
                Source = PolicySource.Shadow,
                Admission = AdmissionOutcome.NotApplicableForAssessmentOnly,
                PackId = evaluation.PackId,
                PackVersion = evaluation.PackVersion,
                RuleSetId = evaluation.RuleSetId,
                RuleSetVersion = evaluation.RuleSetVersion,
                ScorerVersion = evaluation.ScorerVersion,
                EvaluatorVersion = DeterministicPolicyEvaluator.Version,
                Verdict = evaluation.Verdict,
                RequiresHumanReview = evaluation.RequiresHumanReview,
                DecidingRuleId = evaluation.DecidingRuleId,
                DecidingRuleVersion = evaluation.DecidingRuleVersion,
                DecidingThreshold = evaluation.DecidingThreshold,
                DecidingReasonCode = evaluation.DecidingReasonCode,
                DecidingReason = evaluation.DecidingReason,
                MissingDimensions = evaluation.MissingDimensions,
                Coverage = evaluation.Coverage,
                BindingIds = [],
                PropertyFacts = [],
                MissingPropertyKeys = [],
                EvidenceSnapshotDigest = evaluation.EvidenceSnapshotDigest,
                EvaluatedAt = now,
                Shadow = true,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A shadow must not fail an assessment; the failure is recorded.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Unavailable(
                pack.Id,
                pack.Version,
                ruleSet.Id,
                ruleSet.Version,
                pack.ScorerVersion,
                "policy.evaluator_failed",
                "The deterministic evaluator failed, so the pack was not evaluated. This is a record, not an action.",
                now);
        }
    }

    private static PolicyDecisionRecord Unavailable(
        string packId,
        string packVersion,
        string ruleSetId,
        string ruleSetVersion,
        string scorerVersion,
        string reasonCode,
        string reason,
        DateTimeOffset now) => new()
        {
            Source = PolicySource.Shadow,
            Admission = AdmissionOutcome.NotApplicableForAssessmentOnly,
            PackId = packId,
            PackVersion = packVersion,
            RuleSetId = ruleSetId,
            RuleSetVersion = ruleSetVersion,
            ScorerVersion = scorerVersion,
            EvaluatorVersion = DeterministicPolicyEvaluator.Version,
            Verdict = PolicyVerdict.Review,
            RequiresHumanReview = true,
            DecidingRuleId = null,
            DecidingRuleVersion = null,
            DecidingThreshold = null,
            DecidingReasonCode = reasonCode,
            DecidingReason = reason,
            MissingDimensions = [],
            Coverage = 0,
            BindingIds = [],
            PropertyFacts = [],
            MissingPropertyKeys = [],

            // An uncomputed digest is not an empty one.
            EvidenceSnapshotDigest = null,
            EvaluatedAt = now,
            Shadow = true,
        };
}
