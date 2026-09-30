# Console states the harness cannot reach, and what would reach them

Written 2026-09-30 for `corpus-`, at `overview-`'s request: the generator needs a checklist of
**states**, not a count of emails. Rows below are the console states that a polished harness would
drive and does not, each with the reason, the measurement that establishes the reason where there is
one, and the shape of traffic or harness change that would reach it.

The instrument is `ux-scripts/probe-submission-route.sh`, which is not an assertion and does not
fail: it starts a throwaway Host on the harness switches, posts one message, and prints what
`/health/ready`, `POST /v1/submissions`, the three message listings, the decision ledger and the
release route actually answered. Measurements from a run of it are summarised in "What was measured"
below; where a fact came from another lane's run it is attributed, because a table that mixes the two
without saying which is which is how a measurement becomes a rumour.

## The deployment shape everything is measured on

`StyloMail:Assessment:Provider=Nimble` plus `STYLOMAIL_PROFILE_KEY` alone, no cloud credential
(decisions 17 and 18). `console-harness.sh` reaches it with `CONSOLE_PROVIDER=nimble`, and refuses to
start unless Ollama is answering on 11435 with a `nimble` model present. A Nimble Host is a real
assessor rather than an absent one, which is the whole difference between this table's reachable and
unreachable columns.

## The states

| # | State the console renders | Reachable today | What reaches it |
| --- | --- | --- | --- |
| 1 | **An item awaiting attention** (the middle pane is not empty) | **Yes**, on screen since 2026-09-30 | Nimble Host plus traffic policy holds, asserted by `run-console-nimble-smoke.sh`. A message that policy *allows* cannot populate this pane at all, by design: the listing enumerates what needs attention, not what was accepted (see "Normal delivery is not enumerable"). |
| 2 | **A held item** | **Yes**, on screen since 2026-09-30 | The same message, `state: Held`, listed under both `awaiting_decision` and `held`, each asserted separately in that script rather than assumed to follow from the other. |
| 3 | **A quarantined item that can be released** | **Yes, on screen since 2026-10-01** | Risk at or above `QuarantineThreshold` (0.80). This lane's own fixture, `tests/StyloMail.Desktop.Tests/fixtures/quarantine-threshold-payment-change.eml`, measured on a wiped Host on the Nimble shape: 202 `Accepted`, recipient `Quarantined`, action `Quarantine`, `reEvaluateBy: null`, decision risk **0.8219178082191781** under `policy.risk_above_quarantine`, `?state=quarantined` **1 row** and `?state=held` 0, then `POST /v1/quarantine/{queueId}/release` **200 `{released: true, releasedBy: harness}`**. The lever is `payment_redirection`, fired by an explicitly **changed** payment destination (a new sort code and a new account number) rather than by "pay to the account below", plus the other weighted dimensions the message fires; `corpus-` independently reached 0.9315 with a nine-dimension ladder. The fixture is **threshold-targeted by construction** and must never be quoted as a detection rate. The console half landed 2026-10-01: `run-console-quarantine-smoke.sh` submits it, refuses to continue unless the route quarantined it, lists `Quarantined`, opens the row (risk index **0.822** as rendered, with `policy.risk_above_quarantine` among its reasons) and releases it. The history hypothesis this row used to carry is measured false; see "Quarantine needs weighted dimensions" below. |
| 4 | **A message that joins to its decision** | **Yes**, asserted 2026-09-30 | `run-console-nimble-smoke.sh` submits `nimble-held-message.eml`, refuses to drive the console unless the route held it, and opens the decision from the row. The join itself was never the gap: `GET /v1/decisions?messageId=` has been implemented and wired since `ecb86e1`, and a Nimble-backed Host lists a held message carrying the decision's `internalMessageId`. The script asserts the pane is empty before the button is pressed, so what it establishes is the join rather than a pane that was filled another way. |
| 5 | **A sender with history** | Not yet | Repeated traffic from one principal. The listing carries per-sender rows already (`console_seed_management` seeds one); "history" is about what the batch leaves behind. |
| 6 | **A feed carrying real traffic** | Not yet asserted | The Hub is mapped in the main smoke and its notices are real either way. What no run has shown is a notice caused by traffic the *corpus* injected rather than by the harness's own seeding. |
| 7 | **Two evidence rows that differ only by trend window** | **In the response, not on the screen** | The Nimble Host's decision detail carries `behavioural.trend.velocity` and `behavioural.trend.acceleration` with `window: "burst"` and `window: "slow"`, over two scopes, one row per window. They are `Unavailable` with `sampleSupport: 0` until there is sender history, and that is also why they are **not rendered**: the pane draws a decision's evidence under the reasons that cite it, and no reason cites a trend signal while every one of them is unavailable. This row previously read "yes, in a live response", which was about the body and not about the console. What the committed smoke asserts is the other half of the contract, that the qualifier is absent on the unwindowed rows that make up most of a real response. |
| 8 | **Recovery after a feed drops** (the console returns to `Live` and re-reads) | **Yes, on screen since 2026-10-01, for an outage shorter than the retry budget** | `run-console-feed-recovery-smoke.sh` and `console-feed-recovery-smoke.yaml`, 10 actions, 0 failed, exit 0. The harness grew three things for it, all of which the restart needs to be the *same* Host twice: `CONSOLE_REUSE_KEYS=true` keeps the principal key the first start wrote (otherwise the console's key is refused and the run reads as "recovery is broken" when it has tested a new deployment), `CONSOLE_HOST_LOG` keeps the restart's log instead of overwriting the first one's, and `console_pause_sender` applies a change through the Host's own control route from outside the console. **`Live` is not the claim and is asserted second:** the console sets its feed state to Live as soon as the socket is back and raises the resynchronisation after, so a script that stopped at the headline would pass against a console that reconnected and never re-read. The claim is asserted by making something change while the console is blind: the runner pauses the harness sender at T+20 and the script requires `resume-sender-harness` afterwards. Measured, and this is the part that makes it a proof rather than a coincidence: the re-assertion of `Live` took **41.8s**, which is SignalR's fourth and final attempt, so the Host was provably down across the first three and the pause was in place ~20s before the socket came back. A change published to the Hub while nothing is connected is delivered to nobody, so that button can appear only by the console reading the senders again. Screenshots 02 and 03 are the pair: amber `Live updates stopped, screen may be out of date` over a row offering **Pause**, then normal `Live` over the same row offering **Resume** and subtitled `Paused: paus...`. The amber clearing is the second signal, and it is not cosmetic: `Classes.stale` is bound to the model's `ScreenMayBeStale`, and `SurfaceIsCurrent()` clears it only when all three re-reads landed, so a reconnect whose reads failed would leave it amber. **Still out of reach: an outage longer than the budget.** After roughly 42 seconds the console stops retrying, and this run cannot reach that ending. Reading the code for it turned up something worth stating: `ReconnectAsync` has exactly one caller (`OpenConnectionDialogAsync`), and that caller is gated on `dialog.KeyChanged`, which is set only by saving a key or clearing one. So after the retry budget is spent there is no in-console path back that does not involve re-entering the key, and saving the address alone reports "Address saved." without reconnecting. That is the next run and a question for the lane, filed rather than patched: it is a product decision, not a harness one. |
| 9 | **A quarantine release's success path** | **Yes, on screen since 2026-10-01** | `Quarantined` -> `POST /v1/quarantine/{id}/release` -> 200 `{released: true}` -> `Queued`, and a second call answers `{released: false}` rather than claiming a second release. It needs `HostPolicies.Review` (`ApiRoutes.cs:67`), already granted at `console-harness.sh:283`, so there is no privilege gap and no wiring change. **The console half, measured 2026-10-01:** `run-console-quarantine-smoke.sh` asserts Release absent before the row is opened and present after, that Confirm is refused until a reason is typed, that the result line names the principal (`released. Recorded against`), and then that the row is **gone**. The last of those is what found a defect in my own lane: a successful action refreshed the sender controls and never re-read the listing, so the released row stayed on screen with its Release button still offered. Fixed in the same commit by re-reading through `LoadSelectionAsync`. **One thing to build for:** after a release the row is `Queued`, and `GET /v1/messages` enumerates only `awaiting_decision`, `held` and `quarantined`, so the released row appears in no listing at all. The visible effect of a release is the listing emptying, not the row moving somewhere it can be pointed at. |
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

**The route declines when the assessment cannot proceed, on the shapes measured here.** With no
assessor it refuses before spooling anything (`assessor_unavailable`, `503`). With an assessor that
cannot reach its provider it refuses with `Defer` (`503`, a retryable refusal, and the message is not
queued). So an empty queue on those shapes is correct behaviour for a correct reason, and spec 14.5's
reading is right: the fix is a harness with a working provider, which `CONSOLE_PROVIDER=nimble` now is.

**Open, and owed a probe run: whether "provider selected, model down" refuses.** `ingress-` measured a
Nimble host whose model was down accepting the submission (`202`) with the *decision* coming back
`Defer`, which is a different answer from the `503 deferred` above and would put deferred rows in the
queue rather than leave it empty. The shapes differ: theirs had the provider selected with its model
down, this row's Jev provider points at an endpoint that cannot answer at all. The harness has no
switch for theirs, so the two have not been reconciled, and the difference decides whether "the queue
is empty" or "the queue holds deferred rows" is the correct reading of a Host that cannot assess. It is
this lane's to settle, with `probe-submission-route.sh` once that shape is startable.

**The assessment route cannot carry a planted deterministic fact.** `POST /v1/assessments` hands the
pipeline `PayloadReferences.Ephemeral` (`AssessmentsEndpoints.cs:64`, `PayloadReferences.cs:51`), so the
step that reads the original bytes finds none and records `assessment.deterministic_extraction` as
`Unavailable`. Only `POST /v1/submissions` spools the payload and runs the MIME layer. Every ledger
entry `console_seed_decision` seeds therefore has its deterministic rows Unavailable whichever provider
is composed, and decision 24's corroboration gate cannot be exercised through that path at all. Not a
defect in the harness: a fact about which route can carry which fact, and the reason the corpus seeds
through `/v1/submissions` instead.

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

**Quarantine needs weighted dimensions: not vehemence, and not history.** The hypothesis this section
used to carry (that the behavioural and campaign dimensions crossed 0.80 once there was sender
history) was tested and is false. Eight near-duplicate messages from one sender moved
`campaign.security_bearing_variant` from absent to Available with value 1 and left the index at
**0.589041 on all eight**: `CompositeRiskScorer.Score` iterates the *weights* dictionary
(`src/StyloMail.Policy/CompositeRiskScorer.cs:75`) and `DimensionWeights` lists only `semantic.*` keys,
so campaign and behavioural evidence is dropped from the numerator and the denominator alike. Filed as
a medium issue. What does cross the threshold is the count of firing **weighted** semantic dimensions,
on a single message: measured gradient 5 dimensions 0.3973 Allow, 6 dimensions 0.5753 Hold, 7
dimensions 0.6849 Hold, 9 dimensions 0.9315 Quarantine, matching the weighted arithmetic to four
decimals. So the corpus needs a message that fires many weighted dimensions at once, not a heavier word
list and not repetition. One honest limit, `corpus-`'s and worth carrying downstream with the fixture:
the message that reaches 0.9315 was written while looking at the model's earlier answers, so it is a way
to **populate** the state and must never be quoted as a detection rate. Their manifest labels
threshold-targeted fixtures as such.

**A reading is only a reading on an empty Host, and this probe was not checking.** Found 2026-09-30
while verifying something else. The Host's database lives under `$CONSOLE_RUN/data`, and the probe
never wiped its scratch, so a run reused the database the previous run left behind. A default-shape
(Jev, provider unreachable) run listed a held message and a 0.575 `Hold` decision scored by
`nimble:latest`, neither of which that shape can produce, both left in that database sixteen minutes
earlier by a Nimble-shape run. Read without noticing, that is a measurement saying "a Host that cannot
assess nonetheless populated the queue", which is the exact claim the table above exists to settle.
The probe now wipes `$CONSOLE_RUN` before the keys are generated, and the rows below were re-measured
on wiped scratch wherever they were re-measured.

Two consequences worth carrying, because they decide how much of the table above to trust:

- **Contamination can only make a queue look fuller, never emptier.** A stale database adds rows. So
  every "the listing was empty" reading in the table is safe from this defect, and every "the listing
  held N rows" reading needed re-measuring: the Nimble held row and the quarantine row have both been
  re-measured on a wiped Host and both reproduce.
- **Two probes at once must not share a scratch directory**, since the wipe at the start of one deletes
  the other's Host state. Run shapes in sequence, or give a concurrent one its own `CONSOLE_RUN`.

**The risk index in this table is measured on both sides of decision 31, and it does not move.**
`overview-` (info, 1 Oct) recorded that `CompositeRiskScorer.Compute` now applies decision 31's
one-sided rule: a `semantic.conversational_continuity` row that is available but does **not** confirm
(value at or below the port's 0.5 mid-point) contributes nothing rather than a counted zero. Any risk
index quoted from a run where a conversation window was supplied has to say which side of that change
it came from, so this lane re-measured rather than reasoning about it, on a wiped Host and on the
current tree (the change is uncommitted in the working tree; `console_build_all` builds what is on
disk, so the probe below ran against it):

    riskIndex 0.8219178082191781, action Quarantine, reasons [policy.risk_above_quarantine, evidence.masked_dimensions]
    semantic.conversational_continuity: availability NotApplicable, value null

Identical to the value measured 2026-09-30 before the change, to the last digit. The reason is the one
`overview-` gave and this confirms on the route: nothing supplies a conversation context on the
submissions path, so the row is **NotApplicable** and the new rule (which fires only on an *available*
non-confirming row) cannot reach this score at all. So the 0.8219 in row 3 and the 0.822 pinned in
`console-quarantine-smoke.yaml` stand on both measurements, and this lane has no index measured with a
window supplied. Artifacts, cited from the repository rather than from `/tmp`:
`.styloagent/scratch/desktop/quarantine-fixture-decision-post-decision-31.json` and the submission,
listing and probe log beside it.

**The runners reported a failing run as a pass, and that is the same defect one level up.** Found
2026-10-01 by the first run in this lane's history to fail: the quarantine run's first draft failed its
twenty-sixth action, printed `Result: FAIL`, wrote `"success": false` to `result.json`, and
`dotnet run` still exited 0, which every runner passed through with `exit $STATUS`. So a red run was
indistinguishable from a green one from the outside. Every green tick this lane has reported was read
from a `result.json` that said `success: true`, so those reports stand on their evidence; what could
not have happened is a failure being noticed. All six runners now read the verdict where it is written
(`console_final_status`), and README carries the lesson. Recorded here because this document is a
table of how much to trust a reading, and "the instrument was never checked" is the sharpest thing
learned about it so far: the probe stored a previous run's database and the runner cannot fail, and
both were found by reading the instrument rather than the result.

## What this lane owes next, in order

1. ~~A smoke assertion for the message-to-decision join (state 4)~~ **Done** 2026-09-30:
   `run-console-nimble-smoke.sh`, three screenshots, 19 actions, pass.
2. ~~The quarantine run (states 3 and 9)~~ **Done** 2026-10-01: `run-console-quarantine-smoke.sh` and
   `console-quarantine-smoke.yaml`, 27 actions, pass, with `quarantine-threshold-payment-change.eml`
   submitted through `POST /v1/submissions` and the route's own `Quarantined` answer as the
   precondition. It asserts the release's *visible* effect, the listing emptying, which is the only
   effect a release has: a released row is `Queued` and appears in no listing. That assertion found a
   defect in this lane and cost a console fix, both recorded in state 9 above.
3. An assertion for the trend-window qualifier (state 7). The response carries the rows and the screen
   does not, and the reason is now known rather than assumed: evidence renders under the reasons that
   cite it, so this needs a decision in which a reason cites a windowed signal, which needs
   `sampleSupport` above zero, which is traffic. It is a `corpus-` question and not a harness change.
4. ~~Recovery after a feed drops (state 8)~~ **Done** 2026-10-01 for the short outage:
   `run-console-feed-recovery-smoke.sh` and `console-feed-recovery-smoke.yaml`, 10 actions, pass, with
   the re-read asserted through a change made while the console was blind rather than through the
   headline. The longer outage, past SignalR's ~42 second retry budget, is now item 7 below.
5. The "provider selected, model down" shape: make it startable in the harness, then one
   `probe-submission-route.sh` run settles whether the route refuses it or accepts it with a `Defer`
   decision (see "What was measured").
6. The traffic hub's absent consumer, which `overview-` placed on this lane: the console must work with
   the Hub absent and say so rather than look quiet, and must not read "the Hub is not there" as
   "nothing is happening". Behind the conversation graph.
7. A run for the outage that outlasts the reconnect budget, and the decision behind the finding it
   would cover: after SignalR gives up (roughly 42 seconds), `ReconnectAsync` is reachable only
   through `OpenConnectionDialogAsync`, which is gated on `dialog.KeyChanged`, which only saving or
   clearing a key sets. So the console has no way back that does not involve re-entering the key, and
   saving a changed address alone reports "Address saved." and reconnects nothing. The run is cheap
   once the behaviour is decided; the behaviour is the operator's call, not the harness's.
