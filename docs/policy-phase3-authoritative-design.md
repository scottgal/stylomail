# Phase 3: authoritative pack composition

Status: **design only, no code.** Requested by `overview-` on 18 Oct 2026. The review state is merged
at `b885420`; the pack evaluator currently runs only in shadow. Nothing here is implemented, and no
flag is on.

## 1. The feature flag

**Default OFF, per tenant, and never global.**

```yaml
StyloMail:
  Policy:
    Authoritative:
      Tenants: []          # explicit tenant ids; empty is off for everyone
      Jurisdiction: ""     # the deployment's declared jurisdiction
```

Rules:

- The flag is consulted per assessment from the authenticated tenant. There is no process-wide
  on-switch.
- A tenant is authoritative only when **all** of these hold: it is listed, an approved effective pack
  exists for the deployment jurisdiction, its referenced rule set is live, and the Profiles read
  contract is available.
- **Flag off is the legacy decision byte-for-byte.** With both the authoritative and shadow flags
  off, the assessment is identical to a build without pack evaluation: same action, reasons, risk
  arithmetic and queue admission. The test serialises the assessment and compares bytes.
- The shadow flag is separate and already merged. If shadow is on while authoritative is off, the
  only difference is the nullable `Policy` record with `Shadow = true`; the action and reasons are
  unchanged. That distinction is tested explicitly, because "byte-for-byte" is true of the
  decision, not of an assessment that has opted into shadow.
- A listed tenant with no effective pack falls back to the legacy decision and records no
  authoritative `Policy`. A listed tenant whose pack or rule set cannot be read fails closed
  (section 5), not open.

## 2. Composition

The legacy tiers run first and unchanged: resource controls, verified security rules,
suspected-compromise posture, then risk. The pack verdict is computed over the same evidence, then
composed by the table already implemented in `PackPolicyComposer`:

| Legacy outcome | Pack Permit/Observe | Pack Hold | Pack Quarantine | Pack Review |
|---|---|---|---|---|
| Allow | legacy Allow | Hold | Quarantine | Review |
| Hold | legacy Hold | Hold | Quarantine | Review |
| Quarantine | Quarantine | Quarantine | Quarantine | Quarantine |
| Defer | Defer | Defer | Defer | Defer |
| Reject | Reject | Reject | Reject | Reject |

- **The more protective outcome wins.** A pack never relaxes a legacy Hold, Quarantine, Defer or
  Reject, and a pack Permit cannot turn any of them into an Allow.
- **Recipient preference remains last.** It cannot relax a pack Review, Hold or Quarantine. The
  existing tier-5 preference logic runs before composition, so its Allow cannot survive a pack Hold.
- **The ceiling caps fired bands, never escalations.** The evaluator already refuses to cap missing
  evidence, conflicting evidence, insufficient coverage/sample and zero tolerance. The ceiling is
  read from the resolved effective policy, not from the raw pack, so a binding cannot raise it.
- **The evaluator evaluates over the effective policy, not the raw pack.** `PolicyEvaluationInput`
  gains an `EffectivePolicy` carrying the resolved levels, ceiling, binding ids, property facts and
  `ForcedReviewCategories`. A fired candidate in a forced category escalates to Review. The raw
  `PolicyPack` stays on the input and in the record as provenance; it is not the source of the
  thresholds.
- `Observe` changes no action. It adds an Observe record and a reason; it never maps to a hold.

## 3. Action mapping

| Pack verdict | Action | Path |
|---|---|---|
| Permit | legacy action | none; record only if the authoritative path ran |
| Observe | legacy action | none; record carries the Observe verdict |
| Hold | `MailAction.Hold` | existing queue `Held` with the pack's hold window |
| Quarantine | `MailAction.Quarantine` | existing queue `Quarantined` |
| Review | `MailAction.Review` | queue Review with the opening chain and `ReviewDeadline` |

- `ReviewDeadline` is resolved from the effective pack (`ReviewDeadline`, default 24h) and copied to
  `PolicyDecision.ReviewDeadline` at composition, so the queue row stores the resolved value.
- **MonitorAllMail is not a pack verdict.** It is an operator capability, observe-only, and a pack
  that wants monitoring uses `Observe` (no side effect) or the tenant's monitoring setting. No
  pack verdict selects MonitorAllMail in v1.
- **Block is not executable in v1.** The composer and the evaluator cannot produce it; a future
  rule naming it would be refused by the rule-set validator before activation.
- **A verdict with no executable mapping fails closed.** If the Review state is unavailable, the
  mapped Review becomes Quarantine with `policy.review_state_unavailable`; if any other mapping is
  missing or the composed action is not a known `MailAction`, the result is Quarantine. It is never
  Allow and never Defer.

## 4. Profiles: effective facts, not raw guesses

`StyloMail.Policy` gains a ProjectReference to `StyloMail.Profiles`. The direction stays Policy to
Profiles; Profiles never references Policy.

For each assessment the composition root builds the scope chain and reads exactly the v1 keys:

- `age_band`, `country`, `role`, `language` via
  `PropertyScopeChain.Create(addressed, ancestors...)` and
  `IPropertyStore.GetEffectiveAsync(tenantId, chain, keys, now, ct)`.
- Active bindings come through a new read-only `IPolicyBindingReader` in `StyloMail.Policy`,
  implemented by the Host over the landed binding store. The resolver reads bindings at the decision
  instant and records their ids; it never writes one.
- A conversation assessment addresses the conversation; a mail assessment addresses the sender
  entity, with known domain/company ancestors. The recipient's preferences use the recipient chain
  for role/language. The chain used and its governing scope are recorded per fact.
- `audience` and `contact_policy` are not requested: they are not v1 keys and an unknown key throws.

The resolver produces an `EffectivePolicy`:

```
levels   = max(pack.CategoryLevels[category], propertyProtectiveFloor(category, facts))
levels   = binding.RaiseTolerance or LowerTolerance, most specific active binding
ceiling  = pack.ActionCeiling                       // bindings cannot raise it
forced   = property-driven review categories         // unknown age, country difference
assert hard floors >= Strict
```

- A **Suggested** value may raise and never lower: a minor or unknown age band raises adult content
  and coercion to at least Strict, harassment to at least Standard, and an unknown band adds those
  categories to `ForcedReviewCategories`. An inferred adult never lowers a declared minor.
- Country is verified/declared only. A known country different from the deployment jurisdiction adds
  the jurisdiction-sensitive categories to `ForcedReviewCategories`; it never selects a different
  pack in v1.
- Binding ids, each fact's key/value/state/source/scope, and the resolved levels go into the
  decision record. The facts are the replay input: a later reader can recompute the levels without
  reading the live profile, and a suggestion that has since been confirmed does not rewrite the
  decision.
- A Profiles read failure is **not** a permissive default. Unknown facts force review for the
  categories that depend on them, and the record names `policy.properties_unavailable`. It never
  silently lowers a protection.

## 5. Store outage and cache

- The last successful effective pack per (tenant, jurisdiction) is cached in memory as an immutable
  value. It is never mutated in place; a read replaces the reference atomically.
- Pack store failure with a cached pack: compose with the cached pack and record
  `policy.pack_cache_used` with the pack version and the cache age. Flag off remains off.
- Pack store failure with no cache: `Review` with `policy.pack_store_unavailable`. Never Allow and
  never Defer.
- Rule-set read failure: `Review` with `policy.rule_set_unavailable` (or the cached immutable rule
  set for that exact version, with `policy.rule_cache_used` when it is used). A pack is never
  evaluated against a different rule version.
- Profiles read failure: unknown facts and forced review, as in section 4.
- Review state unavailable: `Quarantine` with `policy.review_state_unavailable`.
- The cache is tenant-scoped, so a tenant cannot read another's pack through a cache key.

## 6. The decision record

`PolicyDecisionRecord` gains:

```csharp
public bool Applied { get; init; }            // true only when the pack changed the action
public MailAction? LegacyAction { get; init; } // the action the legacy tiers chose
public MailAction? ComposedAction { get; init; } // the action after composition
public bool Authoritative { get; init; }      // false for a shadow record
```

- `Applied = composedAction != legacyAction`. A pack verdict equal to the legacy action is still
  recorded, with `Applied = false`, because "the pack agreed" is a fact worth keeping.
- `Shadow = false` and `Authoritative = true` on this path. The existing shadow record keeps
  `Shadow = true`, `Applied = false`.
- The record carries pack id/version, rule-set id/version, scorer version, deciding rule
  id/version/threshold, reason code and text, missing dimensions, coverage, evidence digest,
  binding ids and property facts. The console can answer "why did the action change" from the record
  and the ledger without reading live policy.
- A flag-off assessment has no authoritative record. A shadow-only assessment keeps its shadow
  record. Null still means no pack was active or the path was not entered, and is never Permit.
- The record travels in the existing assessment JSON; no new table.

## 7. Tests

- **Flag off parity**: a tenant with no flag and both pack paths off produces an assessment
  byte-identical to a build without pack evaluation; a tenant with a pack but flag off has the same
  action and reasons.
- **Composition matrix**: every cell of the table, at the assessment level, including a pack Permit
  against a legacy Hold/Quarantine/Defer/Reject.
- **No relaxation**: each legacy protective outcome against every pack verdict.
- **Review mapping**: pack Review produces `MailAction.Review`, a queue Review row with the opening
  chain and the resolved deadline; a pack Hold/Quarantine maps to the existing paths; a pack
  Observe changes no action.
- **Outage fallback**: pack-store failure with no cache → Review; with a cache → cached pack and a
  `policy.pack_cache_used` reason; rule-set failure → Review; Review state unavailable → Quarantine;
  Profiles failure → forced review, never lowered.
- **Property-fact replay**: a record replays to the same levels and verdict from its facts alone; a
  Suggested minor raises; an inferred adult does not lower a declared minor; a later confirmation
  does not rewrite the record.
- **Tenant isolation**: a tenant not in the flag list is legacy even when another tenant is
  authoritative; a cached pack never crosses tenants.
- **Determinism**: same evidence, facts, bindings and versions produce the same composed action and
  record digest.

## Owner split

| Piece | Owner |
|---|---|
| Feature flag, composition call, queue admission and deadline | `policy-` in Assessment composition, with `overview-` ruling |
| `EffectivePolicy` resolver, evaluator input, `PackPolicyComposer`, record fields and tests | `policy-` |
| `PolicyDecisionRecord` additions (`Applied`, `LegacyAction`, `ComposedAction`, `Authoritative`) | `policy-` with `overview-` approval (Core contract) |
| Profiles ProjectReference and effective-property adapter | `policy-` (reads only; no writes) |
| Host config, pack/rule cache, `IPolicyBindingReader` implementation, status route, contracts | Host owner (`ingress-` if it returns; otherwise assigned) |
| Pack/binding lifecycle and review surface | already landed; Policy/Host owners as above |
| Console pack/binding/review panels and the "action changed" explanation | `desktop-` |

## What desktop needs

- Existing: pack list/detail, binding list/create/revoke, review list/detail/resolution, capability
  descriptors.
- New for authoritative mode: `PolicyDecisionRecord.Applied`, `LegacyAction`, `ComposedAction`,
  `Authoritative`; the resolved effective levels; each property fact with its state/source/scope;
  the binding ids that shaped the levels; and a status read (`GET /v1/policy/status`) saying whether
  authoritative mode is on for the tenant, the effective pack, whether shadow is on, and whether the
  review state is available.
- Copy: "The pack changed the action from X to Y" when `Applied` is true; "The pack agreed with the
  legacy action" when false; "Shadow only; no action was applied" when `Shadow` is true; "No policy
  pack was active" when the record is null.
- The capability strip must offer no Block and must show Review as WithheldForReview, not a
  containment state.

## Open questions for `overview-`

1. Approve the Core record additions (`Applied`, `LegacyAction`, `ComposedAction`, `Authoritative`).
2. Approve the config shape and per-tenant list; confirm the deployment jurisdiction lives in
   `StyloMail:Policy:Authoritative:Jurisdiction`.
3. Confirm `MonitorAllMail` stays an operator capability and is not a pack verdict in v1.
4. Confirm the Profiles ProjectReference direction and that Policy may reference Profiles now that
   the library is merged.
5. Who owns the Host cache/status route while `ingress-` is exited?
