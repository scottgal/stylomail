# MailAction.Review: the bounded review state

Status: **approved, implementation in progress.** Rulings a-f accepted 18 Oct 2026. Phase 3's
authoritative composition still waits for this slice to be green.

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
- **A review transition never steals a live sibling delivery.** If another recipient is in flight,
  the item is `Delivering` and leased. The transition writes the review recipient and the audit
  fields but leaves the item state and lease to the worker that owns them, or to the lease expiry.
  Re-rolling the item to `Queued` while a worker holds it, or clearing the lease, would let a second
  worker claim the same item and duplicate a delivery. The owner keeps authority until recovery
  actually reclaims the lease, not until its timestamp passes: an expired but unreclaimed lease is
  still preserved, and late renewal/completion remain valid. Only the recovery path may take it.
- **Human review does not consume a transport retry.** A review transition increments no attempt
  counter. Only an actual delivery attempt counts against `MaxAttemptsPerRecipient`, so releasing a
  review and then hitting one temporary transport failure schedules a retry rather than exhausting
  the budget.

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
The surface reads the **opening decision** by its assessment id, never the latest decision for the
message. A reassessment therefore cannot rewrite why the review was opened. If the ledger row has
been pruned, `Policy` is null and `PolicyRecordMissing` is true while the opening reference still
names the pack, rule, reason and digest. A row that predates the opening chain sets
`LegacyReviewOpeningMissing`; the surface names it and does not fabricate a record. A null policy
record is shown as **"No policy pack was active"**; it is not rendered as Permit and no rule or
threshold is invented.

### Routes

| Route | Verb | Privilege |
|---|---|---|
| `/v1/policy/reviews?state=open&overdue=&limit=&after=` | GET | Review |
| `/v1/policy/reviews/{queueId}` | GET | Review |
| `/v1/policy/reviews/{queueId}/release` | POST | Review |
| `/v1/policy/reviews/{queueId}/quarantine` | POST | Review |
| `/v1/policy/reviews/{queueId}/reject` | POST | **Administer** (ruling b) |

Every mutation takes a reason in the body and an actor from the authenticated principal. Reject is a
permanent refusal, not a routine reviewer action, so it carries `Administer`; Release and
Quarantine are reversible and stay on `Review`. The
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
2. **The opening record** (new `queue_review_opening`, queue-owned): one immutable row per review
   naming the assessment id, pack id/version, rule-set id/version, deciding rule id/version, reason
   and evidence digest. It is written in the same transaction as the Review admission, and a Review
   admission without it is refused. This is what survives a reassessment or a pruned ledger.
3. **Policy review events** (new `queue_review_event`, queue-owned): one row per transition with
   tenant, queue id, recipient, assessment id, from-state, to-state, actor, reason, timestamp, an
   opaque policy reference naming the pack/rule versions, and the opening event id. The opening
   event (`review.opened`) is the first row and its id is the opening reference every later event
   carries, so a human resolution and the deadline sweep name the same decision. The queue owns it
   because the audit must commit with the state it describes; a Host-side table would need a
   two-phase write. This is the operator-facing audit: "who decided, on what policy, and when".

The Host database and the queue schema are one SQLite file in this deployment, so the event and the
state change are committed in one transaction. **Outbox rule for a future split:** if the queue and
the Host store are ever separated, a single-transaction write is no longer available, so the queue
writes an outbox row in the same transaction as the state change and the Host drains it into its own
audit store. Do not build the outbox now; do not split the stores without it. A state change with no
audit row is not acceptable in either arrangement.

A reviewer's resolution is a human action. Recording it as a feedback label is **not** part of this
slice: feedback writes remain read-only until Phase 4, and this design adds only the review event.

## 6. Owner split

| Piece | Owner | Why |
|---|---|---|
| `MailAction.Review` and `DeliveryState.Review` enum members | `policy-` with `overview-` approval | Core contract, append-only, no consumer outside this slice |
| Composition table, ceiling behaviour, `PolicyPack.ReviewDeadline` and its validation | `policy-` | Policy's authority |
| Mapping `MailAction.Review` to queue admission and the stored deadline | Assessment composition (`overview-` assigns) | The composition root maps actions to dispositions |
| Queue state, claim exclusion, roll-up, deadline sweep, resolutions, listing filter | `queue-` | Durable state and spool |
| Queue attempt outcomes and `queue_review_event` | `queue-` | State and audit are written together |
| Host review routes, contracts, auth split and decision-ledger join | **`policy-` for this slice** (ruling e), with `review-` verifying the queue changes | `ingress-` is exited; the ownership is temporary and returns to the queue/Host owners when they are live |
| Review queue UI, capability binding, pinned copy | `desktop-` | Console |
| Delivery dispatch | `queue-`, no transport change | The delivery worker already skips non-queued states |
| Ruling and merge | `overview-` | Design authority |

No lane writes another lane's files by assumption. The enum append and the queue schema are the two
changes that need explicit coordination because they cross Core and Queue.

## 7. Migration and rollback

**Additive only:**

- Core enum members appended: `MailAction.Review = 5`, `DeliveryState.Review = 7`.
- `queue_recipient.review_deadline TEXT NULL` plus
  `ix_queue_recipient_review (state, review_deadline)`.
- `queue_review_opening` and `queue_review_event.opening_event_id`, applied by an additive
  v4/v5-to-v6 queue migration. Existing rows are untouched; a review row with no opening is named
  `legacy_review_opening_missing` rather than backfilled.
- `queue_review_opening` table, and `queue_review_event.opening_event_id`, owned by the queue, with
  an additive v4/v5-to-v6 migration.
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
- The sweep moves a due Review to Quarantine, is idempotent across two runs, records actor
  `policy-scheduler`, and its event carries the opening event id and assessment id; it never releases.
- A review admission with no opening decision is refused. After a reassessment, the review read
  still resolves the opening assessment, pack, rule and reason.
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
