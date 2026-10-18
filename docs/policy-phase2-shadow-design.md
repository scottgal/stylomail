# Policy Phase 2: shadow evaluation, no action

Status: **design and shape, edit only.** Phase 2 adds a pack evaluation beside the existing decision
and records it on the assessment. It does not change an action.

## The decision

**The pack evaluator runs as a second opinion, and its verdict is recorded, never applied.**

The existing engine still chooses the action. When a pack is active and the shadow path is enabled,
the evaluator runs over the same evidence and its `PolicyEvaluation` is copied onto the assessment as
a `PolicyDecisionRecord` with `Shadow = true`. The assessment's `Action` is byte-identical to the
legacy result. `ProposedActionInShadow` keeps its existing meaning, which is the legacy engine's
shadow mode; the pack's candidate is the record's `Verdict`, not a second action field.

This separation matters because the two shadows answer different questions:

| Mode | Question | Field |
|---|---|---|
| Existing `AssessmentContext.ShadowMode` | What would the legacy engine have done if forwarding were off? | `ProposedActionInShadow` |
| Phase 2 pack shadow | What does the pack's deterministic rule set say about this evidence? | `PolicyDecisionRecord` |

Conflating them would make one field carry two candidates, and the console could not say which
engine proposed what.

## Where it runs

`MailAssessor.AssessAsync` and `ChatAssessor.AssessAsync`, after evidence composition and after the
legacy decision, before the `MailAssessment` is constructed. The evidence list is the one that
already exists; the evaluator must not see a second composition.

```
evidence ──> CompositeRiskScorer ──> MailPolicyEngine ──> legacy action
                                   └─> PolicyShadowEvaluator ──> PolicyDecisionRecord (shadow)
assessment = { Action = legacy action, Policy = shadow record }
```

A new optional constructor dependency, `IPolicyShadowEvaluator?`, keeps the composition root in
control. When it is null or disabled, the assessment's `Policy` is null and behaviour is unchanged.

## The record

`StyloMail.Core.PolicyDecisionRecord`, nullable on `MailAssessment.Policy`:

```csharp
public sealed record PolicyDecisionRecord
{
    public required string PackId { get; init; }
    public required string PackVersion { get; init; }
    public required string RuleSetId { get; init; }
    public required string RuleSetVersion { get; init; }
    public required string ScorerVersion { get; init; }
    public required string EvaluatorVersion { get; init; }
    public required PolicyVerdict Verdict { get; init; }
    public required bool RequiresHumanReview { get; init; }
    public string? DecidingRuleId { get; init; }
    public int? DecidingRuleVersion { get; init; }
    public double? DecidingThreshold { get; init; }
    public required string DecidingReasonCode { get; init; }
    public required string DecidingReason { get; init; }
    public required IReadOnlyList<string> MissingDimensions { get; init; }
    public required double Coverage { get; init; }
    public required IReadOnlyList<string> BindingIds { get; init; }
    public required IReadOnlyList<PolicyPropertyFact> PropertyFacts { get; init; }
    public required string EvidenceSnapshotDigest { get; init; }
    public required DateTimeOffset EvaluatedAt { get; init; }
    public bool Shadow { get; init; }
}
```

`PolicyPropertyFact` is a small Core record (`Key`, `Value`, `State`, `Source`, `Scope`) so the
record does not force Core to reference Profiles. It is empty in Phase 2; Phase 3 fills it from the
effective-property read.

**Null is "no pack was active", never `Permit`.** A store failure with a pack configured is not a
null record: it is a record with `Verdict = Review`, `RequiresHumanReview = true` and
`DecidingReasonCode = policy.pack_store_unavailable`, consistent with the outage ruling. A null
record therefore has exactly one meaning.

## The evaluator port

`StyloMail.Policy` gains:

```csharp
public interface IPolicyShadowEvaluator
{
    Task<PolicyDecisionRecord?> EvaluateAsync(
        string tenantId,
        IReadOnlyList<Evidence> evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
```

`PolicyShadowEvaluator` implements it over `IPolicyPackReader`, `IPolicyRuleSetReader` and a small
`PolicyShadowOptions` (`Enabled`, `Jurisdiction`). It:

1. Returns null when disabled, when the jurisdiction is unset, or when no pack is effective.
2. Resolves the pack's rule set by version; a missing live set records
   `policy.rule_set_not_live` with `Verdict = Review`.
3. Runs `DeterministicPolicyEvaluator` over the evidence and copies the explanation into the record
   with `Shadow = true`.
4. Catches store and evaluator failures and records `policy.pack_store_unavailable` or
   `policy.evaluator_failed` as a `Review` shadow record. It never throws into the assessment path,
   and it never changes the action.

There is no bindings or properties read in Phase 2. `BindingIds` and `PropertyFacts` are empty, and
the record says so rather than omitting the fields.

## Configuration

```
StyloMail:Policy:Shadow:Enabled      false by default
StyloMail:Policy:Shadow:Jurisdiction  required for the shadow to run
```

The Host composes the shadow evaluator from its existing `IPolicyPackReader` and
`IPolicyRuleSetReader` registrations and passes it into `MailAssessor` and `ChatAssessor`. A
deployment with the shadow off does no pack read at all.

## What Phase 2 does not do

- It does not apply the verdict. A pack `Quarantine` beside a legacy `Allow` leaves `Action = Allow`.
- It does not add `MailAction.Review`; a pack `Review` is recorded in the record only.
- It does not read Profiles, so there is no `ProjectReference` change.
- It does not add a table, a route or a migration. The record travels inside the existing assessment
  JSON in the ledger.

## Tests

Assessment:
- A fake shadow evaluator's record appears on `MailAssessment.Policy`; `Action` is unchanged.
- No shadow evaluator: `Policy` is null; the assessment is unchanged.
- No active pack: null.
- Store failure: `Verdict = Review`, `policy.pack_store_unavailable`, `Shadow = true`, action unchanged.

Policy:
- The shadow evaluator returns the pack's rule id, version, threshold and digest for a known
  fixture, and returns null when no pack is effective.
- A missing rule set yields the named Review record rather than a throw.

Host:
- `DecisionResponse` mirrors `Policy`; an assessment with a null record serves an explicit null, and
  the existing response fields are unchanged.
