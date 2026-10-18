# Phase 3: authoritative pack composition

Status: **design only, no code, no flag on.** Revised on 18 Oct 2026 after the independent
reliability review and `overview-`'s rulings on all eight gaps. This revision is the artifact for
document review; implementation stays blocked.

## 1. Enablement and typed resolution

Authoritative mode is decided **only from the authenticated tenant and configuration**. Dependency
health does not decide whether a tenant is enabled; it decides what that enabled tenant gets.

```yaml
StyloMail:
  Policy:
    Authoritative:
      Tenants:
        acme: GB           # tenant id -> required jurisdiction; presence = enabled
        globex: US
      FallbackReviewDeadline: 24:00:00   # optional, 1h..7d, default 24h
```

- Absence of the tenant key, or an empty map, means authoritative mode is **off** for that tenant.
  **No pack work happens only when both pack paths are off.** The dependency gate is a matrix:
  - Authoritative off and shadow off: no pack, rule-set, Profiles, cache, binding or status access.
  - Authoritative off and shadow on: the existing separate shadow evaluator still reads the pack and
    rule set and records its `Shadow` result. Authoritative-only dependencies (Profiles, bindings,
    effective resolver, status/cache writes) are not touched.
  - Authoritative on for the tenant: the authoritative path runs. The separate shadow evaluator is
    not run for that assessment; one pack evaluation produces one record. If both flags are on,
    the authoritative record supersedes the shadow record for that tenant rather than emitting two.
  - `AssessmentContext.ShadowMode` is a third, independent switch and wins over both (section 2).
  A flag-off tenant with both paths off never calls a throwing dependency.
- A present tenant with a blank jurisdiction is a startup fault, not silent legacy.
- The resolution is typed, not nullable:

```csharp
enum PolicyResolutionKind { Absent, Pack, Unavailable }

record PolicyResolution
{
    PolicyResolutionKind Kind;
    PolicyPack? Pack;
    PolicyRuleSet? RuleSet;
    PolicyResolutionSource Source;     // Store, Cache
    string? UnavailableReason;
}
```

- **Absent** is a healthy "no effective pack": the legacy decision stands.
- **Pack** is a pack plus a live rule-set version (or a coherent cached pair within the staleness
  bound).
- **Unavailable** is an exception, timeout, malformed result, missing required service, or a stale
  cache: it is never Absent and never falls back to legacy. It enters the fallback path in section 6.
- Profiles availability is not an enablement condition. A missing property is a named absence for
  the rules that require it, not a global outage.

## 2. Ordering, composition and the global shadow mode

The order is fixed:

1. Legacy tiers 1–3 and the legacy risk decision, unchanged.
2. If `AssessmentContext.ShadowMode` is true, the existing contract wins: `ProposedActionInShadow`
   is the legacy action and the delivered action is Allow. The pack may still be evaluated for the
   record, but its action is never applied. The record has `Applied = false`, `Shadow = true`,
   `Authoritative = true` (the flag was on and the pack ran), `PolicySource = GlobalShadow`, and
   reason `policy.global_shadow_precedence`. This combination is explicitly unsupported for action,
   not silently relaxed in either direction.
3. Otherwise resolve the policy: Absent → legacy; Unavailable → the fallback candidate in section 6;
   Pack → evaluate the pack.
4. Compose the pack candidate with the legacy decision through `PackPolicyComposer`:
   `Quarantine > Review > Hold > Allow`. Legacy Defer and Reject are never replaced. A pack Permit
   cannot turn a legacy protective outcome into Allow; recipient preference remains last and cannot
   relax a pack Review, Hold or Quarantine.
5. Admission. If the queue refuses admission (missing payload, spool failure, quota), the final
   action is Defer because responsibility never transferred. The record has `Applied = false`,
   `AdmissionRefused = true`; a pack cannot claim Review/Quarantine durability that admission did
   not take. Only policy evaluation is "never Defer"; Step 7 can still Defer.

The ceiling caps a fired band and never an escalation. Missing evidence, conflicting evidence,
insufficient coverage/sample, zero tolerance and a missing required property key are escalations
and are not capped.

## 3. Action mapping

| Pack verdict | Action | Path |
|---|---|---|
| Permit | legacy action | none |
| Observe | legacy action | none; record only |
| Hold | `MailAction.Hold` | queue Held with the resolved pack hold deadline |
| Quarantine | `MailAction.Quarantine` | existing quarantine path |
| Review | `MailAction.Review` | queue Review with the opening chain and resolved deadline |

- `ReviewDeadline` is the pack field (default 24h), resolved to an absolute instant at the decision
  and copied into `PolicyDecision.ReviewDeadline`, the queue row, the opening record and the readback.
- `HoldWindow` is a new pack field, bounded 1h–7d, default 24h. A pack Hold sets
  `ReEvaluateBy = now + HoldWindow`. If the legacy decision is already a Hold, the earlier of the
  legacy and pack deadlines governs; a pack Hold never extends a legacy hold.
- **MonitorAllMail is not a pack verdict.** It remains an operator capability, observe-only.
- **Block is not executable in v1.** The evaluator and composer cannot produce it.
- **Review unsupported is decided before admission.** If the durable Review capability or schema is
  unavailable, do not attempt a Review admission: compose Quarantine instead with
  `policy.review_state_unavailable`. That is a capability decision, not a failed write.
- **A Review admission that is actually refused is a Defer, not a phantom Quarantine.** If Review was
  selected and `AcceptAsync` fails on spool, storage or another acceptance condition, the final
  action is Defer with `policy.review_admission_refused`, `AdmissionOutcome = Refused`,
  `Durable = false`, and no durable review or quarantine claimed. The queue's acceptance contract
  guarantees no queue id on refusal; no retry-as-Quarantine is attempted for a typed admission
  failure.
- Any other unmappable composed action becomes Quarantine. It is never Allow in the policy layer.

## 4. Profiles: conditional uncertainty, not a global outage

`StyloMail.Policy` references `StyloMail.Profiles` (Policy → Profiles only). The v1 keys are exactly
`age_band`, `country`, `role`, `language`, read through
`PropertyScopeChain.Create(addressed, ancestors...)` and
`IPropertyStore.GetEffectiveAsync(tenantId, chain, keys, now, ct)`. Active and expired
`HoldForReview` bindings come through a read-only `IPolicyBindingReader` implemented by the Host.

- `PolicyRule.RequiredPropertyKeys` names the property keys a rule needs. It is validated against
  the fixed v1 catalog. The check runs **before band firing**: a rule whose required key is missing
  returns Review with `policy.property_facts_unavailable`, even when its measured score is below
  every band. Missing facts are never low risk.
- Rules that do not require the missing key evaluate normally. In a rule set containing both, the
  aggregate is the maximum outcome, so a dependent rule's Review is not discarded by another rule's
  Permit/NotFired. A separate assessment with no dependent rule is the positive control that
  ordinary processing continues.
- **Healthy Unknown is not missing.** `State=Unknown`, `Value=null` with a named availability is a
  measured answer. A healthy `age_band=unknown` adds the affected categories to
  `ForcedReviewCategories`; it does not fail `RequiredPropertyKeys`. A read failure is missing and
  fails only the rules that require the key.
- **Only-ineligible country is a named answer, not a read failure.** `State=Unknown` with
  `only_ineligible_candidates` records `country_eligible=false`; a rule requiring `country`
  escalates, other rules compose.
- A read failure records `PropertyFactsUnavailable=true` and the missing keys on the decision. There
  is **no global Profiles-outage Review candidate**.
- A Suggested value may raise and never lower: minor or unknown age raises adult content and
  coercion to at least Strict and harassment to at least Standard. An inferred adult never lowers a
  declared minor. A later confirmation does not rewrite the stored decision.

## 5. Bindings and precedence

Bindings are resolved per category, deterministically:

1. Start with the pack's category level.
2. Apply the category hard floor (never below Strict).
3. Apply property-driven protective floors (minor/unknown age, country rules).
4. Apply binding targets in this order: active bindings, plus expired bindings whose
   `OnExpiry = HoldForReview`. For each category, choose by **more protective target, then narrower
   scope, then latest effective, then binding id ordinal**.
5. **Reapply every property and hard floor after the binding target.** A less protective binding can
   never erase a minor or unknown floor.

Support in Phase 3:

| Binding action | Support |
|---|---|
| RaiseTolerance | Applied to the level, then floors reapplied |
| LowerTolerance | Applied, then floors reapplied; cannot undercut hard/property floors |
| CaptureText | Grants capture within the pack's declared mode; never a policy verdict or level |
| Permit | Recorded but **not applied** to an action or level in Phase 3; would relax a protection and needs its own ruling |
| Observe | Recorded but **not applied** to an action or level in Phase 3; no silent relaxation |

- An expired `HoldForReview` binding keeps the scope withheld for human review. It does not get
  dropped just because `IsActiveAt(now)` is false. `RevertToPack` stops applying at expiry.
- Window semantics are `[StartsAt, ExpiresAt)`: just before expiry it applies; exactly at expiry it
  is expired and the `OnExpiry` rule applies.
- A binding for another tenant or a pack version different from the resolved pack is refused.
- Binding resolution snapshots (id, action, category, target, scope, starts, expires, on-expiry,
  selected/rejected) are recorded in the effective-input snapshot.

## 6. Deadlines, fallback and admission

- `PolicyPack.ReviewDeadline` and `PolicyPack.HoldWindow` are both bounded 1h–7d with verified
  defaults; `ReviewDeadline` is 24h and `HoldWindow` is 24h per ruling.
- Absolute deadlines are resolved once at the decision instant (`now + span`) and copied unchanged
  into the decision record, opening record, queue row and readback. Nothing recomputes them later.
- **No-cache Unavailable fallback**: use the configured `FallbackReviewDeadline` (default 24h,
  bounded 1h–7d), compose it as a Review candidate, and record:
  `Applied = true`, `Authoritative = false`, `Shadow = false`, `PolicySource = Fallback`,
  `PackUnavailable = true`, `FallbackReason`, `OpeningSource = fallback`, and **null pack id,
  version, rule-set id/version, rule id/version and evidence digest**. Do not invent values. The
  queue opening record and event carry the same null provenance and a fallback reason.
- A missing rule set for a configured pack is Unavailable, not Absent: fallback Review (bounded),
  never legacy.
- If the Review state is unavailable, the fallback candidate becomes Quarantine with
  `policy.review_state_unavailable`.
- `Applied` means the final action changed from the legacy action and admission succeeded.
  `Applied = true, Authoritative = false, Shadow = false` is the fallback record, and the console
  must not render it as shadow or no-policy.
- Admission refusal: if the queue refuses, the final action is Defer and `Applied = false`,
  `AdmissionRefused = true`; policy evaluation never claims durability it did not get.

## 7. Cache

The cache owns an immutable **pack + live rule-set version pair**, keyed by
`(tenant, jurisdiction, ruleSetId, ruleSetVersion)`, never a sender-specific `EffectivePolicy`.

- Maximum staleness is **5 minutes**. A cache entry older than that is Unavailable, not Pack.
- A healthy Absent result clears the tenant's cache entry; a pack or binding update in this process
  invalidates the same tenant immediately. Another process sees the update within the 5-minute
  bound.
- Capacity is bounded (e.g. 256 entries) with least-recently-used eviction.
- Each entry carries a monotonic generation. A slow older read may not overwrite a newer result;
  install only if its generation is newer.
- The pack and rule-set versions are cached coherently: an entry never pairs a pack with a rule
  version it did not reference.
- The record names `PolicyResolutionSource.Cache` and the cache age; a stale cache never supplies a
  pack.

## 8. The decision record and replay

`PolicyDecisionRecord` gains and clarifies:

```csharp
enum PolicySource { Pack, Shadow, Fallback, GlobalShadow }
enum AdmissionOutcome { Admitted, Refused, NotApplicableForAssessmentOnly }

public PolicySource Source { get; init; }

public bool PolicyContributed { get; init; }        // composed action differs from legacy
public bool Applied { get; init; }                  // composed action actually applied
public bool Durable { get; init; }                  // admission succeeded
public AdmissionOutcome Admission { get; init; }    // Admitted | Refused | NotApplicableForAssessmentOnly
public bool Authoritative { get; init; }            // true only when the tenant flag was on and a pack ran
public bool Shadow { get; init; }
public bool FallbackAttempted { get; init; }
public bool PackUnavailable { get; init; }
public string? FallbackReason { get; init; }
public bool ReviewStateUnavailable { get; init; }
public bool ReviewAdmissionRefused { get; init; }
public bool PropertyFactsUnavailable { get; init; }
public IReadOnlyList<string> MissingPropertyKeys { get; init; }
public MailAction? LegacyAction { get; init; }
public MailAction? ComposedAction { get; init; }
public MailAction? FinalAction { get; init; }
public PolicyEffectiveInputSnapshot? EffectiveInput { get; init; }
public string? EffectiveInputDigest { get; init; }
```

`PolicyEffectiveInputSnapshot` is self-contained and canonical: resolved level per category (recorded
by its stable name, so Core does not reference Policy), ceiling, forced categories, property facts
(key, value, state, source, scope, availability/reason), the addressed chain, binding resolution
snapshots, and the resolver version. It has its own SHA-256 digest. `EvidenceSnapshotDigest` stays
evidence-only; the two are never conflated. Same evidence with different facts or bindings must not
produce the same effective-input digest.

For a fallback record, `PackId`, `PackVersion`, `RuleSetId`, `RuleSetVersion`, `ScorerVersion`,
`DecidingRuleId` and `DecidingRuleVersion` are null. Null means unavailable, never an empty value;
the existing record fields that are `required` strings become nullable in this slice.

The queue opening row gains `opening_source` and a tagged invariant:

```
opening_source = 'pack':
    pack_id, pack_version, rule_set_id, rule_set_version, reason, evidence_digest are NOT NULL
    unavailable_reason IS NULL
opening_source = 'fallback':
    pack_id, pack_version, rule_set_id, rule_set_version, rule_id, evidence_digest ARE NULL
    unavailable_reason IS NOT NULL, fallback deadline IS NOT NULL
```

No mixed row is legal: a CHECK (or the application-level equivalent with the same predicate)
enforces the source tagging and the pack/fallback null partition. The non-null `evidence_digest` rule
for a **new** pack opening is application-level: a pre-existing pack opening whose digest was
genuinely absent is migrated to `pack` with `legacy_digest_absent=true` rather than being
reclassified as fallback or backfilled with invented bytes. Existing rows that have an opening event
migrate to `opening_source = 'pack'`; rows with no opening remain `legacy_review_opening_missing` and
are never reclassified as fallback. A populated pre-migration fixture proves the backfill and the
CHECK, and a failed event insertion rolls the opening and state back atomically.

- `PolicyContributed = composedAction != legacyAction`. A fallback candidate overridden by a legacy
  Reject/Defer/Quarantine has `FallbackAttempted = true` but `PolicyContributed = false`.
- `Applied = PolicyContributed && Durable`. It is computed after composition and admission, never
  from `Source`.
- `Durable = AdmissionOutcome == Admitted`. On a refused submission it is false and `Applied` is
  false even if the composed action was more protective.
- `AssessmentOnly`: `Admission = NotApplicableForAssessmentOnly`, `Durable = false`, `Applied =
  false`; the assessment's action is still the composed action for explanation, but nothing was
  applied to delivery.
- `Source = Pack`, `Authoritative = true`, `Shadow = false`, `FallbackAttempted = false` on the
  normal path.
- `Source = Shadow`, `Authoritative = false`, `Shadow = true`, `Applied = false` on the pack shadow
  path.
- `Source = GlobalShadow`, `Authoritative = true`, `Shadow = true`, `Applied = false`,
  `PolicyContributed` computed from the composed/legacy pair but never applied.
- `Source = Fallback`, `Authoritative = false`, `Shadow = false`, `FallbackAttempted = true`,
  null pack provenance as in section 6; `PolicyContributed` and `Applied` are computed, not
  hard-coded.
- Null `Policy` still means no pack was active **or the path was not entered**; it is never Permit.
  `Source` plus the flags is the discriminator.
- `Applied = false` means no policy intervention took effect. It does not mean "the pack agreed":
  a pack Permit beside a legacy Reject, a pack Review overridden by legacy Quarantine, an
  assessment-only run, or a refused admission all read false for different reasons. The record's
  `LegacyAction`, `ComposedAction`, `FinalAction` and `Admission` state which. Console copy preserves
  the recorded verdict and says which case it is.
- The record travels in the existing assessment JSON; no new table.

## 9. Legacy parity

Parity is against a **pinned legacy reference**, not two calls through the new path.

- The reference is a serialised assessment produced by the legacy build at a named commit, with
  controlled assessment id, clock, provider results, reservations and profile store contents.
- The comparison uses independent stores per arm, so the first run cannot change the second's
  input.
- Positive controls prove the test can detect a changed action, a changed reason, and a changed
  queue mapping; a parity test that cannot fail is not a test.
- Flag-off tenants query no Profiles, cache or status store and mutate no state. The test uses
  throwing dependency fakes to prove they are never reached.
- Shadow-only parity excludes exactly the `Policy` field and asserts every other member unchanged.

## 10. Tests

Enablement/absence:
- Full authoritative/shadow matrix with dependency-call counters: both off makes no calls; shadow
  only runs the shadow evaluator and changes only `Policy`; authoritative only runs the
  authoritative path; both on emits one authoritative record.
- Flag off with both paths off and throwing Profiles/cache/status fakes: legacy baseline, zero
  dependency calls.
- Flag on, healthy no pack: legacy decision.
- Flag on, pack/rule exception/timeout/malformed: fallback Review, never legacy; missing rule set is
  Unavailable, not Absent.

Ordering/shadow:
- Global `ShadowMode` + flag on: action unchanged, `Source=GlobalShadow`, `Applied=false`,
  `Authoritative=true`, `Shadow=true`, reason; pack verdict preserved.
- Every legacy protective outcome (Allow/Hold/Quarantine/Defer/Reject) against every pack verdict,
  including the fallback candidate.

Profiles:
- Dependent rule with fully measured below-band score still Reviews on a missing required key.
- Identical rule with no property requirement evaluates normally.
- Mixed rule set: dependent Review is not discarded by an independent Permit.
- No-dependent assessment continues ordinarily.
- Healthy Unknown age forces review via categories; read failure only affects required-key rules;
  only-ineligible country is distinguished from no country and from a read failure.

Deadlines/admission:
- Pack Hold sets the deadline from `HoldWindow`; legacy Hold deadline is never extended.
- No-cache fallback has a 24h Review deadline, null pack provenance, `OpeningSource=fallback`.
- Fallback against every legacy outcome: legacy Reject/Defer/Quarantine win and
  `PolicyContributed=false`; fallback Review against Allow contributes and applies only when
  admitted.
- Successful admission after a pack Review: `Applied=true`, `Durable=true`, `Admission=Admitted`.
- Refused admission ends in Defer with `Applied=false`, `Durable=false`, `Admission=Refused`; actual
  queue state shows zero admissions.
- AssessmentOnly: `Applied=false`, `Durable=false`, `Admission=NotApplicableForAssessmentOnly`.
- Review capability unsupported before admission composes Quarantine with
  `policy.review_state_unavailable`; a Review admission refused on spool storage ends in Defer with
  `policy.review_admission_refused`, never a claimed durable review or quarantine.
- Absolute deadlines are identical across record, opening, queue and readback.

Bindings:
- Minor + less protective binding: property floor survives.
- Two equal-specificity bindings in reversed enumeration order resolve identically.
- Tagged opening migration: populated pre-migration pack openings remain pack; old rows without an
  opening read `legacy_review_opening_missing`; a valid fallback opening has null pack/rule/digest and
  a named reason and deadline; a pack opening with null versions is refused; a failed event insert
  rolls back the opening and state atomically.
- Deadline just before/at expiry for `RevertToPack` and `HoldForReview`; expired HoldForReview still
  withholds.
- Revoked binding, wrong tenant, wrong pack version.

Replay/cache/parity:
- Replay after deleting live profile/binding state; input-order permutations; later confirmation.
- Evidence digest and effective-input digest are distinct; differing facts/bindings change the
  effective digest.
- Cache: 5-minute staleness, same-tenant invalidation, coherent pack/rule pair, capacity eviction,
  older slow read cannot overwrite a newer generation, no sender-specific cache.
- Pinned legacy reference parity with independent stores and positive controls.

## Owner split

| Piece | Owner |
|---|---|
| Enablement flag, composition call, admission and deadlines | `policy-` in Assessment composition, with `overview-` ruling |
| `EffectivePolicy`, resolver, evaluator input, composer, record/snapshot, tests | `policy-` |
| Core record and snapshot types | `policy-` with `overview-` approval |
| Profiles reference and property adapter | `policy-` (reads only) |
| Host config, pack/rule cache, status route, `IPolicyBindingReader` | **`policy-` for this slice** (ruling e), with `review-` verifying the Host code; hand back when `ingress-` returns |
| Review surface and queue | landed; **this revision extends the queue only with the tagged fallback opening** (`opening_source`, nullable fallback provenance) under `policy-` temporary ownership (overview ruling), with `review-` verification |
| Console panels and copy | `desktop-` |

## Desktop needs

- `PolicyDecisionRecord`: `Source`, `Applied`, `LegacyAction`, `ComposedAction`, `Authoritative`,
  `PackUnavailable`, `FallbackReason`, `AdmissionRefused`, `PropertyFactsUnavailable`,
  `MissingPropertyKeys`, `EffectiveInput` and `EffectiveInputDigest`.
- A status read: `GET /v1/policy/status` returns tenant, authoritative enabled, effective pack,
  shadow enabled, review state available, cache age.
- Copy: preserve the pack verdict; do not call `Applied=false` "agreement"; render `Source=Fallback`
  as "Policy was unavailable; the action was withheld for review", not shadow or no-policy; render
  `GlobalShadow` as "Global shadow mode prevented the pack from changing the action."
- Action strip: no Block; Review is WithheldForReview; MonitorAllMail is observe-only.

## Rulings incorporated

All eight rulings of 18 Oct 2026 are incorporated: typed enablement; ordering and global shadow
precedence; conditional Profiles uncertainty; bounded Hold/Review deadlines and admission refusal;
post-binding floors and expired HoldForReview; self-contained replay snapshot and truthful Applied
copy; 5-minute cache semantics with invalidation, bounds and generations; pinned legacy parity with
independent stores and positive controls.

Implementation remains blocked pending document review.
