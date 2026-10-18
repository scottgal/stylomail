# Policy host wiring: packs, bindings and the evaluator call site

Decision record for the slice bounded by `overview-` on 18 Oct 2026, after the first policy slice
was accepted at merged main `264b177`.

Status: **design accepted in shape, Phase 1 not built.** Rulings A-E were issued on 18 Oct 2026:
the six tables are approved in shape (no DDL executed), `PolicyVerdict` moves to Core, the review
mapping is option C (a distinct bounded Review state), a store outage caches the last effective pack
and reviews on a miss, and v1 has one pack per tenant and jurisdiction. No table was created, no
route was mapped, no evaluator call was added to the live path. The first slice (`5093d77`) is a
pure library and is not wired to anything.

## The decision

**Wire the policy surface in four bounded phases, and keep the live path on the legacy engine until
a pack exists, a binding path exists and the review state can be represented honestly.**

1. **Stores and lifecycle, read-only API.** Pack, rule-set and binding stores with the draft,
   approved, effective, superseded lifecycle. The evaluator is not called. Nothing an operator can
   do here changes how a message is decided.
2. **Shadow evaluation.** The Host calls the evaluator beside the existing engine for tenants with
   an active pack, records the candidate verdict, and leaves the delivered action untouched.
3. **Authoritative evaluation.** Only after Phase 1 is green and `MailAction.Review` exists with its
   bounded state: the pack verdict composes with the legacy tiers, and the legacy engine remains the
   rollback.
4. **Feedback writes.** Promotion and retirement become able to change a rule only after the
   decision record is stored and joined to labels. Until then the feedback capture stays read-only.

The reason for the order is not ceremony. A pack that can change an action before a binding store
exists is a configuration with no manual override path. A shadow evaluation that is stored on the
assessment before the decision record exists is a verdict the console cannot explain. And an
authoritative review verdict before the review state exists is a review that lasts five seconds.

## Ownership and boundaries

| Area | Owner | This design's boundary |
|---|---|---|
| `src/StyloMail.Policy` | `policy-` | Pack schema, evaluator, binding rules, feedback gates. No storage, no Host types. |
| Host DDL, routes, contracts, DI | `ingress-` by default; `policy-` for Phase 1 | `ingress-` has exited. Phase 1 is implemented by `policy-` in its own worktree, with the migration commit sent to `overview-` for review before it is run. |
| `src/StyloMail.Profiles` | `profiles-` | Policy reads the effective-property contract; it never writes properties and never duplicates them. |
| Console | `desktop-` | Consumes capability descriptors and consequence inputs; owns the sentence templates. |
| Proxy and runtime reliability | `review-` | Not touched. |

`StyloMail.Policy` already references only `StyloMail.Core`. The Host references Policy through
Assessment. No new NuGet dependency is needed for this design.

## The pack store

### Tables

```sql
CREATE TABLE host_policy_pack (
    tenant_id        TEXT NOT NULL,
    pack_id          TEXT NOT NULL,
    version          TEXT NOT NULL,
    state            TEXT NOT NULL,        -- draft | approved | effective | superseded | withdrawn
    jurisdiction     TEXT NOT NULL,        -- selected from the pack context, queryable
    content_sha256   TEXT NOT NULL,        -- hash of the immutable payload bytes
    payload          TEXT NOT NULL,        -- PolicyPack JSON
    author           TEXT NOT NULL,        -- principal id, not a display name
    approver         TEXT NULL,
    second_approver  TEXT NULL,
    effective_from   TEXT NULL,
    superseded_by    TEXT NULL,            -- "<pack_id>@<version>"
    created_at       TEXT NOT NULL,
    approved_at      TEXT NULL,
    PRIMARY KEY (tenant_id, pack_id, version)
);

CREATE INDEX ix_host_policy_pack_tenant_state
    ON host_policy_pack (tenant_id, state, effective_from);

-- At most one effective pack per tenant and jurisdiction. A second activation in the same
-- jurisdiction is a transactional supersession, not a second live pack.
CREATE UNIQUE INDEX ux_host_policy_pack_effective
    ON host_policy_pack (tenant_id, jurisdiction)
    WHERE state = 'effective';

CREATE TABLE host_policy_pack_event (
    event_id    TEXT NOT NULL PRIMARY KEY,
    tenant_id   TEXT NOT NULL,
    pack_id     TEXT NOT NULL,
    version     TEXT NOT NULL,
    event       TEXT NOT NULL,             -- drafted | edited | approved | activated | superseded | withdrawn
    actor       TEXT NOT NULL,             -- principal id
    reason      TEXT NOT NULL,
    at          TEXT NOT NULL,
    payload     TEXT NULL                  -- before/after state where it changed
);
```

The payload is the whole `PolicyPack` from the first slice. The indexed columns exist so the
effective lookup and the audit list do not deserialise every row; the document exists because a
pack is a versioned artefact whose full shape matters more than its queryability. This is the same
shape the decision ledger already uses at `HostDatabase.cs:299-321` (read at `264b177`).

### Lifecycle

```
draft --approve--> approved --activate--> effective --supersede--> superseded
  |                    |
  +----withdraw--------+----withdraw----> withdrawn
```

- **Draft is editable.** Every save revalidates with `PolicyPackValidator`; an invalid draft is
  refused and the refusal codes are returned to the console. `PolicyPackValidator` already refuses
  an Off or Observe hard floor, an unknown category, an undeclared floor and a lowering without a
  second approver (`PolicyPacks.cs`).
- **Approved is immutable.** A change is a new version. `content_sha256` is over the canonical
  payload bytes (sorted keys, invariant culture) and is computed at approval, so an approval names
  bytes rather than a version label. A stored payload whose hash no longer matches its row is a
  corruption, and the row is refused at read.
- **Activation is transactional.** In one transaction: re-run validation against the previous
  effective pack in the same jurisdiction, verify the referenced rule-set version is live, insert
  the supersession event, set the prior row to `superseded` with `superseded_by`, set this row to
  `effective`. The partial unique index is the second line of defence: a bug in the service cannot
  produce two effective packs.
- **No backdating.** `effective_from` must be at or after `approved_at`, and the store refuses a
  backdated activation with `pack.effective_before_approval`, tested. A pack is not retroactive
  evidence; a decision already made is explained by the version that was effective when it was made.
- **Approval is by authenticated principals.** The request carries a principal id, not a free-text
  name. A lowering or a capture widening needs a second principal with `Administer`, and the two
  must be distinct. A same-principal second approval is refused with
  `pack.second_approver_not_distinct`, tested, not merely documented. The pack payload keeps
  human-readable author and approver fields for portability, and the store keeps the principal ids
  that authorised it. This is how spec 9.15's "recorded operator decision" becomes checkable rather
  than declared.
- **Withdrawal before effect** is allowed. Withdrawing an effective pack is not: the rollback is
  to activate the previous version, which is an auditable event, or to deactivate the pack path
  entirely, which reverts the tenant to the legacy engine.

### Lookup

`GetEffectiveAsync(tenantId, jurisdiction, now)`:

1. Look for a row in `effective` state whose `effective_from <= now`.
2. If none, look for the most recent `approved` row whose `effective_from <= now` and promote it
   lazily in a transaction. A background scheduler is deliberately not required for correctness.
3. If neither exists, return null. **Null means "no pack", not "all categories Off".** The
   evaluator is not called and the legacy decision stands.

**v1 selects one pack per tenant and jurisdiction, from the deployment's declared jurisdiction.**
Ruling E: an entity's country may raise protection but may not select a different pack. A known,
verified or declared country that differs from the pack's jurisdiction does not switch packs; it
adds the jurisdiction-sensitive categories to `EffectivePolicy.ForcedReviewCategories`, so a fired
rule in them escalates to `Review` with `policy.jurisdiction_differs_from_pack`. An inferred or
imported country is ineligible (`profiles-`' contract already enforces this). Per-entity vertical
and multi-pack selection are deferred.

### Store ports

The decision path gets **read-only** ports, so the engine cannot write:

```csharp
public interface IPolicyPackReader
{
    Task<PolicyPack?> GetEffectiveAsync(
        string tenantId, string jurisdiction, DateTimeOffset now, CancellationToken cancellationToken);

    Task<PolicyPack?> GetVersionAsync(
        string tenantId, string packId, string version, CancellationToken cancellationToken);
}

public interface IPolicyRuleSetReader
{
    Task<PolicyRuleSet?> GetAsync(
        string tenantId, string ruleSetId, string version, CancellationToken cancellationToken);
}
```

Lifecycle writes live in the Host as `PolicyPackAdministration`, not in the evaluator's dependency
graph. This is the same split `profiles-` uses for properties: Host owns writes and confirmation,
policy reads. It also means a future caller cannot reach a mutation through the decision path.

## The rule-set store

```sql
CREATE TABLE host_policy_rule_set (
    tenant_id      TEXT NOT NULL,
    rule_set_id    TEXT NOT NULL,
    version        TEXT NOT NULL,
    state          TEXT NOT NULL,          -- candidate | live | retired
    content_sha256 TEXT NOT NULL,
    payload        TEXT NOT NULL,          -- PolicyRuleSet JSON
    promoted_at    TEXT NULL,
    approver       TEXT NULL,
    PRIMARY KEY (tenant_id, rule_set_id, version)
);

CREATE TABLE host_policy_rule_event (
    event_id TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    rule_set_id TEXT NOT NULL,
    version TEXT NOT NULL,
    event TEXT NOT NULL,                   -- proposed | shadowed | promoted | retired
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    at TEXT NOT NULL,
    payload TEXT NULL
);
```

- `PolicyPack.RuleSetVersion` must resolve to a `live` row in the same tenant. The evaluator already
  refuses a version mismatch (`policy.rule_set_version_mismatch`); the store adds the tenant and
  state checks at activation.
- A `candidate` row exists only for shadow evaluation. It cannot be referenced by a pack.
- Promotion is a state transition gated by `RulePromotionGate`; retirement by
  `RuleRetirementMonitor`. Both are library-only in Phase 1 and 2.

## Bindings and effective tolerance

Bindings from the first slice are stored as immutable payloads with an append-only event stream:

```sql
CREATE TABLE host_policy_binding (
    tenant_id    TEXT NOT NULL,
    binding_id   TEXT NOT NULL,
    state        TEXT NOT NULL,            -- active | revoked | expired
    scope_kind   TEXT NOT NULL,
    scope_id     TEXT NOT NULL,
    action       TEXT NOT NULL,
    category_id  TEXT NULL,
    target_level TEXT NULL,
    starts_at    TEXT NOT NULL,
    expires_at   TEXT NOT NULL,
    review_by    TEXT NOT NULL,
    payload      TEXT NOT NULL,
    PRIMARY KEY (tenant_id, binding_id)
);

CREATE INDEX ix_host_policy_binding_scope
    ON host_policy_binding (tenant_id, scope_kind, scope_id, state, expires_at);

CREATE TABLE host_policy_binding_event (
    event_id   TEXT NOT NULL PRIMARY KEY,
    tenant_id  TEXT NOT NULL,
    binding_id TEXT NOT NULL,
    event      TEXT NOT NULL,              -- created | reviewed | revoked | expired
    actor      TEXT NOT NULL,
    reason     TEXT NOT NULL,
    at         TEXT NOT NULL
);
```

- A binding is validated against the effective pack **at creation**: no hard-floor lowering below
  `Strict`, no capture wider than the pack's declared mode. `PolicyBindingValidator` already
  enforces both.
- Expiry is resolved at read with `IsActiveAt(now)`; there is no reliance on a sweeper. The
  `OnExpiry` resolution is part of the binding: `RevertToPack` or `HoldForReview`, with no "keep"
  member.
- A binding row is never deleted. Revocation is an event and a state change.
- Effective resolution for a category is:

  ```
  level = pack.CategoryLevels[category]
  level = max(level, propertyProtectiveFloor)       // properties may only raise
  level = binding.LowerTolerance or RaiseTolerance   // lower only with a second approver
  assert level >= HardFloorMinimum for hard floors
  ```

  The resolver returns an `EffectivePolicy` that names the pack version, every contributing
  binding id and every property fact used. The evaluator records those ids, so a decision is
  replayable without re-reading the store. This is an additive record change in `StyloMail.Policy`,
  not a schema change.

## Consuming the profiles contract

`profiles-` has declared the read contract stable: `src/StyloMail.Profiles`,
`IPropertyStore.GetEffectiveAsync(tenantId, PropertyScopeChain, keys, now, ct)`, returning one
`EffectiveProperty` per requested key with `Value`, `State`, `Source`, `Scope`, model block,
evidence refs, confirmation, candidates and an explicit `Availability`. Policy adds a ProjectReference
**after** that library is merged, and reads only. It never calls the write or confirm routes.

**v1 consumes exactly four keys.** The library defines `age_band`, `country`, `role` and
`language`, and `GetEffectiveAsync` throws `ArgumentException` for any other key. Do not pass
`audience` or `contact_policy`: they are not implemented, and an unknown key must not be sent
hoping it comes back `Unknown`. `audience` is a pack-context field, not an entity property, and
`contact_policy` is a later slice.

| Key | Use | Rule |
|---|---|---|
| `age_band` | audience protection | Most protective band governs. A minor raises `adult_content` to at least `Strict`, `coercion_and_extortion` to at least `Strict`, `harassment` to at least `Standard`. An inferred adult never lowers anything. |
| `country` | protective facts | Verified or declared only. A known country different from the pack's jurisdiction does not select a pack in v1: it adds the jurisdiction-sensitive categories to `ForcedReviewCategories`. An inferred or imported country is ineligible. |
| `role`, `language` | evidence context | Passed to rules as facts. They never lower a protection. |

An age band of `unknown` does not lower a level and does not pick a default. It adds the affected
categories to `EffectivePolicy.ForcedReviewCategories`, so any rule that fires in one of them
escalates to `Review`. This is the "uncertain band may raise sensitivity and escalate" rule
expressed as an outcome rather than a guessed level. `State = Suggested` is not unusable: it means
no human has confirmed it, and a binding that requires human authority must check `State` or
`ConfirmedAt`, never the value alone.

The scope chain is built with `PropertyScopeChain.Create(addressed, ancestors...)`. A conversation
chain is used only when the addressed scope is the conversation; an entity-only assessment addresses
the entity, with domain and company ancestors when known. A wider read can never include a
conversation row, by construction. `Unknown`, `NotProduced` and `NotApplicable` are distinct: a
missing value is never read as a value, a default or a zero.

**The dependency direction is Policy to Profiles, never the reverse.** `StyloMail.Profiles`
references only the BCL. The shared vocabulary (`age_band` ids, scope chain) stays in Profiles;
Policy imports it. If Profiles ever needs a Policy type, the type goes in Core instead.

## The evaluator call site

Today the only action is chosen at `MailAssessor.cs:426` and `ChatAssessor.cs:90` (read at
`264b177`), both constructing a `MailPolicyEngine` per assessment. The new pack evaluator does not
replace those tiers; it slots in beside them.

Order inside an assessment after evidence composition:

1. **Legacy tier 1-3, unchanged.** Kill switch, outbound quota, verified security-rule violations,
   suspected-compromise posture. These are established facts and operational states. A pack cannot
   override them, and an absent pack cannot remove them.
2. **Pack evaluator, over the same evidence.** It produces a `PolicyEvaluation` with its typed
   verdict, rule version, threshold and evidence digest.
3. **Compose by most protective outcome.** The composed action is the more protective of the legacy
   risk decision and the pack verdict. A pack never makes the legacy decision more permissive.
4. **Recipient preference stays last.** It cannot relax a pack `Review`, `Hold` or `Quarantine`,
   exactly as it cannot relax the security tiers today.

The composition is a pure function in `StyloMail.Policy` (`PackPolicyComposer`) with a pinned
mapping table. It takes the legacy `PolicyDecision`, the pack `PolicyEvaluation` and the effective
policy, and returns the composed action plus reason codes. The assessment keeps one action, not two
competing ones.

### The review state

Ruling C: `PolicyVerdict.Review` maps to a new `MailAction.Review`, not to a short Hold and not to
Quarantine. The semantics are pinned:

- Delivery is withheld. The message appears in a review queue with the evidence and the rule that
  produced it, and a human decides.
- The state has a deadline, proposed at 24 hours and configurable per pack (`PolicyPack.ReviewDeadline`;
  a new pack field, not a table change). At the deadline it escalates to Quarantine or follows the
  pack's explicit rule. It never auto-releases and never silently delivers.
- `MailAction.Review` is appended to the enum after `Reject` so existing numeric values do not move.
  It touches Core, the Host contracts, the console and the queue worker, and it needs its own review
  store and schema approval. That schema is not part of the six tables in this design.
- Phase 2 may shadow a Review outcome but must not act on it. C must land before Phase 3 becomes
  authoritative.

Copy is part of the contract, not an afterthought: Review says "Awaiting human review; no final
delivery decision has been made." Quarantine says "Stopped before delivery while awaiting review."
Never "under review" for containment, and never containment language for Review.

### Store outage

If the pack store is unreadable, the Host uses the last effective pack cached in memory after a
successful read. On a cache miss it returns `Review` with `policy.pack_store_unavailable`, never
`Defer` and never a permissive default. Until `MailAction.Review` exists, the authoritative
composition falls back to the most protective legacy outcome, or Quarantine when the legacy outcome
is `Allow`. The cache-miss path is a test, not a comment. A deferral returns the mail to the sender
and looks like a policy decision, which is why it is refused as a fallback.

## The decision record

`MailAssessment` is in `StyloMail.Core` and cannot reference `StyloMail.Policy`. Ruling B approved
the move, and `PolicyVerdict` now lives in `StyloMail.Core/PolicyVerdict.cs`; `StyloMail.Policy`
imports it. `MailAssessment` gains an optional `PolicyDecisionRecord`:

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
    public required IReadOnlyList<string> MissingDimensions { get; init; }
    public required double Coverage { get; init; }
    public required IReadOnlyList<string> BindingIds { get; init; }
    public required IReadOnlyList<PolicyPropertyFact> PropertyFacts { get; init; }
    public required string EvidenceSnapshotDigest { get; init; }
    public required DateTimeOffset EvaluatedAt { get; init; }
    public bool Shadow { get; init; }
}
```

- **Null is "not evaluated", never `Permit`.** A row written before this field existed, or a tenant
  with no active pack, has no policy record. The console says "No policy pack was active", not a
  clean verdict.
- **Shadow is visibly not applied.** A shadow record carries `Shadow = true` and the console says
  "no action was applied". It may show the candidate verdict, but never as the outcome.
- The evidence rows are already on `MailAssessment.Evidence`; the record carries the digest, not a
  second copy. That keeps one evidence list and one arithmetic.
- The ledger stores the whole assessment as JSON at `HostDatabase.cs:299-321`, so this needs **no
  new evaluation table**. If the promotion loop later needs SQL over verdicts, a derived
  `host_policy_evaluation` extract can be built from the ledger; it is not part of this slice.
- `AssessmentVersions.PolicyVersion` keeps naming the legacy engine. The new record names the pack
  and rule set. Overloading one field with both would make a replay ambiguous.

The Host contract mirrors the record on `DecisionResponse`; `DecisionResponse.cs:28` and `:112`
already show the pattern with `ProposedActionInShadow`. `desktop-` reads it as a new pane section,
not as prose derived from a score.

## Feedback capture stays read-only

The existing `host_feedback_record` table (`HostDatabase.cs:325-343`) already joins a label to
`decision_id`, which is the assessment id. `FeedbackEndpoints` (`OperatorEndpoints.cs:18`) writes
those labels. Nothing in this slice changes that path.

Held read-only:

- No candidate-rule writes, no promotion transitions, no retirement transitions. `RulePromotionGate`
  and `RuleRetirementMonitor` remain library-only, exercised by fixtures.
- No model-label rows. The current table has no model id, version, prompt contract or evidence
  digest columns, so a weak label cannot be stored with its provenance yet. Adding those columns is
  a schema change and is **not** proposed here; it belongs with the promotion slice.
- No label whose `decision_id` does not resolve to a stored decision. When the join is added, a
  label whose evidence digest does not match the stored decision is refused rather than attached to
  a similar one.

## Action capability descriptor and consequence inputs

`desktop-` currently hard-codes consequence copy in three places (`ShellModel.cs:971-1026`, read at
`264b177`) and hard-codes outcome descriptions in `DecisionView.cs:255-262`. The descriptor moves
the facts to the Host and the sentence templates to the console, so a policy change cannot leave
stale prose behind.

### Shape

Desktop's additions are accepted: `MonitorAllMail` is an explicit kind, capability availability and
a denial reason are served, the target and effective scope are named with a display-safe reference,
affected counts use the same availability rule, the resulting delivery or containment state is
explicit, Review carries its owner, deadline, escalation and reversal, and notification and audit
effects are stated.

```csharp
public enum PolicyActionKind
{
    Hold, Quarantine, Review, ReleaseQuarantine,
    PauseSender, ResumeSender, BlockSender, MonitorAllMail, CaptureText,
}

public enum ConsequenceAvailability
{
    Measured,       // a number from a stated window
    NotProduced,    // the query ran or exists but produced no usable value
    NotApplicable,  // the question does not apply to this action or this tenant
}

public enum PolicyCapabilityAvailability
{
    Available, NotAvailable,
}

public enum PolicyResultingState
{
    Delivered, Delayed, WithheldForReview, Contained, Rejected, Deferred, NotApplicable,
}

public sealed record PolicyActionCapability
{
    public required PolicyActionKind Kind { get; init; }
    public required PolicyCapabilityAvailability Availability { get; init; }
    public required string? DenialReason { get; init; }          // code, e.g. "pack_level_off"
    public required int AuthorityTier { get; init; }              // 0..4; PII has its own tier
    public required bool Automatic { get; init; }
    public required bool RequiresHumanApproval { get; init; }
    public required string RequiredPrivilege { get; init; }       // "Review" | "Administer"
    public required IReadOnlyList<BindingScope> Scopes { get; init; }
    public required PolicyResultingState ResultingState { get; init; }
    public required bool Reversible { get; init; }
    public PolicyActionKind? ReversalAction { get; init; }
    public TimeSpan? MaximumDuration { get; init; }
    public required bool ReasonRequired { get; init; }
    public required string AuditEventKind { get; init; }
    public required string NotificationEffect { get; init; }      // "none" | "review_queue" | "sender_owner" | ...
    public required string ConsequenceTemplateId { get; init; }
    public required int ConsequenceTemplateVersion { get; init; }
    public required IReadOnlyList<ConsequenceInputDescriptor> Inputs { get; init; }
}

public sealed record ConsequenceInputDescriptor
{
    public required string Name { get; init; }
    public required string Unit { get; init; }
    public required ConsequenceAvailability DefaultAvailability { get; init; }
    public required string Source { get; init; }                  // "decision_ledger" | "pack" | "binding" | "property"
}

public sealed record ConsequenceInput
{
    public required string Name { get; init; }
    public string? Value { get; init; }                           // null exactly when not produced
    public required ConsequenceAvailability Availability { get; init; }
    public int? SampleSupport { get; init; }
    public double? Coverage { get; init; }
    public required string Window { get; init; }
    public DateTimeOffset? MeasuredAt { get; init; }
}

public sealed record PolicyTarget
{
    public required BindingScope ScopeKind { get; init; }
    public required string DisplayReference { get; init; }        // display-safe, never a raw address
    public required string ScopeId { get; init; }                 // opaque, for the Host call
}

public sealed record PolicyReviewTerms
{
    public required string Queue { get; init; }                   // disclose-safe queue name or "unknown"
    public string? Owner { get; init; }
    public required DateTimeOffset Deadline { get; init; }
    public required string EscalationAction { get; init; }        // "quarantine" | "pack_rule"
    public required string NextAction { get; init; }              // reversal or decision path
}

public sealed record PolicyConsequence
{
    public required string TemplateId { get; init; }
    public required int TemplateVersion { get; init; }
    public required IReadOnlyList<ConsequenceInput> Inputs { get; init; }
    public required PolicyTarget Target { get; init; }
    public required PolicyResultingState ResultingState { get; init; }
    public PolicyReviewTerms? Review { get; init; }
    public required string PackId { get; init; }
    public required string PackVersion { get; init; }
    public string? RuleSetVersion { get; init; }
    public string? EvidenceSnapshotDigest { get; init; }
}
```

### Inputs the console needs

Every input carries its own availability. A missing count is `NotProduced` or `NotApplicable`,
never `0`.

- `effective_level`: the resolved tolerance for the category, and `hard_floor` with a reason.
- `target_scope_kind`, `target_display_reference`: `PolicyTarget`, display-safe. `binding_scope`
  alone cannot say what or who is affected.
- `affected_messages`, `affected_relationships`: counts when measurable, with the same availability
  semantics and the window they were measured over.
- `would_have_been_held_last_window`, `confirmed_last_window`, `overridden_last_window`,
  `reversed_last_window`: counts over a stated window, with sample support and coverage, from the
  decision ledger. Spec 9.15's operator surface ("12 messages would have been held last month, 3
  were confirmed") is exactly this row.
- `resulting_state`: one of `PolicyResultingState`, so the copy cannot turn a review into a
  containment claim.
- `review_queue`, `review_owner`, `review_deadline`, `review_escalation`, `review_next_action`:
  present for Review, `NotApplicable` otherwise. The queue and owner are `unknown` when they cannot
  be disclosed, never omitted.
- `binding_scope`, `binding_expires_at`, `binding_review_by`, `binding_reversible`: from the active
  binding, or `NotApplicable` when the action is not bound.
- `capture_retention_days`, `capture_max_characters`, `capture_statement`: from the pack and the
  capture binding. `NotApplicable` when capture is not the action.
- `reversal_action`, `reversal_deadline`, `expiry`: from the static descriptor.
- `notification_effect`, `audit_event_kind`: stated, not implied.
- `pack_version`, `rule_set_version`, `evidence_digest`: provenance, so the copy can link to the
  explanation.

### Copy rules

These sentences are pinned so a template change is visible:

| State | Copy |
|---|---|
| Review | "Awaiting human review; no final delivery decision has been made." |
| Quarantine | "Stopped before delivery while awaiting review." |
| No policy record | "No policy pack was active." |
| Shadow record | "Shadow only; no action was applied." |

"Under review" is never used for containment, and containment language is never used for Review.

### Who owns the sentence

The Host serves `templateId`, `templateVersion` and typed inputs. The console owns the templates,
keyed by `(templateId, templateVersion)`, and renders the sentence. A template version is bumped
when the wording changes; the descriptor version is bumped when the inputs change. The Host never
sends prose that a policy change can make false without a version bump.

### Routes

| Route | Verb | Privilege | Phase |
|---|---|---|---|
| `/v1/policy/packs` | GET | Review | 1 |
| `/v1/policy/packs/{id}` | GET | Review | 1 |
| `/v1/policy/packs` | POST (draft) | Administer | 1 |
| `/v1/policy/packs/{id}/{version}/approve` | POST | Administer | 1 |
| `/v1/policy/packs/{id}/{version}/activate` | POST | Administer | 1 |
| `/v1/policy/packs/{id}/{version}/withdraw` | POST | Administer | 1 |
| `/v1/policy/bindings` | GET | Review | 1 |
| `/v1/policy/bindings` | POST | Administer | 1 |
| `/v1/policy/bindings/{id}/revoke` | POST | Administer | 1 |
| `/v1/policy/capabilities` | GET | Review | 1 (static descriptors; counts report `NotProduced`) |
| `/v1/decisions/{id}` | GET | Review | adds `PolicyDecisionRecord` and per-case consequences in Phase 2 |

The split mirrors `ApiRoutes.cs`: reads on `Review`, mutations on `Administer`. Approval and
activation are mutations, and the second approver must be a distinct authenticated principal with
`Administer`, checked from the request identity, not from a body string.

The review queue itself is not in Phase 1. `MailAction.Review` and its bounded state are a separate
slice with their own store and schema approval; the capability endpoint in Phase 1 reports the
action as `NotAvailable` with `denial_reason = "review_state_not_implemented"` until it lands. That is
an honest absence, not a hidden one.

## Failure modes and invariants

These are the tests each phase must pin.

1. **No active pack.** The evaluator is not called. The legacy decision is unchanged. The assessment
   carries `Policy = null`, not `Permit`.
2. **Invalid approved pack.** Activation is refused. A row that fails validation is never read as
   effective, and a hash mismatch is a refusal, not a warning.
3. **Two effective packs.** Impossible: the unique partial index plus the activation transaction.
4. **Cross-tenant read.** A pack id from another tenant resolves to null. No route enumerates across
   tenants.
5. **Lowering without a second approver.** Refused at approval, with the violated level named.
   A hard floor below `Strict` is refused regardless of approvers. A same-principal second approval
   is refused with `pack.second_approver_not_distinct`.
6. **Backdating.** `effective_from` before `approved_at` is refused with
   `pack.effective_before_approval`.
7. **Profiles unavailable or a property unknown.** The conservative default applies. No inferred
   adult lowers a protection, and a known country that differs from the pack's jurisdiction adds the
   jurisdiction-sensitive categories to `ForcedReviewCategories`.
8. **Pack store outage.** The effective pack is immutable and cached in memory after first read. A
   cache miss during an outage produces `Review` with `policy.pack_store_unavailable`, never `Defer`
   and never `Permit`. Until `MailAction.Review` exists, the fallback is the most protective legacy
   outcome, or Quarantine when that outcome is `Allow`.
9. **Evaluator throws.** The composition falls back to the legacy decision and records
   `policy.evaluator_failed`. An exception never produces an `Allow` that the pack would not have.
10. **Binding expiry.** Resolved at read. `HoldForReview` holds the scope; `RevertToPack` returns to
    the pack. No binding becomes permanent by being forgotten.
10. **Shadow mode.** Records only. The delivered action is byte-identical to the production action,
    and the shadow record's `Applied` is always false.
11. **Shadow mode.** Records only. The delivered action is byte-identical to the production action,
    and the shadow record's `Applied` is always false.
12. **A pack never relaxes the legacy decision.** The composed action is at least as protective as
    the legacy result for every pair in the mapping table, and a pack `Permit` cannot turn a legacy
    `Hold`, `Quarantine`, `Defer` or `Reject` into an `Allow`. Pinned by a table-driven test, not by
    a comment.
13. **Recipient preference stays last.** It cannot relax a pack `Review`, `Hold` or `Quarantine`. A
    test covers each of the three, because an action-only test would relax the review state the way
    an earlier defect relaxed the semantic-blackout hold.

## Phases and their tests

**Phase 1: stores, lifecycle, read-only routes.**
- Host migration and store tests: draft edit revalidates, approved immutable, hash mismatch refused,
  one effective per jurisdiction, supersession in one transaction, withdrawal before effect only,
  cross-tenant isolation, principal-based second approval, same-principal refusal, backdated
  activation refusal, auth split.
- Capability endpoint tests: every kind is present (including `MonitorAllMail`), availability and
  denial reasons are explicit, and no consequence count is reported as zero when it is `NotProduced`.
- No assessment path changes and **the evaluator is not called from any Phase 1 route**. The Policy
  suite and the Host suite stay green.

**Phase 2: shadow evaluation.**
- `PolicyDecisionRecord` in Core, mirrored on `DecisionResponse`, with `Shadow = true`.
- Assessment test: a tenant with an active pack records a candidate verdict; the action and the
  queue behaviour do not change; the evidence digest matches a re-evaluation of the same rows.
- A tenant with no pack records null and is otherwise unchanged.
- A cache-miss test: the shadow path records `policy.pack_store_unavailable` and still does not
  change the action.
- Desktop reads the record in a read-only pane section.

**Phase 3: authoritative composition.**
- Depends on `MailAction.Review` and its bounded review state (option C) and on
  `StyloMail.Profiles` being merged.
- Composition tests: a pack cannot relax legacy tier 1-3; the more protective outcome wins; a
  binding raises or lowers within its scope; a hard floor cannot be lowered; a country difference
  reviews the jurisdiction-sensitive categories; a missing property reviews rather than defaults;
  the cache-miss fallback is Quarantine or the most protective legacy outcome.
- Rollback: deactivating the pack returns the tenant to the legacy engine at the next assessment.

**Phase 4: feedback writes.**
- Promotion and retirement transitions with the full gate; label provenance columns; a refused
  promotion writes no rule-set state.
- Read-only until the decision record is stored and the join is proven.

## Open questions for `overview-`

Rulings A-E are recorded in the sections above: schema approved in shape, `PolicyVerdict` moved to
Core, review mapping C, cache-and-review on a store miss, one pack per tenant and jurisdiction. What
remains:

1. **Review state storage and queue ownership.** Ruling C is accepted; the review queue needs an
   owner, a store and its own schema approval, and `MailAction.Review` touches Core, Host contracts,
   desktop and the queue worker. Proposed default deadline is 24 hours, configurable per pack.
2. **Feedback provenance columns.** Deferred to Phase 4, but the decision to add them is needed
   before a weak model label can be captured with its model and version.
3. **Profiles merge.** The read contract is stable in shape; the library is not merged yet. Policy
   adds the ProjectReference after the SHA lands. No key is passed before then, and `audience` and
   `contact_policy` are not passed at all in v1.
4. **Pack authoring UI.** Read and lifecycle only in Phase 1; the consequence panel is rendered from
   typed inputs, and the settings page copy is not authored here.

## Not in this design

- No live action is executed or enabled. The first slice remains pure.
- No queue, proxy, transport or credential change.
- No model call in the decision path. The evaluator is deterministic; labels are feedback, not
  authority.
- No new NuGet dependency.
- No pack authoring UI in Phase 1 beyond read and lifecycle, and no promotion UI before Phase 4.
