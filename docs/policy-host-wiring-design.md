# Policy host wiring: packs, bindings and the evaluator call site

Decision record for the slice bounded by `overview-` on 18 Oct 2026, after the first policy slice
was accepted at merged main `264b177`.

Status: **design only, not built.** No table was created, no route was mapped, no evaluator call was
added to the live path. The first slice (`5093d77`) is a pure library and is not wired to anything.

## The decision

**Wire the policy surface in four bounded phases, and keep the live path on the legacy engine until
a pack exists, a binding path exists and the review state can be represented honestly.**

1. **Stores and lifecycle, read-only API.** Pack, rule-set and binding stores with the draft,
   approved, effective, superseded lifecycle. The evaluator is not called. Nothing an operator can
   do here changes how a message is decided.
2. **Shadow evaluation.** The Host calls the evaluator beside the existing engine for tenants with
   an active pack, records the candidate verdict, and leaves the delivered action untouched.
3. **Authoritative evaluation.** Only after Phase 1 is green and the review mapping below is ruled:
   the pack verdict composes with the legacy tiers, and the legacy engine remains the rollback.
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
| Host DDL, routes, contracts, DI | `ingress-` per the architecture | This design proposes the tables and routes; `ingress-` owns their Host shape. |
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
- **No backdating.** `effective_from` must be at or after `approved_at`. A pack is not retroactive
  evidence; a decision already made is explained by the version that was effective when it was made.
- **Approval is by authenticated principals.** The request carries a principal id, not a free-text
  name. A lowering or a capture widening needs a second principal with `Administer`, and the two
  must be distinct. The pack payload keeps human-readable author and approver fields for
  portability, and the store keeps the principal ids that authorised it. This is how spec 9.15's
  "recorded operator decision" becomes checkable rather than declared.
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

Jurisdiction selection is a security decision, not a convenience:

- The effective country comes from `profiles-`' property read and must be `verified` or `declared`.
  An inferred or imported country is ineligible (`profiles-`' contract already enforces this).
- If the effective country is known and differs from the pack's jurisdiction, an effective pack for
  that jurisdiction is required. If none exists, the decision is `Review` with
  `policy.no_pack_for_jurisdiction`. It is never silently decided by the wrong legal constraints.
- If the country is unknown, the tenant's declared default jurisdiction governs. This is the same
  conservative default spec 9.15 gives an undeclared audience.

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
evidence refs, confirmation and candidates. Policy adds a ProjectReference **after** that library
is merged, and reads only. It never calls the write or confirm routes.

The keys policy consumes:

| Key | Use | Rule |
|---|---|---|
| `age_band` | audience protection | Most protective band governs. A minor raises `adult_content` to at least `Strict`, `coercion_and_extortion` to at least `Strict`, `harassment` to at least `Standard`. An inferred adult never lowers anything. |
| `country` | jurisdiction selection | Verified or declared only. Known and different from the pack's jurisdiction requires a pack for that jurisdiction; otherwise `Review`. |
| `audience` | context | A mixed or unknown audience keeps grooming at `Strict` or above. |
| `role`, `language` | evidence context | Passed to rules as facts. They never lower a protection. |
| `contact_policy` | binding scope context | Read for the console's effective-binding display. Not a tolerance input. |

An age band of `unknown` does not lower a level and does not pick a default. It adds the affected
categories to `EffectivePolicy.ForcedReviewCategories`, so any rule that fires in one of them
escalates to `Review`. This is the "uncertain band may raise sensitivity and escalate" rule
expressed as an outcome rather than a guessed level.

**The dependency direction is Policy to Profiles, never the reverse.** Profiles must not reference
Policy. The shared vocabulary (`age_band` ids, scope chain) stays in Profiles; Policy imports it.

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

### The review mapping gap

`PolicyVerdict.Review` has no honest representation in today's `MailAction` enum, and this design
does not pretend otherwise. The options:

| Option | Change | Consequence |
|---|---|---|
| A. `Review` maps to `Hold` with today's 5-30s window | none | Dishonest: 30 seconds is a re-check, not human review (`PolicyOptions.MaxHoldDeadline`). |
| B. `Review` maps to `Quarantine` | none | Removes the message from the inbox, which is stronger than review and reversible, but conflates "a human should look" with "this is contained". |
| C. Add `MailAction.Review` and a review-queue state | Core enum, Host contracts, desktop, queue | Honest: the message is retained for a human, with a stated deadline and a reversal path. Touches four lanes. |
| D. Keep the action as `Allow` and record a review item after delivery | Core/Host | Unacceptable for inbound pre-acceptance: the review arrives after the harm. |

**Recommendation: C, before Phase 3.** Until C is ruled, Phase 2 can shadow the verdict without
mapping it to an action, and Phase 3 stays blocked. The alternative, B, is acceptable only if the
product rules that a gray-zone message is contained while it waits; that is a product decision, not
a policy one.

## The decision record

`MailAssessment` is in `StyloMail.Core` and cannot reference `StyloMail.Policy`. To carry the
explanation in the ledger payload, the shared verdict vocabulary moves to Core before wiring:

- `PolicyVerdict` moves from `StyloMail.Policy` to `StyloMail.Core` (or a `StyloMail.Core.Policy`
  namespace). This is safe now because the enum has no consumer outside the first slice. After
  wiring it becomes a breaking change.
- `MailAssessment` gains an optional `PolicyDecisionRecord`:

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
  with no active pack, has no policy record. The console must say "no pack was active" rather than
  rendering a clean verdict.
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

```csharp
public enum PolicyActionKind
{
    Hold, Quarantine, Review, ReleaseQuarantine,
    PauseSender, ResumeSender, BlockSender, CaptureText,
}

public enum ConsequenceAvailability
{
    Measured,        // a number from a stated window
    NotMeasured,     // the window exists but the query failed or was not run
    NotApplicable,   // the question does not apply to this action or this tenant
}

public sealed record PolicyActionCapability
{
    public required PolicyActionKind Kind { get; init; }
    public required int AuthorityTier { get; init; }              // 0..4; PII has its own tier
    public required bool Automatic { get; init; }
    public required bool RequiresHumanApproval { get; init; }
    public required string RequiredPrivilege { get; init; }       // "Review" | "Administer"
    public required IReadOnlyList<BindingScope> Scopes { get; init; }
    public required bool Reversible { get; init; }
    public PolicyActionKind? ReversalAction { get; init; }
    public TimeSpan? MaximumDuration { get; init; }
    public required bool ReasonRequired { get; init; }
    public required string AuditEventKind { get; init; }
    public required string ConsequenceTemplateId { get; init; }
    public required int ConsequenceTemplateVersion { get; init; }
    public required IReadOnlyList<ConsequenceInputDescriptor> Inputs { get; init; }
}

public sealed record ConsequenceInputDescriptor
{
    public required string Name { get; init; }                    // e.g. "would_have_been_held"
    public required string Unit { get; init; }                    // "messages" | "days" | "level"
    public required ConsequenceAvailability DefaultAvailability { get; init; }
    public required string Source { get; init; }                  // "decision_ledger" | "pack" | "binding"
}

public sealed record ConsequenceInput
{
    public required string Name { get; init; }
    public string? Value { get; init; }                           // null exactly when not measurable
    public required ConsequenceAvailability Availability { get; init; }
    public int? SampleSupport { get; init; }
    public double? Coverage { get; init; }
    public required string Window { get; init; }                  // e.g. "last 30 days"
    public DateTimeOffset? MeasuredAt { get; init; }
}

public sealed record PolicyConsequence
{
    public required string TemplateId { get; init; }
    public required int TemplateVersion { get; init; }
    public required IReadOnlyList<ConsequenceInput> Inputs { get; init; }
    public required string PackId { get; init; }
    public required string PackVersion { get; init; }
    public string? RuleSetVersion { get; init; }
    public string? EvidenceSnapshotDigest { get; init; }
}
```

### Inputs the console needs

Every input carries its own availability. The rule is the one the evidence layer already holds: a
missing count is `not measured`, never `0`.

- `effective_level`: the resolved tolerance for the category, and `hard_floor` with a reason.
- `would_have_been_held_last_window`, `confirmed_last_window`, `overridden_last_window`,
  `reversed_last_window`: counts over a stated window, with sample support and coverage, from the
  decision ledger. Spec 9.15's operator surface ("12 messages would have been held last month, 3
  were confirmed") is exactly this row.
- `binding_scope`, `binding_expires_at`, `binding_review_by`, `binding_reversible`: from the active
  binding, or `not_applicable` when the action is not bound.
- `capture_retention_days`, `capture_max_characters`, `capture_statement`: from the pack and the
  capture binding. `not_applicable` when capture is not the action.
- `reversal_action`, `reversal_deadline`, `expiry`: from the static descriptor.
- `pack_version`, `rule_set_version`, `evidence_digest`: provenance, so the copy can link to the
  explanation.

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
| `/v1/policy/capabilities` | GET | Review | descriptor, after desktop agrees |
| `/v1/decisions/{id}` | GET | Review | adds `PolicyDecisionRecord` in Phase 2 |

The split mirrors `ApiRoutes.cs`: reads on `Review`, mutations on `Administer`. Approval and
activation are mutations, and the second approver must be a distinct authenticated principal with
`Administer`, checked from the request identity, not from a body string.

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
   A hard floor below `Strict` is refused regardless of approvers.
6. **Profiles unavailable or a property unknown.** The conservative default applies. No inferred
   adult lowers a protection, and a known country with no matching pack produces `Review`.
7. **Pack store outage.** The effective pack is immutable and cached in memory after first read. A
   cache miss during an outage produces `Review` with `policy.pack_store_unavailable`, never a
   permission fallback. The legacy tiers still run; this is a decision to be ratified, because it
   trades availability for the safety property that an unreadable pack is not an absent one.
8. **Evaluator throws.** The composition falls back to the legacy decision and records
   `policy.evaluator_failed`. An exception never produces an `Allow` that the pack would not have.
9. **Binding expiry.** Resolved at read. `HoldForReview` holds the scope; `RevertToPack` returns to
   the pack. No binding becomes permanent by being forgotten.
10. **Shadow mode.** Records only. The delivered action is byte-identical to the production action,
    and the shadow record's `Applied` is always false.
11. **A review verdict has no honest action.** Phase 3 does not ship until the mapping is ruled.

## Phases and their tests

**Phase 1: stores, lifecycle, read-only routes.**
- Host migration and store tests: draft edit revalidates, approved immutable, hash mismatch refused,
  one effective per jurisdiction, supersession in one transaction, withdrawal before effect only,
  cross-tenant isolation, principal-based second approval, auth split.
- No assessment path changes. The Policy suite and the Host suite stay green.

**Phase 2: shadow evaluation.**
- `PolicyDecisionRecord` in Core, mirrored on `DecisionResponse`, with `Shadow = true`.
- Assessment test: a tenant with an active pack records a candidate verdict; the action and the
  queue behaviour do not change; the evidence digest matches a re-evaluation of the same rows.
- A tenant with no pack records null and is otherwise unchanged.
- Desktop reads the record in a read-only pane section.

**Phase 3: authoritative composition.**
- Depends on the review mapping (C, recommended) and on `StyloMail.Profiles` being merged.
- Composition tests: a pack cannot relax legacy tier 1-3; the more protective outcome wins; a
  binding raises or lowers within its scope; a hard floor cannot be lowered; a jurisdiction
  mismatch reviews; a missing property reviews rather than defaults.
- Rollback: deactivating the pack returns the tenant to the legacy engine at the next assessment.

**Phase 4: feedback writes.**
- Promotion and retirement transitions with the full gate; label provenance columns; a refused
  promotion writes no rule-set state.
- Read-only until the decision record is stored and the join is proven.

## Open questions for `overview-`

1. **Review mapping**: approve option C (add `MailAction.Review` and a bounded review state) before
   Phase 3, or rule that B (quarantine while waiting) is the product behaviour. This is the only
   blocker to authoritative wiring.
2. **Schema approval**: the six tables above (pack and its events, rule set and its events, binding and its events). No migration has been written.
3. **Move `PolicyVerdict` to Core**: needed for `MailAssessment.PolicyDecisionRecord`. Safe now,
   breaking after a consumer exists.
4. **Store-outage behaviour**: cache the last effective pack and review on a miss, or defer. I
   recommend review on a miss, not defer, because a deferral returns the mail to the sender and
   looks like a policy decision.
5. **Pack selection**: one effective pack per tenant and jurisdiction, with properties only raising
   protection. A tenant that needs a different vertical per entity is out of scope for v1.
6. **Feedback provenance columns**: defer to Phase 4, but the decision to add them is needed before
   a weak model label can be captured with its model and version.

## Not in this design

- No live action is executed or enabled. The first slice remains pure.
- No queue, proxy, transport or credential change.
- No model call in the decision path. The evaluator is deterministic; labels are feedback, not
  authority.
- No new NuGet dependency.
- No pack authoring UI in Phase 1 beyond read and lifecycle, and no promotion UI before Phase 4.
