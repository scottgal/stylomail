# `transport-` — saved context

## Identity and scope
SMTP/MTA transport, the queue's delivery port, and the Cloudflare Email Routing connector.
Owns `src/StyloMail.Transport/` and `tests/StyloMail.Transport.Tests/`. Touches no other project.

Branch: `main` (no worktree). Repo: `/Users/scottgalloway/RiderProjects/stylomail`.

## Delivered (this session)
- `src/StyloMail.Transport/` — new project. Deps: **Core + Queue only, zero third-party packages.**
  MIME parsing deliberately not referenced; the transport does bounded byte work, not parsing.
- `tests/StyloMail.Transport.Tests/` — **124 tests, all green**, verified with
  `dotnet test tests/StyloMail.Transport.Tests/StyloMail.Transport.Tests.csproj`.
- Both projects added to `StyloMail.slnx`; **whole-solution build is clean** (no errors, no warnings,
  analyzers as errors).
- Not committed — `git add`/`commit` are forbidden for me.

### Layout
- `Smtp/` — protocol: `SmtpReply`, `SmtpReplyReader` (bounded), `SmtpDataWriter` (dot-stuffing),
  `SmtpSession` (client state machine), `SmtpSessionFactory` via `SocketSmtpChannel`, `SmtpBounds`,
  `SmtpTranscript` (redacting), `SmtpUpstream`/`SmtpCredentials`.
- `Delivery/SmtpDeliveryPort.cs` — the queue's egress port implementation.
- `Ingress/` — `SmtpSubmissionListener`, `SmtpIngressSession`, `SmtpIngressOptions`,
  `RecipientDomainPolicy`, `TransportHeaderScanner`, `IngressContracts`.
- `Cloudflare/CloudflareEmailRoutingConnector.cs` — inbound-only Worker ingest.

## Arbitration received (overview-)
- **`reply_to_thread` archive-only IS the intent**; the protocol prose promising delivery is the
  defect. `send_message` when a peer must act on the answer; `reply_to_thread` only to close a
  thread. **If a reply may not have landed, `send_message` is correct** — thread hygiene yields.
- **`Received` `for <recipient>` omission credited** — *assert it*, which is done (`TheHopMarkerNeverCarriesARecipientAddress` + a reflection test that `ReceivedHeaderStamp` cannot even express a recipient).
- **Ingress wiring + the `ISmtpIngressSink` adapter belong to `host-`**; their brief was extended.
  Constructor shapes + the four construction-time traps sent to them. **Do not touch
  `src/StyloMail.Host/`.**
- **ASSIGNED WORK (pending): `queue-` will contact me** to exercise `IDeliveryPort` against the real
  `SmtpDeliveryPort`. `overview-` named the three scenarios to cover: **`InDoubt`**, **partial
  per-recipient results**, and **a throwing port**. "Please cooperate with it." Stand by for their
  contact — do not pre-empt it.
- **Payload-bound coupling: `overview-` chose to ENFORCE, not document.** `host-` will assert
  `transportMax <= queueMax` at construction in the composition root. That is `host-`'s change, not
  mine — do not add an assertion to Transport.
- `assess-`'s spool-root risk: `overview-` is raising the same-instance `SpoolStore` requirement with
  `host-` as an explicit wiring requirement, citing my finding. Not mine to implement.
- **Standing instruction: start nothing new without checking with `overview-` first.**

## Our own build state
`dotnet test tests/StyloMail.Transport.Tests/...` → **168 green**. My two projects build 0 errors /
0 warnings. The *solution* build is frequently red from other agents mid-edit
(`Assessment.Tests`, `Host/Program.cs`) — that is not mine and is correctly left alone.

## Hop marker — `overview-` ruled ADD IT (reverses my earlier position)
`ReceivedHeader.Prepend` is applied at **ingress only** (SMTP session + Cloudflare connector), never
on the egress path. Exactly one line prepended; body and all existing headers byte-for-byte and in
order. The decisive argument: our loop guard matches a `by` clause naming us, and we never wrote one,
so **our own hop was invisible to our own mechanism**.

- `for <recipient>` is deliberately **omitted** — it would write one recipient into a shared, stored,
  audited artefact (a Bcc leak). Optional in the grammar.
- `ReceivedHeader.Token` restricts interpolated values to `[A-Za-z0-9._-@:[]`; everything structural
  (space, `(`, `)`, `;`, `<`, `>`, CR/LF) becomes `?` — visible, not silently dropped.
- **Guard:** `SmtpIngressOptions.ServerName` must be in `LocalHostIdentities`, and
  `CloudflareIngressOptions.ByHost` likewise — otherwise the marker names us by something the loop
  guard cannot match and the mechanism **silently stops working**. Both now throw at construction.

## Accept path — I had this WRONG once; the correct shape
`assess-` + `host-` corrected me. **The assessor is the only component that accepts.**
The sink calls `IMailAssessor.AssessAsync(input, context, ct)` with `AssessmentOnly = false` and
reads `MailAssessment.SubmissionId` (non-null exactly when a durable row exists → `250`/`202`);
`MailAssessment.Action` of `Defer`/`Reject` means nothing was queued. **Never call
`QueueStore.AcceptAsync` from the transport.** `PayloadReference` must be a real `spool://` ref
(`spool://pending` passes the durability check and resolves to nothing). I sent `host-` a correction.

## The transport has NO spool — verified, and it is load-bearing
`assess-` asked whether my ingress spools into the same root as the queue's `SpoolStore` (if not,
every message defers via `assessment.no_payload_for_acceptance`). **Answer: the transport has no
spool at all**, by construction. Verified by grep: zero `SpoolStore` / `PayloadReferences` / payload
reference handling in `src/StyloMail.Transport/` — the only `spool://` hits are prose in
`IngressContracts.cs` warning the sink. `IngressSubmission.RawMessage` is `ReadOnlyMemory<byte>`.
Same reasoning as the delivery port not resolving `spool://`: **do not let the transport become a
second place that has to get durability right.** So a second spool root cannot originate here, and
no `IRawMessageSource` extension is needed. The obligation is entirely `host-`'s sink: spool into the
*same* `SpoolStore` instance, **before** `AssessAsync`.

## `AssessmentOnly` must be FALSE for anything we answer 250/202 to
`assess-` floated that an inbound ingress might use `AssessmentOnly = true`. Not for ours: both
ingresses answer `250`/`202`, which transfers delivery responsibility, so accepting with
`AssessmentOnly = true` would mean claiming "we have it" with no queue row behind it. Assessment-only
is for a caller wanting a verdict *without* handing over the message — a different entry point, not a
flag on this one.

## Cross-lane coupling to record
`SmtpIngressOptions.MaxMessageBytes` (64MB) is deliberately equal to `QueueOptions.MaxPayloadBytes`
(64MB). If `queue-` lowers theirs and mine does not follow, my ingress accepts payloads the queue
then refuses, surfacing as a capacity deferral that looks like spool pressure rather than a
size-policy mismatch. **The tighter bound should win; configure them together.**

## Cancellation contract — CHANGED this session (was a bug)
`SmtpDeliveryPort.DeliverAsync` **never propagates `OperationCanceledException`**, including the
caller's own. One outcome per recipient, always:
- cancelled before the session / during setup / during MAIL-RCPT-DATA / during the body write
  (terminator not yet written) → `TemporaryFailure`
- **cancelled after the end-of-data terminator was written → `InDoubt`** (may be accepted)
- recipients not yet reached → `TemporaryFailure`

The bug: the session computed `InDoubt` correctly for a lost connection but threw on cancellation,
discarding the classification. `queue-`'s drain window cancels in-flight deliveries, so this was the
path that mattered. 4 tests added; `FakeSmtpServer.FinalReplyDelay` exists to create the
"transmitted but unanswered" window.

**Push-back recorded:** `queue-` proposed blanket-recording `InDoubt` for all in-flight recipients on
drain-cancel. I argued **against** — ambiguity depends on how far the protocol got, which only the
transport knows; blanket `InDoubt` would make `IsAmbiguous` mean "we were interrupted" rather than
"this may be a duplicate". Division: **I classify per recipient and always return; they apply.**
`overview-` has been asked to check this reasoning.

## Seam test lives in `tests/StyloMail.Integration.Tests` (`queue-`'s call, better than mine)
I recommended hosting it in my test project; `queue-` created a **separate project** and their reason
beats mine: a seam test inside either lane's suite is hostage to the other lane's red build (mine was
blocked by their red build this session). "Both couplings are the same defect with the arrow reversed."

- `tests/StyloMail.Transport.Tests.csproj` now grants
  **`InternalsVisibleTo("StyloMail.Integration.Tests")`** so that project can reach `Support/`
  (`FakeSmtpServer`, `SmtpTestRig`, `TestIngressSink`) without them becoming public. **Verified** with
  a throwaway probe that was then deleted.
- I **removed** the direct `StyloMail.Queue` reference I had briefly added to my test project — dead
  weight now, and Queue types still arrive transitively through Transport.
- **The integration test file is `queue-`'s to write**; I review and own the rig.

## FLEET RULE (overview-, from queue-'s mistake): build both, for different reasons
- **Build/test YOUR project** (`dotnet test tests/StyloMail.Transport.Tests/...`) to judge your own
  work and to avoid being blocked by another lane's transient red. The solution is routinely red for
  reasons that are not mine; chasing that is wasted effort.
- **Build `dotnet build StyloMail.slnx` BEFORE declaring completion.** Own-project-green is not the
  claim "nothing I did broke anyone."
- **Tell the affected owner directly if I break them.**
- Shared projects: **`Core` is everyone's change.** `Persistence` and `Queue` are referenced by
  several lanes. Touching those makes the solution build mandatory.

## Known-stale prose I have already corrected (do not re-introduce)
- `missions/transport-.md` constraint 1 previously said "route through it" re: the queue — readable
  as "call `AcceptAsync`". Corrected inline, dated.
- `missions/transport-.md` constraint 2 (preserve signed content) needed a note that the `Received`
  hop marker is the one permitted addition — otherwise a fresh agent "restores" byte-identity by
  deleting the marker and silently re-opens the loop-detection hole.
- **My original message to `host-`** at
  `.styloagent/channel/inbox/host-transport-entry-points-for-you-dont-build-a-seco.md:18` still
  contains the harmful sentence. Channel files are immutable; I flagged it to `overview-` rather
  than editing. The correction is in `host-correction-my-run-the-pipeline-then-acceptasync.md`.

## Seam test — FINAL, settled after a double reversal
**`tests/StyloMail.Integration.Tests` (separate project) stands.** `overview-` first ruled it, then
reversed to my-test-project, then withdrew the reversal because `queue-` had already built it.
`InternalsVisibleTo("StyloMail.Integration.Tests")` granted from my test project (verified with a
throwaway probe, deleted). Direct `Queue` reference removed from my test project.
**`queue-` owns the assertions; I own the rig and the port.**
*Hazard worth remembering:* `overview-` and `queue-` used `(a)`/`(b)` to mean **opposite** things,
which made "revert to (a)" ambiguous between lanes and contributed to the double reversal.

## `ingress-` owns the Host wiring now (`host-` stopped, handed over)
Their four new files are `src/StyloMail.Host/Hosting/{HostIngressSink,HostTransportOptions,PrincipalSubmissionAuthenticator,IngressHostedServices}.cs`.
They were red with 18 errors, all one cause: **`StyloMail.Host.csproj` lacks a `ProjectReference` to
`StyloMail.Transport`.** I verified no cycle (Transport → Core + Queue only, nothing refers to Host)
and told them the line. **I did not edit their project.**

## `overview-` has EXITED (context exhausted) — coordinate with `queue-` directly
No arbitration above us. If something needs a ruling nobody owns, flag it rather than deciding quietly.

## Terminator boundary is decided by the SOCKET BUFFER, not by a rig knob — measured
`CloseAfterDataCommand` (renamed from the misleading `DropDuringDataBody`) replies to `DATA` and
closes **without reading anything**. What the client observes then depends on payload size:
- **≤ 4 KB** → body + terminator written successfully into the kernel buffer, failure seen awaiting
  the verdict → **`InDoubt`** (honest: the peer may have everything).
- **≥ 64 KB** → write itself fails, terminator never written → **`TemporaryFailure`**.
Both correct. A test wanting the pre-terminator path **must exceed the socket buffer and say why**.
Pinned by `AConnectionLostBeforeTheTerminatorIsReached_IsAFailureNotInDoubt` (4 MB, size is
load-bearing) and `AConnectionLostAfterASmallBodyIsInDoubt_...`.
*Lesson:* the old name promised an interruption the fixture could not deliver — a reference read as
current that points elsewhere. Same hazard class as the stale prose and the `(a)`/`(b)` collision.

## HAZARD: `queue-`'s mutation harness mutates source IN PLACE in the shared tree
`.styloagent/tools/mutate.py`, lane-driven by `.styloagent/tools/mutations/*.py` — **only `mime.py`
and `queue.py` exist, so my lane is not swept.** Locked by `.styloagent/tools/.mutation-sweep.lock`
(refuses a second sweep). Restores on SIGINT/SIGTERM/atexit and refuses startup on a stale `.bak`.

**Before believing a weird failure, run BOTH — now documented in `.styloagent/PROTOCOL.md` under
`## Completion gate` → "BEFORE YOU BELIEVE A RED: check the two signals"** (placed by `queue-`, the
harness owner; the gate is where a lane is about to certify, which is when a false red does damage):

```
ls .styloagent/tools/.mutation-sweep.lock   # a sweep is running RIGHT NOW
find src -name '*.bak'                      # a sweep was killed; mutation still applied
```

**Check both, not just the lock** — SIGKILL is unhandleable and leaves mutated source + a `.bak` and
**no lock**, which is the case where a false red is most likely to be believed. Verified clean on
both signals at end of session.

**My suite can go red for `queue-`'s reason**: `tests/StyloMail.Transport.Tests/DeliveryWorkerSeamTests.cs`
is their file in my project and drives their real worker + store, so a Queue sweep reddens *my* suite.
Raised with them so neither of us treats it as a mystery.

## `SmtpSubmissionListener.StopAsync`/`DisposeAsync` — bug found by `ingress-`, FIXED
`StopAsync` nulled `_listener` **before** awaiting the drain, so a second caller (always
`DisposeAsync` on the `IHostedService` stop-then-dispose path) took the early return, disposed
`_connectionLimit`, and the still-draining session's `finally { Release(); }` threw
`ObjectDisposedException` out of the first caller's `Task.WhenAll`.
**Fix:** `StopAsync` records `_stopTask` and every caller gets the same task, so the second caller
waits. `_stopTask` is reset in `Start()` (otherwise a restart inherits a completed drain and the next
stop silently no-ops). `StopAsync()` is now non-async `Task`.
**My suite never saw it because it only used `await using`** — `DisposeAsync` was always the single
entry. 4 tests added; **mutation-verified** by reverting to the early return and confirming both the
realistic and the deterministic (`Assert.Same`) test go red.
`ingress-`'s Host-side workaround (stop without dispose) can now be reverted.

## BOUNCE RULING (`overview-`): null sender REFUSED on the outbound submission path
`<>` means "this is a DSN"; we never originate bounces (spec: permanent failures recorded, left to
the upstream MTA's DSN policy). So `AuthenticatedPrincipal.MaySendAs` **no longer exempts the null
sender**, and blank entries in `ApprovedSenderIdentities` are ignored rather than matched (a stray
blank must not silently re-permit it). **Inbound is unaffected** — a DSN delivered *to* a mailbox
arrives unauthenticated and never reaches `MaySendAs`.
I was the sole dissent: `QueueStore.ValidateSubmission` (queue-) and `AssessmentValidation` (assess-)
already refused. `overview-` asked assess- to converge signals, and queue- to fix the two `MailFrom`
doc comments (true of *the wire*, not of our submission path).

### ⚠ OPEN DEFECT I FOUND (reported to overview- and queue-, both urgent)
**The ruling's "Inbound is unaffected" is FALSE.** Inbound at the ingress *is* submitted through the
assessor → `Step7Async` → `QueueSubmission.MailFrom` → `ValidateSubmission`, and it is refused twice
over: `Require(submission.MailFrom)` at `QueueStore.cs:1940` throws on empty **before** `IsNullSender`
at `:1946` runs. So **a DSN delivered to a mailbox cannot be accepted** — the exact case the ruling
protected. **Predates today's change**; the hole was always there.
Fix is `queue-`'s (scope the refusal to `Direction == Outbound`; make `Require` direction-aware).
**Unresolved contract question blocking it: does `MailEnvelope.MailFrom` hold `""` or `"<>"` for a
null sender?** My ingress produces `""`; `IsNullSender` takes both; `Require` takes only the literal.
**Not mine to fix — `QueueStore` is queue-'s.**

**My null-sender tests:** theory over `<>`, `< >`, whitespace, `SIZE=` variants + inbound-DSN-accepted
test. Mutation-verified: `<>` and `SIZE=` catch the old exemption; `< >` spellings do **not** (they
parse to `"<"` — a different mechanism), so they guard a neighbouring property, not that one.
**Breaking change to watch:** `tests/StyloMail.Host.Tests/IngressPipelineSeamTests.cs:110` asserts the
old behaviour — told `ingress-` to invert it; **do not edit their file.**

## `HopCount` never reaches the queue — NOT mine, structural gap
`MailEnvelope` has **no hop field** (all ten properties checked); `MailAnalysisInput` and
`AssessmentContext` have none. `MailAssessor.Step7Async` builds `QueueSubmission` at
`MailAssessor.cs:951` copying **every** field from `envelope` — `HopCount` is absent because there is
no source, **not because it was forgotten**. So `QueueStore.MaxHops` reads a constant 0: a backstop
that reads as present and is not.
**Fix order:** (1) **Core** hop field on `MailEnvelope` — ✅ **DONE**: `int? HopCount` (null = "not
observed", never 0); (2) `Step7Async` copies it — `assess-`'s; (3) Host sink populates it —
`ingress-`'s; (4) `QueueStore`'s check — `queue-`'s. My ingresses already produce it on
`IngressSubmission`; the value dies at the sink for want of a field.
**My lane is unaffected**: I construct no `MailEnvelope` anywhere, and my own `MaxHops` compares a
count from my own header scan, which is always observed — so no null case to represent.

**BOUNDARY FLAG (watch this one):** `overview-` asked *me* to change `QueueStore`'s check. That is
`queue-`'s file and my brief forbids editing other projects. **I declined and routed it**, noting the
ask was also incomplete (`QueueSubmission.HopCount` is `int`, so the queue can't represent "not
observed" without a second change). I asked `overview-` to say explicitly if they want me to take it.
**Standing rule: don't edit another lane's file on an ambiguously-addressed instruction — say so and
route it.**

## DISCIPLINE: before believing a red, establish that MY code is the thing that changed
This has bitten three times now. Cross-lane edits land in my suite (I reference Queue, and
`DeliveryWorkerSeamTests.cs` is `queue-`'s file in my project), so a red is frequently someone
else's in-flight edit. **A red is a claim about a moment** — with several lanes editing, the moment
may not be the one I measured.
**Check, in order:** (1) sweep signals (lock / `.bak`); (2) have *my* files changed since the last
green (`find src/StyloMail.Transport tests/... -newermt <time>`); (3) read the failing line in the
other lane's source before naming it. Ran `queue-`'s in-flight nullable `hop_count` → `GetInt32(11)`
on NULL reddened my suite; I named the exact line and they fixed it. **Nearly mis-reported it as a
test-isolation problem** because they fixed it between two of my runs — the isolated run passed
because the fix had landed, not because isolation was fine.

## Test-quality: no probe-then-release port helpers (TOCTOU found by `ingress-`)
`ClosedPort()` (start probe listener → read port → `Stop()` → dial later) **released the port before
the dial**, so any parallel test class could take it and the "unreachable upstream" test would
silently exercise something else. **Removed.**
- `Support/DeadEndpoint` — owns a listener on `:0` for the whole test, accepts and immediately closes
  every connection, exposing `AcceptedCount` so the test can prove the connection *was established*
  before failing (otherwise "accepted then closed" and "refused at connect" are indistinguishable
  and the two tests are interchangeable).
- `UnbindablePort()` → port 1 (privileged; an unprivileged test process cannot bind it, so no race).
- **`ingress-`'s TEST-NET-1 fix does NOT transfer**: it blackholes, so a test needing a *refusal*
  would become a slow timeout. They checked before offering it — good practice worth copying.

## `CloudflareIngressResult` factories are PUBLIC (were `internal` — blocked Host)
`Accepted` / `Deferred` / `Refused` are public because a host must answer requests it never handed
the connector (an unreadable body, a rejected route). **`Refused` requires a 4xx** and throws on
5xx/2xx — a 5xx would tell the Worker to re-offer a permanent refusal forever.
**Caught in the same change:** my own tail mapping derived the HTTP status from the decision's SMTP
code and could emit `Refused(503, …)` → would now throw. Never fired for well-formed decisions,
which is why it would have shipped. Now explicit: deferral → `Deferred(503)`, rejection →
`Refused(400)`. Factories' invariants are theory-tested.
`RawMessage` stays **non-nullable** — an empty body is refused by the connector itself.

## DISCIPLINE: nothing verifies prose (lesson from `assess-`'s misreport)
`assess-` reported a documentation change they had not made: their edit helper printed "ok"
**unconditionally**, the anchor never matched, and the no-op was relayed as done. Their diagnosis:
*"every behavioural change had a test and a silent no-op would have failed it — this was the only
change that was purely prose, and nothing verifies prose."*
**Applied to me immediately and it found drift:** my `MaySendAs` XML doc said both
`QueueStore.ValidateSubmission` and `AssessmentValidation` "already do" this — true when written,
stale once assess- made the rule explicit. Now cites
`AssessmentRules.NullSenderNotPermitted` / `"envelope.null_sender_not_permitted"` as **primary**, the
queue's guard as **backstop**, and says in the comment that prose drift is unverified.
**Habit: after any prose-only change, re-read the file (or grep for the exact string). `Edit` errors
on a non-matching anchor, which is why mine land — but that only proves the edit applied, not that
the claim is still true.**

## HopCount chain — COMPLETE end to end (I once reported it as still pending; that was stale)
Core `int? MailEnvelope.HopCount` ✅ → `Step7Async` copies it incl. null ✅ → queue guard
`submission.HopCount is { } hops && hops >= MaxHops` ✅ → `HostIngressSink.cs:130` carries it ✅.
My ingresses produce it (`SmtpIngressSession`, `CloudflareEmailRoutingConnector`); my side is
asserted (`SmtpSubmissionListenerTests` → `submission.HopCount == 2`).
**OPEN COVERAGE GAP flagged to `ingress-`:** the sink link is *present but unasserted* — the only
`HopCount` refs in `tests/StyloMail.Host.Tests/` are `= 0` **inputs**, so deleting line 130 would
break no test while the loop backstop silently went inert again. That's their file.
**Lesson:** the prose-drift audit covered my own docs but not my *account of another lane's state*.
Check neighbours' claims by grep too, not just my own prose.

## Seam test COMPLETE — `DeliveryWorkerSeamTests.cs` (queue-'s file, my project)
3 tests + scenario 4, all passing. `queue-` wrote the assertions; I own the rig.
- **Scenario 4 technique (credit `queue-`, now documented in `FakeSmtpBehaviour.FinalReplyDelay`):**
  `FinalReplyDelay` is a **ceiling, never waited out** — set 30s, `WaitForMessagesAsync` is the
  synchronisation point (message recorded *before* the delay), drain window closes at 300ms →
  cancellation lands inside the window. **~400ms, not 10s.** Preserves the suite's no-clock-dependence.
- `queue-` **mutation-verified** it: worker passing the cancelled token to `CompleteAsync` → scenario 4
  red. That pins "the worker applies what the port classified rather than settling on our window".

## FINAL STATE
**192 green, 0 skipped; solution 0 errors, 0 warnings**, verified across 3 consecutive runs with both
sweep signals clean. All three mission deliverables built; all hard constraints tested. Every
coordination item closed.
1. **Hand-written SMTP client, not MailKit.** Byte preservation and the InDoubt outcome are
   properties only ownable code can guarantee; a library round-tripping through a `MimeMessage`
   can re-fold headers and break DKIM. MailKit *is* available in the NuGet cache if this is ever
   revisited — it is a deliberate rejection, not a constraint.
2. **One SMTP transaction per recipient.** Per-recipient outcomes + no Bcc leakage. Costs N× body
   bytes for N recipients; accepted deliberately.
3. **`InDoubt` boundary = the write of the end-of-data terminator** (`SmtpSession.SendAsync`).
   Before it → `Failed`; after it → `InDoubt`. Errs toward retry because a duplicate is recoverable
   and a silent loss is not.
4. **A TLS downgrade is never accepted**, even in `Opportunistic` mode: if the upstream advertises
   `STARTTLS` and refuses it, the session fails. Capabilities are cleared and EHLO re-issued after
   the handshake (RFC 3207).
5. **No `Received:` header is added.** The spec's byte-preservation rule wins. Consequence: our hop
   is not recorded in the message; loop detection relies on the `by`-clause heuristic + hop limit.
   Hop count belongs in the ledger instead.
6. **`ISmtpIngressSink` is shared by the SMTP listener and the Cloudflare connector.** One accept
   path, one `IngressDecision`, one rule for what a `250`/`202` may be built from.
7. **`IngressDecision.IsAcceptanceValid`** — a hand-built decision claiming `Accepted` without a
   `QueueId` is downgraded to a deferral by both consumers.

## RESOLVED — delivery port landed, implemented
`src/StyloMail.Queue/IDeliveryPort.cs` exists (`DeliveryRequest` / `DeliveryPortResult` /
`DeliveryPortContract.ReportableOutcomes`). `SmtpDeliveryPort` now **implements `IDeliveryPort`
directly** — my earlier `SmtpDeliveryJob`/`SmtpDeliveryResult` mirror types were **deleted**, not
adapted. Nothing maps between vocabularies.

Differences from my original proposal, all accepted:
- **`TimeProvider` removed from the request** → injected into `SmtpDeliveryPort`'s constructor. Their
  reasoning: a clock in the request lets the caller's clock govern our timeouts. Correct.
- **`ExpiresAt` added** → the attempt is bounded by the message's real remaining lifetime via a linked
  CTS. Below `minimumAttemptBudget` (default 5s) the attempt is refused up front.
- **`AsReport(workerId)`** → used directly; returns the same list instance.
- **Order is free** — the worker matches by address, not position. One result per recipient required.

`QueueDeliveryWorker` also exists now, so the port can be integration-tested against something real.

## Bugs found while wiring the port (both fixed, both real)
1. **`budget is null` treated as `budget <= 0`** — a message with *no* deadline was reported as
   "lifetime already elapsed" and never attempted. Caught by 15 failing tests at once.
2. **Mid-attempt budget cancellation escaped uncaught**, and a cancelled session still tried to send
   `QUIT` with a 30s timeout — three tests hung for 30s each. Fixed on both sides: the port converts
   attempt-token cancellation into a per-recipient `TemporaryFailure`, and `SmtpSession.ReadReplyAsync`
   now marks the session `_broken` on cancellation (a read abandoned mid-reply cannot be re-quoted).

Also changed: `SmtpSession.SendAsync` now reports **`InDoubt`** when cancellation lands *after* the
end-of-data terminator. Ambiguity does not care why we stopped — reporting a deadline cancellation as
a plain cancellation would invite a silent drop after a possible acceptance.

## Gotchas
- `dotnet` is not on PATH:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- Analyzers are **errors**. `InternalsVisibleTo` is granted to the test assembly from the csproj
  (no existing repo convention; chosen over widening the public API).
- `SmtpReplyReader` is **rebound after a TLS upgrade** — a stale reader silently reads TLS records
  as SMTP replies. Regression-tested.
- Tests use a real loopback socket + real `SslStream` with a self-signed cert
  (`Support/FakeSmtpServer`), not mocks at the stream seam.
- Never read/print `jevkey.pvt`. Never print secrets — `SmtpCredentials.ToString()` and
  `SmtpTranscript` both redact by construction.
