using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StyloMail.Core;

namespace StyloMail.Policy;

/// <summary>How a rule's threshold is compared with the measured value.</summary>
public enum RuleComparison
{
    /// <summary>Fire when the value is greater than or equal to the band's threshold.</summary>
    AtLeast = 0,

    /// <summary>Fire when the value is less than or equal to the band's threshold.</summary>
    AtMost = 1,
}

/// <summary>
/// One explicit threshold on a rule, and what crossing it means.
/// </summary>
/// <param name="Threshold">
/// The number the evidence is compared against. It is stored with the decision, so a replayed
/// decision cannot be explained by a threshold that has since moved.
/// </param>
/// <param name="Verdict">The typed outcome at this band.</param>
/// <param name="ReasonCode">Stable, operator-facing reason code, e.g. <c>policy.payment_redirection_hold</c>.</param>
public sealed record RuleBand(double Threshold, PolicyVerdict Verdict, string ReasonCode);

/// <summary>
/// A versioned deterministic rule over named evidence.
/// </summary>
/// <remarks>
/// <para>
/// A rule is the only thing that selects an action. It names the signal it reads, the inputs its
/// verdict depends on, its bands, and the sample and coverage minimums below which it refuses to
/// answer. The model contributes a value; the rule decides what the value means.
/// </para>
/// <para>
/// <b>Missing evidence escalates.</b> A rule whose required signal was not measured does not fall
/// through to its lowest band: absence of evidence is not evidence of safety, and a rule that would
/// score a missing dimension as zero would turn an outage into an allow.
/// </para>
/// </remarks>
public sealed record PolicyRule
{
    /// <summary>Stable rule id. A rule's identity travels with its decisions across versions.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The rule's version. A threshold change is a new version, never an edit: prior decisions are
    /// explained by the version that produced them.
    /// </summary>
    public required int Version { get; init; }

    public required string Category { get; init; }

    /// <summary>The signal whose measured value is compared against <see cref="Bands"/>.</summary>
    public required string SignalId { get; init; }

    public required RuleComparison Comparison { get; init; }

    /// <summary>
    /// Every signal that must be measured for this rule to be evaluable. Defaults to the scored
    /// signal alone. A rule whose verdict depends on a second dimension names it here, and the
    /// coverage it reports is over this list.
    /// </summary>
    public IReadOnlyList<string>? RequiredSignalIds { get; init; }

    /// <summary>The inputs actually required: the explicit list, or the scored signal.</summary>
    public IReadOnlyList<string> RequiredSignals =>
        RequiredSignalIds is { Count: > 0 } required ? required : [SignalId];

    /// <summary>
    /// Property keys this rule needs. A missing key escalates the rule to Review before any band is
    /// considered; a rule that does not name a key is independent of property absence.
    /// </summary>
    public IReadOnlyList<string>? RequiredPropertyKeys { get; init; }

    /// <summary>The property keys actually required, empty when none.</summary>
    public IReadOnlyList<string> RequiredProperties =>
        RequiredPropertyKeys is { Count: > 0 } keys ? keys : [];

    /// <summary>Bands, ordered or not; the evaluator sorts by threshold for the comparison.</summary>
    public required IReadOnlyList<RuleBand> Bands { get; init; }

    /// <summary>
    /// The dimensions whose measurement counts toward this rule's coverage. Defaults to
    /// <see cref="RequiredSignals"/>. A rule may name a wider set than it strictly requires: the
    /// required set is mandatory, and the coverage set is what the verdict is expected to be
    /// calibrated over, so a case missing a coverage dimension can still be refused for thin
    /// coverage even though every mandatory input arrived.
    /// </summary>
    public IReadOnlyList<string>? CoverageSignalIds { get; init; }

    /// <summary>The dimensions the rule's coverage is measured over.</summary>
    public IReadOnlyList<string> CoverageSignals =>
        CoverageSignalIds is { Count: > 0 } coverage ? coverage : RequiredSignals;

    /// <summary>Fraction of <see cref="CoverageSignals"/> that must be measured. 1.0 by default.</summary>
    public double MinimumCoverage { get; init; } = 1.0;

    /// <summary>
    /// Minimum observations behind the scored signal. Below it the rule reports insufficient sample
    /// rather than a verdict, so a thin history cannot produce a confident action.
    /// </summary>
    public int MinimumSampleSupport { get; init; }

    /// <summary>
    /// The outcome when required evidence or sample support is missing. Must be at least
    /// <see cref="PolicyVerdict.Review"/>; <see cref="RuleSetValidator"/> refuses anything lower.
    /// </summary>
    public PolicyVerdict OnMissingEvidence { get; init; } = PolicyVerdict.Review;
}

/// <summary>A versioned set of rules. The pack references this version, so a decision names both.</summary>
public sealed record PolicyRuleSet
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required IReadOnlyList<PolicyRule> Rules { get; init; }
}

/// <summary>Why a rule set was refused.</summary>
public sealed record RuleSetViolation
{
    public required string Code { get; init; }

    public required string RuleId { get; init; }

    public required string Message { get; init; }
}

/// <summary>Structural checks on rules, run before the evaluator will trust a rule set.</summary>
/// <remarks>
/// These are the failures a compiler cannot catch because rules arrive from a registry rather than
/// from source. Each one would otherwise produce a rule that looks live and can never fire, or one
/// whose meaning depends on list order: a duplicate id, an unknown category, an empty band list, a
/// non-finite threshold, two bands at the same threshold, or a missing-evidence outcome that fails
/// open.
/// </remarks>
public static class RuleSetValidator
{
    public const string RuleIdMissing = "rule.id_missing";
    public const string DuplicateRuleId = "rule.duplicate_id";
    public const string VersionInvalid = "rule.version_invalid";
    public const string CategoryUnknown = "rule.category_unknown";
    public const string SignalMissing = "rule.signal_missing";
    public const string ScoredSignalNotRequired = "rule.scored_signal_not_required";
    public const string BandsEmpty = "rule.bands_empty";
    public const string ThresholdInvalid = "rule.threshold_invalid";
    public const string ThresholdDuplicated = "rule.threshold_duplicated";
    public const string BandOutcomeNotActionable = "rule.band_outcome_not_actionable";
    public const string BandReasonMissing = "rule.band_reason_missing";
    public const string CoverageInvalid = "rule.coverage_invalid";
    public const string CoverageExcludesRequired = "rule.coverage_excludes_required";
    public const string SampleMinimumInvalid = "rule.sample_minimum_invalid";
    public const string MissingEvidenceNotEscalating = "rule.missing_evidence_not_escalating";
    public const string PropertyKeyUnknown = "rule.property_key_unknown";

    public static IReadOnlyList<RuleSetViolation> Validate(PolicyRuleSet ruleSet)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);

        var violations = new List<RuleSetViolation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in ruleSet.Rules ?? [])
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                violations.Add(Violation(RuleIdMissing, rule.Id ?? string.Empty, "A rule needs a stable id."));
                continue;
            }

            if (!seen.Add(rule.Id))
            {
                violations.Add(Violation(DuplicateRuleId, rule.Id, $"Rule id '{rule.Id}' appears more than once; which one fired would depend on list order."));
            }

            if (rule.Version <= 0)
            {
                violations.Add(Violation(VersionInvalid, rule.Id, "A rule version is a positive number; zero names no version."));
            }

            if (!PolicyCategories.TryGet(rule.Category, out _))
            {
                violations.Add(Violation(CategoryUnknown, rule.Id, $"'{rule.Category}' is not a policy category."));
            }

            if (string.IsNullOrWhiteSpace(rule.SignalId))
            {
                violations.Add(Violation(SignalMissing, rule.Id, "A rule must name the signal it reads."));
            }

            if (rule.RequiredSignals.Any(string.IsNullOrWhiteSpace))
            {
                violations.Add(Violation(SignalMissing, rule.Id, "Every required signal must be named."));
            }
            else if (!rule.RequiredSignals.Contains(rule.SignalId, StringComparer.Ordinal))
            {
                violations.Add(Violation(
                    ScoredSignalNotRequired,
                    rule.Id,
                    $"The scored signal '{rule.SignalId}' must appear in the rule's required signals."));
            }

            if (rule.Bands is null || rule.Bands.Count == 0)
            {
                violations.Add(Violation(BandsEmpty, rule.Id, "A rule with no bands can never fire and can only escalate."));
                continue;
            }

            foreach (var band in rule.Bands)
            {
                if (!double.IsFinite(band.Threshold))
                {
                    violations.Add(Violation(ThresholdInvalid, rule.Id, "A threshold must be a finite number."));
                }

                if (band.Verdict is PolicyVerdict.Permit)
                {
                    violations.Add(Violation(
                        BandOutcomeNotActionable,
                        rule.Id,
                        "A band that returns Permit cannot be told apart from no match; use Observe."));
                }

                if (string.IsNullOrWhiteSpace(band.ReasonCode))
                {
                    violations.Add(Violation(BandReasonMissing, rule.Id, "Every band needs a stable reason code."));
                }
            }

            if (rule.Bands.GroupBy(band => band.Threshold).Any(group => group.Count() > 1))
            {
                violations.Add(Violation(ThresholdDuplicated, rule.Id, "Two bands at the same threshold make the outcome depend on list order."));
            }

            if (rule.MinimumCoverage is < 0 or > 1)
            {
                violations.Add(Violation(CoverageInvalid, rule.Id, "Minimum coverage is a fraction between 0 and 1."));
            }

            if (rule.CoverageSignals.Any(string.IsNullOrWhiteSpace))
            {
                violations.Add(Violation(SignalMissing, rule.Id, "Every coverage signal must be named."));
            }
            else if (rule.RequiredSignals.Any(required => !rule.CoverageSignals.Contains(required, StringComparer.Ordinal)))
            {
                violations.Add(Violation(
                    CoverageExcludesRequired,
                    rule.Id,
                    "The coverage set must include every required signal; coverage cannot be narrower than the rule's mandatory inputs."));
            }

            if (rule.MinimumSampleSupport < 0)
            {
                violations.Add(Violation(SampleMinimumInvalid, rule.Id, "Minimum sample support cannot be negative."));
            }

            foreach (var key in rule.RequiredProperties)
            {
                if (!PolicyPropertyKeys.IsKnown(key))
                {
                    violations.Add(Violation(
                        PropertyKeyUnknown,
                        rule.Id,
                        $"'{key}' is not a v1 property key. Supported: {string.Join(", ", PolicyPropertyKeys.All)}."));
                }
            }

            if (rule.OnMissingEvidence < PolicyVerdict.Review)
            {
                violations.Add(Violation(
                    MissingEvidenceNotEscalating,
                    rule.Id,
                    $"Missing evidence would produce {rule.OnMissingEvidence}; it must escalate to at least {PolicyVerdict.Review}."));
            }
        }

        return violations;
    }

    private static RuleSetViolation Violation(string code, string ruleId, string message) => new()
    {
        Code = code,
        RuleId = ruleId,
        Message = message,
    };
}

/// <summary>Everything the evaluator is given. Nothing is reached for from ambient state.</summary>
public sealed record PolicyEvaluationInput
{
    public required string TenantId { get; init; }

    public required PolicyPack Pack { get; init; }

    public required PolicyRuleSet RuleSet { get; init; }

    /// <summary>Evidence rows. Order is not meaningful and does not affect the outcome.</summary>
    public required IReadOnlyList<Evidence> Evidence { get; init; }

    /// <summary>
    /// The resolved effective policy. Null keeps the evaluator on the raw pack, which is the Phase 2
    /// shadow contract; the authoritative path supplies it.
    /// </summary>
    public EffectivePolicy? Effective { get; init; }
}

/// <summary>What one rule did, whether or not it fired.</summary>
/// <remarks>
/// A non-firing rule is recorded too. The absence of a rule from an explanation is indistinguishable
/// from a rule that was never in the registry, and "why did this happen" has to be answerable for
/// the rules that stayed quiet as well as the one that spoke.
/// </remarks>
public sealed record RuleEvaluation
{
    public required string RuleId { get; init; }

    public required int RuleVersion { get; init; }

    public required string Category { get; init; }

    public required string SignalId { get; init; }

    /// <summary>Fired, not fired, or refused for one of the named evidence reasons.</summary>
    public required RuleEvaluationState State { get; init; }

    /// <summary>The typed outcome this rule argued for. Null when it did not fire.</summary>
    public PolicyVerdict? Outcome { get; init; }

    /// <summary>The band threshold that produced the outcome. Null when no band matched.</summary>
    public double? Threshold { get; init; }

    /// <summary>The measured value, or null when the signal was not measured.</summary>
    public double? ObservedValue { get; init; }

    /// <summary>Measured required signals over required signals, for this rule.</summary>
    public required double Coverage { get; init; }

    /// <summary>Coverage dimensions measured for this rule.</summary>
    public required int CoverageSignalsMeasured { get; init; }

    /// <summary>Coverage dimensions the rule expects.</summary>
    public required int CoverageSignalsExpected { get; init; }

    public int? SampleSupport { get; init; }

    /// <summary>Required signals that were not measured. Named, never counted as zero.</summary>
    public required IReadOnlyList<string> MissingSignals { get; init; }

    /// <summary>
    /// Required signals answered by more than one distinct row. They were measured, but the engine
    /// will not choose between the answers.
    /// </summary>
    public IReadOnlyList<string> ConflictingSignals { get; init; } = [];

    /// <summary>True when the pack's ceiling reduced the outcome the rule asked for.</summary>
    public bool CappedByCeiling { get; init; }

    /// <summary>
    /// True when this rule alone puts a human in front of the case: a zero-tolerance category, a
    /// missing dimension, or a rule that declares review on fire.
    /// </summary>
    public bool RequiresHumanReview { get; init; }

    public required string ReasonCode { get; init; }

    public required string Reason { get; init; }
}

/// <summary>The state of a rule evaluation. A non-firing rule is a statement, not a silence.</summary>
public enum RuleEvaluationState
{
    /// <summary>A band matched and produced an outcome.</summary>
    Fired = 0,

    /// <summary>The signal was measured and no band matched; below the detection floor.</summary>
    NotFired = 1,

    /// <summary>A required signal was not measured.</summary>
    MissingEvidence = 2,

    /// <summary>Required signals were measured but below the rule's coverage minimum.</summary>
    InsufficientCoverage = 3,

    /// <summary>The measured row carried fewer observations than the rule requires.</summary>
    InsufficientSample = 4,

    /// <summary>More than one distinct row answered the same signal; the engine will not pick one.</summary>
    ConflictingEvidence = 5,

    /// <summary>A property key this rule requires could not be read.</summary>
    MissingProperty = 6,

    /// <summary>An effective property requires review even though no band fired.</summary>
    ForcedReview = 7,

    /// <summary>An expired HoldForReview binding withholds this category even below the band.</summary>
    WithheldForReview = 8,
}

/// <summary>
/// The record of one deterministic decision: the outcome, the reason, and every input and rule that
/// produced it.
/// </summary>
/// <remarks>
/// <para>
/// This is the object a console's "why did this happen" links to (spec 9.14). It carries the pack,
/// rule-set and scorer versions, the deciding rule's id, version and threshold, the evidence
/// snapshot in canonical order with a digest over it, and the full list of rule evaluations. A
/// decision is reproducible when the same snapshot and the same versions produce the same verdict,
/// which is what the digest makes checkable.
/// </para>
/// <para>
/// The snapshot carries no message content. Evidence rows carry signal values and producer
/// versions; text capture is a separate, bound PII path.
/// </para>
/// </remarks>
public sealed record PolicyEvaluation
{
    public required string TenantId { get; init; }

    public required string PackId { get; init; }

    public required string PackVersion { get; init; }

    public required string RuleSetId { get; init; }

    public required string RuleSetVersion { get; init; }

    public required string ScorerVersion { get; init; }

    public required PolicyVerdict Verdict { get; init; }

    /// <summary>True when a human must see this decision, whatever the verdict's automatic ceiling.</summary>
    public required bool RequiresHumanReview { get; init; }

    /// <summary>The rule whose outcome decided the verdict. Null when nothing fired and nothing was missing.</summary>
    public string? DecidingRuleId { get; init; }

    public int? DecidingRuleVersion { get; init; }

    public double? DecidingThreshold { get; init; }

    public required string DecidingReasonCode { get; init; }

    public required string DecidingReason { get; init; }

    /// <summary>Distinct required signals that were not measured, sorted ordinally.</summary>
    public required IReadOnlyList<string> MissingDimensions { get; init; }

    /// <summary>Measured required signals over all required signals, across the rule set.</summary>
    public required double Coverage { get; init; }

    /// <summary>Every rule that was evaluated, sorted by rule id.</summary>
    public required IReadOnlyList<RuleEvaluation> Rules { get; init; }

    /// <summary>The evidence the decision used, in canonical order.</summary>
    public required IReadOnlyList<Evidence> EvidenceSnapshot { get; init; }

    /// <summary>SHA-256 over the canonical snapshot. Two equal digests are the same inputs.</summary>
    public required string EvidenceSnapshotDigest { get; init; }

    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>Named so a decision can never be attributed to a model: this is the rules engine.</summary>
    public required string DecidedBy { get; init; }
}

/// <summary>
/// Evaluates versioned rules over stored evidence and returns a typed outcome with its explanation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic and total.</b> Every dependency is an argument: the pack, the rule set, the
/// evidence and the clock. Rules and evidence rows are sorted before use, so the same inputs produce
/// the same verdict, the same deciding rule and the same snapshot digest regardless of the order the
/// caller supplied them in. There is no provider call in this path and no mutation of the input.
/// </para>
/// <para>
/// <b>The safety direction is one-way.</b> Missing evidence, insufficient coverage, insufficient
/// sample and conflicting evidence all escalate to <see cref="PolicyVerdict.Review"/>. A zero-
/// tolerance category elevates any candidate to review and never to an accusation. The pack's
/// action ceiling can reduce a fired outcome but can never reduce an escalation, so a low ceiling
/// cannot turn "we did not measure it" into "it was fine".
/// </para>
/// </remarks>
public sealed class DeterministicPolicyEvaluator
{
    public const string DecidedBy = "deterministic-policy-evaluator";

    /// <summary>The evaluator version stamped on every decision and shadow record.</summary>
    public const string Version = "deterministic-policy-evaluator/1";

    private readonly TimeProvider _time;

    public DeterministicPolicyEvaluator(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public PolicyEvaluation Evaluate(PolicyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var pack = input.Pack;
        var ruleSet = input.RuleSet;
        var snapshot = Canonicalise(input.Evidence ?? []);
        var digest = Digest(snapshot);

        // Cross-tenant evaluation and a version mismatch are contract failures, not verdicts about
        // the message. Both escalate rather than evaluating against the wrong thresholds.
        if (!string.Equals(input.TenantId, pack.TenantId, StringComparison.Ordinal))
        {
            return Refusal(input, snapshot, digest, "policy.tenant_scope_mismatch",
                $"Pack '{pack.Id}' belongs to tenant '{pack.TenantId}', not '{input.TenantId}'. Cross-tenant threshold reuse is not evaluated.");
        }

        // A pack that was never validated must not evaluate: an unvalidated floor is a floor nobody
        // approved, and the evaluator is the last point before an action.
        var packValidation = PolicyPackValidator.Validate(pack);
        if (!packValidation.IsValid)
        {
            return Refusal(input, snapshot, digest, "policy.pack_invalid",
                "The pack is invalid: " + string.Join("; ", packValidation.Violations.Select(violation => violation.Code)));
        }

        if (!string.Equals(pack.RuleSetVersion, ruleSet.Version, StringComparison.Ordinal))
        {
            return Refusal(input, snapshot, digest, "policy.rule_set_version_mismatch",
                $"Pack '{pack.Id}' references rule set '{pack.RuleSetVersion}' but was given '{ruleSet.Version}'. A decision against a different rule version is not replayable.");
        }

        var ruleSetViolations = RuleSetValidator.Validate(ruleSet);
        if (ruleSetViolations.Count > 0)
        {
            return Refusal(input, snapshot, digest, "policy.rule_set_invalid",
                "The rule set is invalid: " + string.Join("; ", ruleSetViolations.Select(violation => $"{violation.Code} ({violation.RuleId}): {violation.Message}")));
        }

        if (ruleSet.Rules.Count == 0)
        {
            return Refusal(input, snapshot, digest, "policy.no_rules",
                "The rule set contains no rules, so nothing was evaluated. An empty rule set is a configuration failure, not a clean message.");
        }

        var available = IndexEvidence(snapshot);
        var levels = input.Effective?.Levels ?? pack.CategoryLevels ?? new Dictionary<string, ToleranceLevel>();
        var ceiling = input.Effective?.Ceiling ?? pack.ActionCeiling;
        var missingProperties = input.Effective?.MissingPropertyKeys ?? [];
        var forced = (input.Effective?.ForcedReviewCategories ?? []).ToHashSet(StringComparer.Ordinal);
        var withheld = (input.Effective?.WithheldReviewCategories ?? []).ToHashSet(StringComparer.Ordinal);
        var evaluations = ruleSet.Rules
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .Select(rule => EvaluateRule(rule, available, levels, missingProperties, forced, withheld, ceiling))
            .ToList();

        foreach (var category in withheld.Where(category =>
                     evaluations.All(evaluation => !string.Equals(evaluation.Category, category, StringComparison.Ordinal))))
        {
            evaluations.Add(new RuleEvaluation
            {
                RuleId = $"policy.withheld_review.{category}",
                RuleVersion = 1,
                Category = category,
                SignalId = string.Empty,
                State = RuleEvaluationState.WithheldForReview,
                Outcome = PolicyVerdict.Review,
                Threshold = null,
                ObservedValue = null,
                Coverage = 1,
                CoverageSignalsMeasured = 0,
                CoverageSignalsExpected = 0,
                SampleSupport = null,
                MissingSignals = [],
                RequiresHumanReview = true,
                ReasonCode = "policy.binding_expired_hold",
                Reason = $"An expired HoldForReview binding still withholds {category} for a human decision.",
            });
        }

        var deciding = SelectDeciding(evaluations);
        var missing = evaluations
            .SelectMany(evaluation => evaluation.MissingSignals)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var verdict = evaluations
            .Select(evaluation => evaluation.Outcome ?? PolicyVerdict.Permit)
            .OrderByDescending(ProtectionRank)
            .First();
        var requiresHumanReview = verdict is PolicyVerdict.Review or PolicyVerdict.Quarantine
            || evaluations.Any(evaluation => evaluation.RequiresHumanReview);

        var totalExpectedCoverage = evaluations.Sum(evaluation => evaluation.CoverageSignalsExpected);
        var totalMeasuredCoverage = evaluations.Sum(evaluation => evaluation.CoverageSignalsMeasured);

        var coverage = totalExpectedCoverage == 0
            ? 1.0
            : (double)totalMeasuredCoverage / totalExpectedCoverage;

        return new PolicyEvaluation
        {
            TenantId = input.TenantId,
            PackId = pack.Id,
            PackVersion = pack.Version,
            RuleSetId = ruleSet.Id,
            RuleSetVersion = ruleSet.Version,
            ScorerVersion = pack.ScorerVersion,
            Verdict = verdict,
            RequiresHumanReview = requiresHumanReview,
            DecidingRuleId = deciding?.RuleId,
            DecidingRuleVersion = deciding?.RuleVersion,
            DecidingThreshold = deciding?.Threshold,
            DecidingReasonCode = deciding?.ReasonCode ?? "policy.no_rule_fired",
            DecidingReason = deciding?.Reason ?? "No rule fired and no required evidence was missing.",
            MissingDimensions = missing,
            Coverage = coverage,
            Rules = evaluations,
            EvidenceSnapshot = snapshot,
            EvidenceSnapshotDigest = digest,
            EvaluatedAt = _time.GetUtcNow(),
            DecidedBy = DecidedBy,
        };
    }

    private static RuleEvaluation EvaluateRule(
        PolicyRule rule,
        IReadOnlyDictionary<string, IReadOnlyList<Evidence>> available,
        IReadOnlyDictionary<string, ToleranceLevel> levels,
        IReadOnlyList<string> missingPropertyKeys,
        IReadOnlySet<string> forced,
        IReadOnlySet<string> withheld,
        PolicyVerdict ceiling)
    {
        var missingProperty = rule.RequiredProperties
            .FirstOrDefault(key => missingPropertyKeys.Contains(key, StringComparer.Ordinal));

        if (missingProperty is not null)
        {
            return new RuleEvaluation
            {
                RuleId = rule.Id,
                RuleVersion = rule.Version,
                Category = rule.Category,
                SignalId = rule.SignalId,
                State = RuleEvaluationState.MissingProperty,
                Outcome = PolicyVerdict.Review,
                Threshold = null,
                ObservedValue = null,
                Coverage = 0,
                CoverageSignalsMeasured = 0,
                CoverageSignalsExpected = 0,
                SampleSupport = null,
                MissingSignals = [],
                RequiresHumanReview = true,
                ReasonCode = "policy.property_facts_unavailable",
                Reason = $"Required property '{missingProperty}' could not be read, so the rule cannot answer. Missing facts are not low risk.",
            };
        }

        var level = levels.TryGetValue(rule.Category, out var declared) ? declared : (ToleranceLevel?)null;
        var required = rule.RequiredSignals.Distinct(StringComparer.Ordinal).ToList();
        var missing = new List<string>();
        var conflicting = new List<string>();
        Evidence? scored = null;

        foreach (var signal in required)
        {
            if (!available.TryGetValue(signal, out var rows) || rows.Count == 0)
            {
                missing.Add(signal);
                continue;
            }

            // Two distinct available rows for one signal are conflicting evidence, not a value to
            // pick. Two producers may legitimately disagree, and choosing the first would make the
            // decision depend on input order.
            var distinct = rows
                .Where(row => row.Availability == EvidenceAvailability.Available && row.Value is not null)
                .GroupBy(CanonicalRow, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            if (distinct.Count == 0)
            {
                missing.Add(signal);
                continue;
            }

            if (distinct.Count > 1)
            {
                conflicting.Add(signal);
                continue;
            }

            if (string.Equals(signal, rule.SignalId, StringComparison.Ordinal))
            {
                scored = distinct[0];
            }
            else if (distinct[0].SampleSupport is { } support && support < rule.MinimumSampleSupport)
            {
                // A required corroborating input with too little support is not corroboration.
                missing.Add(signal);
            }
        }

        var coverageSignals = rule.CoverageSignals.Distinct(StringComparer.Ordinal).ToList();
        var coverageMeasured = coverageSignals.Count(signal => IsMeasured(signal, rule, available));
        var coverage = coverageSignals.Count == 0
            ? 1.0
            : (double)coverageMeasured / coverageSignals.Count;

        if (conflicting.Count > 0)
        {
            return Escalation(rule, RuleEvaluationState.ConflictingEvidence, "policy.conflicting_evidence",
                $"More than one measured row answered {string.Join(", ", conflicting)}; the engine will not pick one.",
                coverage, coverageMeasured, coverageSignals.Count, missing, conflicting,
                observed: scored?.Value, sample: scored?.SampleSupport);
        }

        if (missing.Count > 0)
        {
            return Escalation(rule, RuleEvaluationState.MissingEvidence, "policy.evidence_missing",
                $"Required evidence was missing or below its sample minimum: {string.Join(", ", missing)}. Missing evidence is not low risk.",
                coverage, coverageMeasured, coverageSignals.Count, missing, conflicting,
                observed: null, sample: null);
        }

        if (scored is not null
            && scored.SampleSupport is { } measuredSupport
            && measuredSupport < rule.MinimumSampleSupport)
        {
            return Escalation(rule, RuleEvaluationState.InsufficientSample, "policy.sample_below_minimum",
                $"{measuredSupport} observations is below this rule's minimum of {rule.MinimumSampleSupport}.",
                coverage, coverageMeasured, coverageSignals.Count, missing, conflicting,
                observed: scored.Value, sample: measuredSupport);
        }

        if (coverage < rule.MinimumCoverage)
        {
            return Escalation(rule, RuleEvaluationState.InsufficientCoverage, "policy.coverage_below_minimum",
                $"Coverage {coverage.ToString("0.###", CultureInfo.InvariantCulture)} is below this rule's minimum {rule.MinimumCoverage.ToString("0.###", CultureInfo.InvariantCulture)}.",
                coverage, coverageMeasured, coverageSignals.Count, missing, conflicting,
                observed: scored?.Value, sample: scored?.SampleSupport);
        }

        if (scored is null)
        {
            return Escalation(rule, RuleEvaluationState.MissingEvidence, "policy.scored_signal_not_measured",
                $"The scored signal '{rule.SignalId}' was not among the measured rows.",
                coverage, coverageMeasured, coverageSignals.Count, missing, conflicting,
                observed: null, sample: null);
        }

        var value = scored.Value!.Value;
        var band = Match(rule, value);

        if (band is null)
        {
            if (withheld.Contains(rule.Category))
            {
                return new RuleEvaluation
                {
                    RuleId = rule.Id,
                    RuleVersion = rule.Version,
                    Category = rule.Category,
                    SignalId = rule.SignalId,
                    State = RuleEvaluationState.WithheldForReview,
                    Outcome = PolicyVerdict.Review,
                    Threshold = null,
                    ObservedValue = value,
                    Coverage = coverage,
                    CoverageSignalsMeasured = coverageMeasured,
                    CoverageSignalsExpected = coverageSignals.Count,
                    SampleSupport = scored.SampleSupport,
                    MissingSignals = [],
                    RequiresHumanReview = true,
                    ReasonCode = "policy.binding_expired_hold",
                    Reason = $"An expired HoldForReview binding still withholds {rule.Category} for a human decision.",
                };
            }

            return new RuleEvaluation
            {
                RuleId = rule.Id,
                RuleVersion = rule.Version,
                Category = rule.Category,
                SignalId = rule.SignalId,
                State = RuleEvaluationState.NotFired,
                Outcome = null,
                Threshold = null,
                ObservedValue = value,
                Coverage = coverage,
                CoverageSignalsMeasured = coverageMeasured,
                CoverageSignalsExpected = coverageSignals.Count,
                SampleSupport = scored.SampleSupport,
                MissingSignals = [],
                ReasonCode = "policy.below_detection_floor",
                Reason = $"Value {value.ToString("0.####", CultureInfo.InvariantCulture)} is below the lowest threshold on rule '{rule.Id}'.",
            };
        }

        var outcome = band.Verdict;
        var capped = false;

        if (outcome > ceiling)
        {
            outcome = ceiling;
            capped = true;
        }

        // Zero tolerance: every candidate escalates to a human, evidence preserved. It raises the
        // floor of the response to Review; it never raises it to an accusation. A band may still be
        // stricter than Review, and Hold or Quarantine are capped by the pack ceiling as usual.
        var zeroTolerance = level == ToleranceLevel.ZeroTolerance;
        // Review outranks Hold in the composition order (Quarantine > Review > Hold > Allow), even
        // though the enum's numeric order puts Hold above Review. Use the composition rank here.
        if (zeroTolerance && ProtectionRank(outcome) < ProtectionRank(PolicyVerdict.Review))
        {
            outcome = PolicyVerdict.Review;
        }

        if ((forced.Contains(rule.Category) || withheld.Contains(rule.Category))
            && ProtectionRank(outcome) < ProtectionRank(PolicyVerdict.Review))
        {
            outcome = PolicyVerdict.Review;
        }

        var requiresReview = zeroTolerance
            || forced.Contains(rule.Category)
            || withheld.Contains(rule.Category)
            || outcome == PolicyVerdict.Review;
        var withheldOverride = withheld.Contains(rule.Category)
            && ProtectionRank(band.Verdict) < ProtectionRank(PolicyVerdict.Review);

        return new RuleEvaluation
        {
            RuleId = rule.Id,
            RuleVersion = rule.Version,
            Category = rule.Category,
            SignalId = rule.SignalId,
            State = RuleEvaluationState.Fired,
            Outcome = outcome,
            Threshold = band.Threshold,
            ObservedValue = value,
            Coverage = coverage,
            CoverageSignalsMeasured = coverageMeasured,
            CoverageSignalsExpected = coverageSignals.Count,
            SampleSupport = scored.SampleSupport,
            MissingSignals = [],
            CappedByCeiling = capped,
            RequiresHumanReview = requiresReview,
            ReasonCode = withheldOverride
                ? "policy.binding_expired_hold"
                : band.ReasonCode,
            Reason = zeroTolerance
                ? $"{rule.Category} is at zero tolerance: a candidate at {value.ToString("0.####", CultureInfo.InvariantCulture)} against {band.Threshold.ToString("0.####", CultureInfo.InvariantCulture)} routes to human review with the evidence preserved."
                : withheldOverride
                    ? $"An expired HoldForReview binding still withholds {rule.Category} for a human decision."
                    : forced.Contains(rule.Category)
                        ? $"{rule.Category} requires human review because an effective property is unknown or differs from the pack jurisdiction."
                        : $"Value {value.ToString("0.####", CultureInfo.InvariantCulture)} crossed rule '{rule.Id}' at threshold {band.Threshold.ToString("0.####", CultureInfo.InvariantCulture)}.",
        };
    }

    private static RuleBand? Match(PolicyRule rule, double value)
    {
        var ordered = rule.Comparison == RuleComparison.AtLeast
            ? rule.Bands.OrderByDescending(band => band.Threshold)
            : rule.Bands.OrderBy(band => band.Threshold);

        foreach (var band in ordered)
        {
            var matches = rule.Comparison == RuleComparison.AtLeast
                ? value >= band.Threshold
                : value <= band.Threshold;

            if (matches)
            {
                return band;
            }
        }

        return null;
    }

    private static RuleEvaluation Escalation(
        PolicyRule rule,
        RuleEvaluationState state,
        string reasonCode,
        string reason,
        double coverage,
        int coverageMeasured,
        int coverageExpected,
        IReadOnlyList<string> missing,
        IReadOnlyList<string> conflicting,
        double? observed,
        int? sample)
    {
        var escalated = rule.OnMissingEvidence < PolicyVerdict.Review
            ? PolicyVerdict.Review
            : rule.OnMissingEvidence;

        // A sample or coverage refusal is an escalation even when the rule's missing-evidence
        // outcome is stricter; the named reason still says why the rule could not answer.
        if (state is RuleEvaluationState.InsufficientCoverage or RuleEvaluationState.InsufficientSample
            or RuleEvaluationState.ConflictingEvidence)
        {
            escalated = escalated < PolicyVerdict.Review ? PolicyVerdict.Review : escalated;
        }

        return new RuleEvaluation
        {
            RuleId = rule.Id,
            RuleVersion = rule.Version,
            Category = rule.Category,
            SignalId = rule.SignalId,
            State = state,
            Outcome = escalated,
            Threshold = null,
            ObservedValue = observed,
            Coverage = coverage,
            CoverageSignalsMeasured = coverageMeasured,
            CoverageSignalsExpected = coverageExpected,
            SampleSupport = sample,
            MissingSignals = missing,
            ConflictingSignals = conflicting,
            CappedByCeiling = false,
            // An escalation is never capped by the pack ceiling: the ceiling bounds automatic
            // actions, not the duty to look.
            RequiresHumanReview = true,
            ReasonCode = reasonCode,
            Reason = reason,
        };
    }

    /// <summary>
    /// True when a signal has exactly one usable measurement. Conflicting rows and thin sample
    /// support are not measurement, so they do not raise coverage: coverage is the fraction of
    /// dimensions that actually answered, not the fraction that produced a row.
    /// </summary>
    private static bool IsMeasured(
        string signal,
        PolicyRule rule,
        IReadOnlyDictionary<string, IReadOnlyList<Evidence>> available)
    {
        if (!available.TryGetValue(signal, out var rows) || rows.Count == 0)
        {
            return false;
        }

        var distinct = rows
            .Where(row => row.Availability == EvidenceAvailability.Available && row.Value is not null)
            .GroupBy(CanonicalRow, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (distinct.Count != 1)
        {
            return false;
        }

        return rule.MinimumSampleSupport <= 0
            || (distinct[0].SampleSupport ?? 0) >= rule.MinimumSampleSupport;
    }

    /// <summary>The composition order: Quarantine, then Review, then Hold, then Allow.</summary>
    private static int ProtectionRank(PolicyVerdict verdict) => verdict switch
    {
        PolicyVerdict.Permit or PolicyVerdict.Observe => 0,
        PolicyVerdict.Hold => 1,
        PolicyVerdict.Review => 2,
        PolicyVerdict.Quarantine => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown policy verdict."),
    };

    private static RuleEvaluation? SelectDeciding(IReadOnlyList<RuleEvaluation> evaluations)
    {
        var fired = evaluations
            .Where(evaluation => evaluation.Outcome is not null)
            .OrderByDescending(evaluation => ProtectionRank(evaluation.Outcome!.Value))
            .ThenBy(evaluation => evaluation.RuleId, StringComparer.Ordinal)
            .ToList();

        return fired.Count == 0 ? null : fired[0];
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<Evidence>> IndexEvidence(IReadOnlyList<Evidence> snapshot)
    {
        var index = new Dictionary<string, IReadOnlyList<Evidence>>(StringComparer.Ordinal);

        foreach (var group in snapshot.GroupBy(row => row.SignalId, StringComparer.Ordinal))
        {
            index[group.Key] = group.ToList();
        }

        return index;
    }

    /// <summary>
    /// Canonical ordering: sort by the row's own canonical text. Input order is never carried into
    /// the snapshot, so two callers holding the same rows produce the same digest.
    /// </summary>
    private static IReadOnlyList<Evidence> Canonicalise(IReadOnlyList<Evidence> evidence) =>
        evidence
            .OrderBy(CanonicalRow, StringComparer.Ordinal)
            .ToList();

    private static string CanonicalRow(Evidence row)
    {
        var attributes = row.Attributes is null
            ? string.Empty
            : string.Join(";", row.Attributes.Select(attribute => $"{attribute.Name}={attribute.Value}"));

        return string.Join(
            '\u001f',
            row.SignalId,
            row.Origin.ToString(),
            row.Availability.ToString(),
            row.Value?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
            row.Confidence?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
            row.SampleSupport?.ToString(CultureInfo.InvariantCulture) ?? "-",
            row.SourceVersion,
            row.ObservedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            row.ObservedScope ?? "-",
            attributes);
    }

    private static string Digest(IReadOnlyList<Evidence> snapshot)
    {
        var canonical = string.Join('\n', snapshot.Select(CanonicalRow));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }

    private PolicyEvaluation Refusal(
        PolicyEvaluationInput input,
        IReadOnlyList<Evidence> snapshot,
        string digest,
        string reasonCode,
        string reason) => new()
        {
            TenantId = input.TenantId,
            PackId = input.Pack.Id,
            PackVersion = input.Pack.Version,
            RuleSetId = input.RuleSet.Id,
            RuleSetVersion = input.RuleSet.Version,
            ScorerVersion = input.Pack.ScorerVersion,
            Verdict = PolicyVerdict.Review,
            RequiresHumanReview = true,
            DecidingRuleId = null,
            DecidingRuleVersion = null,
            DecidingThreshold = null,
            DecidingReasonCode = reasonCode,
            DecidingReason = reason,
            MissingDimensions = [],
            Coverage = 0,
            Rules = [],
            EvidenceSnapshot = snapshot,
            EvidenceSnapshotDigest = digest,
            EvaluatedAt = _time.GetUtcNow(),
            DecidedBy = DecidedBy,
        };
}
