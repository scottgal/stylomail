# Console states the harness cannot reach, and what would reach them

Written 2026-09-30 for `corpus-`, at `overview-`'s request: the generator needs a checklist of
**states**, not a count of emails. Rows below are the console states that a polished harness would
drive and does not, each with the reason, the measurement that establishes the reason where there is
one, and the shape of traffic or harness change that would reach it.

The instrument is `ux-scripts/probe-submission-route.sh`, which is not an assertion and does not
fail: it starts a throwaway Host on the harness switches, posts one message, and prints what
`/health/ready`, `POST /v1/submissions`, the three message listings, the decision ledger and the
release route actually answered. Every measurement quoted here is from a run of it, with the
responses left in `/tmp/stylomail-probe-*` and summarised in "What was measured" below.

## The deployment shape everything is measured on

`StyloMail:Assessment:Provider=Nimble` plus `STYLOMAIL_PROFILE_KEY` alone, no cloud credential
(decisions 17 and 18). `console-harness.sh` reaches it with `CONSOLE_PROVIDER=nimble`, and refuses to
start unless Ollama is answering on 11435 with a `nimble` model present. A Nimble Host is a real
assessor rather than an absent one, which is the whole difference between this table's reachable and
unreachable columns.

## The states

| # | State the console renders | Reachable today | What reaches it |
| --- | --- | --- | --- |
| 1 | **An item awaiting attention** (the middle pane is not empty) | **Yes**, measured | Nimble Host plus traffic policy holds. A message that policy *allows* cannot populate this pane at all, by design: the listing enumerates what needs attention, not what was accepted (see "Normal delivery is not enumerable"). |
| 2 | **A held item** | **Yes**, measured | The same message, `state: Held`, listed under both `awaiting_decision` and `held`. |
| 3 | **A quarantined item that can be released** | **Not yet reached**, and no route gap observed | Risk at or above `QuarantineThreshold` (0.80). Three single messages scored 0.48, 0.55 and 0.58, so no single message tested comes close. Hypothesis for `corpus-`: the dimensions that raise the index (`behavioural.*`, `campaign.near_duplicate`) need **history**, so a batch from one repeated sender is what will cross 0.80; a single message cannot. |
| 4 | **A message that joins to its decision** | **Very likely**, not yet asserted on screen | The join itself is **not** the gap: `GET /v1/decisions?messageId=` is implemented and wired (`ecb86e1`), which is why this row is about a run rather than a route. What a run could not do is start one, because the queue listings stayed empty. A Nimble-backed Host lists a held message whose `internalMessageId` is the one on the decision, so the run now has a row to start from and what is missing is the assertion. |
| 5 | **A sender with history** | Not yet | Repeated traffic from one principal. The listing carries per-sender rows already (`console_seed_management` seeds one); "history" is about what the batch leaves behind. |
| 6 | **A feed carrying real traffic** | Not yet asserted | The Hub is mapped in the main smoke and its notices are real either way. What no run has shown is a notice caused by traffic the *corpus* injected rather than by the harness's own seeding. |
| 7 | **Two evidence rows that differ only by trend window** | **Yes, in a live response**, measured | The Nimble Host's decision detail carries `behavioural.trend.velocity` and `behavioural.trend.acceleration` with `window: "burst"` and `window: "slow"`, over two scopes. They are `Unavailable` with `sampleSupport: 0` until there is history, but the rows are distinct and present, which is what the console's rendering needs. This closes a limitation `README.md` has carried since `008f90d`. |
| 8 | **Recovery after a feed drops** (the console returns to `Live` and re-reads) | No | A Host that comes back on the same address with the same key and database. This is a harness gap and it is this lane's, not the generator's: the runner would have to keep the principal key and the database across the restart, where today both are regenerated per run. |
| 9 | **A quarantine release's success path** | Blocked on state 3 | The route exists (`POST /v1/quarantine/{id}/release`, `Review`) and the console has the surface; the refusal path is already covered by a unit test. Nothing is quarantined to release yet. |
| 10 | **Native OS dialogs** | No, by design | There are none yet. When the API key entry lands it will open one, and an `NSOpenPanel` is not an Avalonia control, so the harness can neither see nor click it. |

## What was measured, and where the earlier claim was wrong

`console-smoke.yaml` has said since it was written that the queue stays empty because "POST
/v1/submissions declines an assessment when the semantic provider is unavailable". Measured on
2026-09-30, that is right about the queue and imprecise about the route, and the difference matters to
anyone reading a status code:

| Host shape | `/health/ready` | `POST /v1/submissions` | Queue | Ledger |
| --- | --- | --- | --- | --- |
| no assessor (`CONSOLE_ASSESSOR=false`) | `503` `assessor_unavailable` | `503` `assessor_unavailable` | empty | empty |
| Jev, endpoint unreachable (the harness default) | `200` ready | `503` `deferred`, "retry when the provider is reachable" | empty | one decision, action `Defer`, 12 masked dimensions |
| Nimble, ordinary sample | `200` ready | `202` accepted, recipient `Queued`, action `Allow`, risk 0.479 | empty | one decision, `Allow`, `nimble:latest` |
| Nimble, phishing sample with failing SPF/DKIM | `200` ready | `202` accepted, recipient `Held`, action `Hold`, risk 0.575 | **one item**, under `awaiting_decision` and `held` | one decision, `Hold`, deterministic and semantic signals |
| Nimble, attachment and payment-pressure sample | `200` ready | `202` accepted, `Queued`, `Allow`, risk 0.548 | empty | one decision, `Allow` |

Three things follow.

**The route declines, and it never accepts-and-holds.** With no assessor it refuses before spooling
anything (`assessor_unavailable`, `503`). With an assessor that cannot reach its provider it refuses
with `Defer` (`503`, a retryable refusal, and the message is not queued). So an empty queue on those
shapes is correct behaviour for a correct reason, and spec 14.5's reading is right: the fix is a
harness with a working provider, which `CONSOLE_PROVIDER=nimble` now is.

**An empty queue is not one statement.** On the Nimble shape an *allowed* message leaves the queue
listing empty because `GET /v1/messages` enumerates only `awaiting_decision`, `held` and
`quarantined`, and a message in normal delivery is deliberately not enumerable (the route's own
remarks, `ListingEndpoints`). So "the queue is populated" must mean "an item is awaiting attention",
and benign-allowed traffic will never produce it. A generator that emits mostly ordinary mail and
expects a full pane will read a correct system as a broken one.

**Failing SPF/DKIM/DMARC is not a lever on the disposition.** Measured 2026-09-30 by submitting the
same message four times on one Nimble Host, varying only the authentication block: the risk index was
identical every time, `0.4794520547945206` (35/73), and so was the dimension list, all twelve of them
semantic. What the block does change is a signal's *coverage*: `deterministic.trusted_authentication_failure`
reads `NotApplicable` with no results, `ReducedCoverage` when they arrive without a trusted verifier,
and `Available` when they are marked `fromTrustedVerifier`. No risk dimension consumes that signal, so
it never reaches the score. A generator should not expect auth results to push an item towards `Held`
or `Quarantine`, and a message that looks malformed on that axis is not a weakness in the traffic.

**Quarantine needs history, not vehemence.** Making a single message more obviously malicious did not
raise its score: the attachment and payment sample (0.548) scored *lower* than the simpler phishing
sample (0.575). The dimensions that could push an item past 0.80 are the behavioural and campaign
ones, and on every single-message run they came back `Unavailable` with `sampleSupport: 0`. That is a
testable hypothesis for the corpus rather than a defect: a batch of repeated traffic from one sender
is what makes those dimensions `Available`, and it is also what state 5 needs.

## What this lane owes next, in order

1. A smoke assertion for the message-to-decision join (state 4), now that a listed message exists.
2. A smoke assertion for the trend-window qualifier on a live response (state 7), which the README
   currently denies is reachable.
3. Recovery after a feed drops (state 8), which needs a runner that survives a Host restart.
4. Nothing for the quarantine release (state 3): the finding is a traffic question, and it is filed
   with `corpus-` rather than worked around here.
