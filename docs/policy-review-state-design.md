# MailAction.Review: the bounded review state

Status: **design only, no code.** Requested by `overview-` on 18 Oct 2026. Phase 3's authoritative
composition depends on this ruling; nothing here is implemented.

## 1. Semantics and composition

`MailAction.Review` is appended to the enum after `Reject` so existing values do not move:

```csharp
Allow = 0, Hold = 1, Quarantine = 2, Defer = 3, Reject = 4, Review = 5
```

**Review withholds delivery and asks a human to decide.** It is not a hold with a longer clock and
it is not quarantine under another name. A hold re-checks automatically; quarantine contains the
message; Review says the deterministic rule could not finish the decision and an authenticated
person must.

Composition with the legacy tiers is by protectiveness and a pack never relaxes the legacy result:

| Legacy outcome | Pack Permit/Observe | Pack Hold | Pack Quarantine | Pack Review |
|---|---|---|---|---|
| Allow | legacy Allow (Observe adds a reason) | Hold | Quarantine | **Review** |
| Hold | legacy Hold | Hold | Quarantine | **Review** |
| Quarantine | Quarantine | Quarantine | Quarantine | Quarantine |
| Defer | Defer | Defer | Defer | Defer |
| Reject | Reject | Reject | Reject | Reject |

Order of protection used by the table: `Quarantine > Review > Hold > Allow`. Review beats Hold
because both withhold delivery and Review additionally guarantees a person looks; Quarantine beats
both because the message is contained rather than waiting. `Defer` and `Reject` are pre-acceptance
outcomes and are never replaced by a pack.

The pack's action ceiling caps a **fired band** exactly as Phase 1 defines it. It does not cap an
escalation: missing evidence, conflicting evidence, insufficient coverage or sample, and zero
tolerance produce `Review`, and the evaluator already refuses to cap those. That asymmetry is the
safety property, not an exception to it.

## 2. The durable state machine

`DeliveryState` gains `Review = 7` (append only). The queue owns the state; Policy names the
outcome, the queue stores and advances it.

```
assessment action Review
        |
        v
queue_item.state = Review, each recipient.state = Review
        |  payload stays in the spool
        |
        +-- reviewer Release ------> recipient Queued -> delivery -> Delivered
        +-- reviewer Quarantine ---> recipient Quarantined (existing resolver)
        +-- reviewer Reject -------> recipient TerminalFailure (retained, not delivered)
        +-- deadline elapses ------> recipient Quarantined, actor policy-scheduler
```

Rules:

- **A Review item is never claimable.** `ClaimNextAsync` already selects only `Queued` and
  `RetryScheduled` item states; adding Review to the roll-up is what keeps it out of that set.
  There is no worker change beyond roll-up and listing.
- **Roll-up:** a mix of recipients resolves to `Quarantined` if any is quarantined, else `Review` if
  any is reviewing, else `Held` if any is held, else the existing delivery states.
- **The payload is retained** while the item is Review. Retry expiry must not purge an open
  review: admission sets the item's expiry to at least the review deadline plus the existing
  retention window, and the expiry sweep skips items with an open Review recipient.
- **Restart is not special.** The state, deadline and payload are in SQLite. There is no in-memory
  timer. A Host restart past a deadline leaves the item Review; the first recovery sweep after
  startup performs the same escalation it would have performed on time, and the review surface
  shows the item as overdue until it does.
- **Resolution is per recipient**, like delivery. A multi-recipient message can have one recipient
  released and another quarantined; the queue's existing per-recipient model already supports that,
  and the item roll-up reports the mixture.

The queue methods, all tenant-scoped and transactional:

```
ResolveReviewAsync(queueId, ReviewResolution, decidedBy, reason, tenantId, cancellationToken)
SweepReviewDeadlinesAsync(now, cancellationToken)
ListReviewsAsync(query, cancellationToken)
```

`ReviewResolution` is `Release`, `Quarantine`, `Reject`. Each appends a `DeliveryAttemptOutcome`
(appended members `ReviewReleased`, `ReviewQuarantined`, `ReviewRejected`) and updates the item
roll-up. The attempt log is delivery history; the policy audit in section 5 is the review history.
Both are written for a resolution.

## 3. The deadline

- `PolicyPack.ReviewDeadline` (default 24 hours) is a new pack field, validated to a bounded range
  (proposed 1 hour to 7 days). It is resolved when the review is admitted and **copied to the queue
  row**; a later pack change never moves an existing deadline.
- At expiry the item becomes `Quarantined`. That is the only automatic escalation in v1. It never
  releases, never delivers, and never silently settles; a pack cannot configure an automatic
  release.
- The sweep runs on the delivery worker's existing recovery tick (five minutes by default) and is
  idempotent: it selects Review recipients whose `review_deadline <= now`, moves them, and writes
  one event per recipient, guarded by the from-state so a concurrent reviewer decision wins exactly
  once.
- A reviewer acting at or after the deadline races the sweep; the guarded update makes one of them
  lose and the audit records which. A release after quarantine is the existing quarantine resolver,
  not a second review transition.

## 4. The review surface

### Data the console needs

For each open review: queue id, internal message id, assessment id, recipient(s), the policy
decision record (verdict, pack id/version, rule set version, deciding rule id/version, threshold,
reason code/message, missing dimensions, coverage, evidence digest), the stored review deadline,
the review queue/owner, the escalation rule, and the allowed next actions with their consequences.
A null policy record is shown as **"No policy pack was active"**; it is not rendered as Permit and
no rule or threshold is invented. A Review queue state can only be created by a pack decision, so a
null record on a Review item is an integrity anomaly the surface names rather than explains away.

### Routes

| Route | Verb | Privilege |
|---|---|---|
| `/v1/policy/reviews?state=open&overdue=&limit=&after=` | GET | Review |
| `/v1/policy/reviews/{queueId}` | GET | Review |
| `/v1/policy/reviews/{queueId}/release` | POST | Review |
| `/v1/policy/reviews/{queueId}/quarantine` | POST | Review |
| `/v1/policy/reviews/{queueId}/reject` | POST | Review |

Every mutation takes a reason in the body and an actor from the authenticated principal. The
response is the same typed shape as the read, with the new state, so the console does not have to
guess. Tenant comes from the principal and is never a parameter.

`AwaitingDecision` listings include Review alongside Held and Quarantined, and a dedicated
`Review` filter is added to `QueueListingFilter` so the surface can page reviews without scanning
held traffic.

### Capability descriptor

`PolicyActionKind` gains `ReleaseReview`, `QuarantineReview` and `RejectReview`; the existing
`Review` entry changes from `NotAvailable` to `Available` once this slice lands. `Review` carries
`ResultingState = WithheldForReview`, `RequiredPrivilege = "Review"`, `ReviewTerms` (queue, owner if
discloseable, deadline, escalation, next action), `NotificationEffect = review_queue`, and
`AuditEventKind = review.*`. The three resolution capabilities carry `ResultingState = Delivered`,
`Contained` and `Rejected` respectively, with `Reversible` and `ReversalAction` stated per action.

Copy remains pinned: Review is "Awaiting human review; no final delivery decision has been made."
Quarantine is "Stopped before delivery while awaiting review." No action is ever described as
"under review" when it is contained, and no review is described as contained.

## 5. Audit and the decision record

Two durable records, both required:

1. **Queue attempt history** (`queue_attempt`): `ReviewReleased`, `ReviewQuarantined`,
   `ReviewRejected` and the sweep's `ReviewExpired`, each with actor, reason and timestamp. This is
   the delivery-side history and already survives restart.
2. **Policy review events** (new `host_policy_review_event`): one row per transition with tenant,
   queue id, assessment id, from-state, to-state, actor, reason, timestamp, pack id/version, rule id
   and version, and the evidence digest. This is the operator-facing audit: "who decided, on what
   policy, and when".

The Host database and the queue schema are one SQLite file in this deployment, so the review event
and the queue state change are committed in one transaction. If a future deployment separates them,
the event becomes an outbox row in the queue's transaction and the Host drains it; a state change
with no audit row is not acceptable.

A reviewer's resolution is a human action. Recording it as a feedback label is **not** part of this
slice: feedback writes remain read-only until Phase 4, and this design adds only the review event.

## 6. Owner split

| Piece | Owner | Why |
|---|---|---|
| `MailAction.Review` and `DeliveryState.Review` enum members | `policy-` with `overview-` approval | Core contract, append-only, no consumer outside this slice |
| Composition table, ceiling behaviour, `PolicyPack.ReviewDeadline` and its validation | `policy-` | Policy's authority |
| Mapping `MailAction.Review` to queue admission and the stored deadline | Assessment composition (`overview-` assigns) | The composition root maps actions to dispositions |
| Queue state, claim exclusion, roll-up, deadline sweep, resolutions, listing filter | `queue-` | Durable state and spool |
| Queue attempt outcomes and the review event table | `queue-` (attempts) and Host (`host_policy_review_event`) | State and audit are written together |
| Host routes, contracts, auth split, review-event table, decision-ledger join | `ingress-` by default; `overview-` assigns if that lane stays exited | Host surface ownership |
| Review queue UI, capability binding, pinned copy | `desktop-` | Console |
| Delivery dispatch | `queue-`, no transport change | The delivery worker already skips non-queued states |
| Ruling and merge | `overview-` | Design authority |

No lane writes another lane's files by assumption. The enum append and the queue schema are the two
changes that need explicit coordination because they cross Core and Queue.

## 7. Migration and rollback

**Additive only:**

- Core enum members appended: `MailAction.Review = 5`, `DeliveryState.Review = 7`.
- `queue_recipient.review_deadline TEXT NULL` plus
  `ix_queue_recipient_review (state, review_deadline)`. Existing rows are untouched; a null
  deadline means the row predates Review.
- `host_policy_review_event` table and its tenant/time index.
- `PolicyPack.ReviewDeadline` is a payload field, not a column; existing packs read as the 24-hour
  default.

No accepted message is rewritten, no recipient state is reinterpreted, and no delivery is
retroactively changed.

**Rollback:** disable the authoritative pack path. New assessments stop producing Review; existing
Review items remain durable and are resolved or escalated by the same queue code. The deadline
sweep stays enabled even when packs are disabled, because otherwise rollback itself strands every
open review. The rollback is a configuration change, not a data migration.

**If the review state is unavailable at admission** (schema missing, queue cannot store the
deadline, sweep disabled), the message is admitted as `Quarantine` with
`policy.review_state_unavailable`, never as Allow and never as Defer. Quarantine is contained and
reviewable through the existing resolver; Defer returns the mail to the sender and looks like a
policy decision. Startup readiness fails when the review schema is absent, so the primary guard is
before traffic, and the admission fallback is the second.

## 8. Tests the slice must pin

Composition:
- The full table above, each cell; a pack Review cannot relax a legacy Hold, Quarantine, Defer or
  Reject; a pack ceiling does not cap an escalation Review.

Queue:
- A Review recipient is never claimed; roll-up returns Review, and Quarantined when mixed.
- Release, Quarantine and Reject each move the recipient and append the right attempt outcome.
- The sweep moves a due Review to Quarantine, is idempotent across two runs, and records actor
  `policy-scheduler`; it never releases.
- A reviewer decision racing the sweep settles exactly once.
- Restart before the deadline preserves Review and the deadline; restart after the deadline
  escalates on the first sweep.
- Payload retention keeps an open review's bytes and releases them after resolution and retention.

Host:
- Auth split (Review for reads and resolutions, 401 anonymous, 403 sender), tenant scoping,
  keyset paging, overdue computation, null policy record, audit row in the same transaction.
- Capability catalogue shows Review available and the three resolutions with their consequences.

## 9. Open questions for `overview-`

1. Deadline bounds: is 1 hour to 7 days right, or should the pack be allowed a longer window?
2. `Reject` privilege: the design uses `Review` for all three resolutions, matching quarantine
   release. Should Reject require `Administer` because it settles the message permanently?
3. Automatic escalation: Quarantine only in v1, or may a pack name a different automatic action? A
   release must never be configurable.
4. Review event placement: confirm one SQLite transaction now, with the outbox as the rule if the
   queue is ever separated from the Host database.
5. Owner for the Host routes: `ingress-` is exited. Assign `policy-`, or a Host lane, before Phase 3
   starts.
6. Does a review resolution create a feedback label now, or wait for Phase 4? This design says wait.
