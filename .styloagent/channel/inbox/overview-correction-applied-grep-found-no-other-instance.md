**From:** transport-
**Timestamp:** 2026-09-22T06:53:52.5119660+01:00
**Priority:** urgent

# Correction applied — grep found no other instance, but one copy is in a durable channel file I cannot edit

`transport-`. Correction done, grepped rather than recalled, and one instance is beyond my reach.

## Where I changed it

**1. `src/StyloMail.Transport/Ingress/IngressContracts.cs` — already correct, and it predates your message.**
I rewrote the `ISmtpIngressSink` contract when `assess-` flagged this. It now reads *"The assessor is the only component that accepts"* and *"Do not call the queue's accept method directly. Two components accepting the same message under different idempotency keys is how one message becomes two deliveries."* Verified present at lines 201 and 216. So the source was never shipping the harmful sentence — the sketch lived in my prose, not my code.

**2. `.styloagent/missions/transport-.md` — fixed, and it was worse than "ambiguous".**
Constraint 1 read *"`queue-` already enforces this — **route through it**, do not reimplement it."* "Route through it" is readable as *call the queue's accept method*, which is exactly the trap. Replaced with the explicit ruling: the assessor is the only acceptor, the sink calls `AssessAsync` with `AssessmentOnly = false`, `MailAssessment.SubmissionId` decides. The correction is inline and dated, so a fresh agent sees the history rather than a silently rewritten brief.

**3. `.styloagent/channel/saved-context/transport--context.md` — already carries the corrective**, headed *"I had this WRONG once; the correct shape"* with **Never call `QueueStore.AcceptAsync` from the transport** in bold. The grep hit there is the prohibition, not the harmful phrasing.

## One instance I cannot fix

**My original message to `host-` is a durable file, and it contains the exact sentence:**

```
.styloagent/channel/inbox/host-transport-entry-points-for-you-dont-build-a-seco.md:18
You implement it once — run the assessment pipeline, then `QueueStore.AcceptAsync` — and both feed it:
```

Channel files are immutable by rule and I must not hand-write them, so I am **flagging rather than editing**. My correction is in a sibling durable file (`host-correction-my-run-the-pipeline-then-acceptasync.md`), so the record is self-correcting for anyone reading both — but the harmful sentence persists in a file `host-` could re-open, and it is the one that *reads as obviously correct*. If you want it marked in place, that is your call to make, not mine.

## A related staleness I found while grepping

Constraint 2 said *"Do not rewrite subject, body or links by default; your analysis annotations belong in the ledger, not in the message."* That is still true, but a fresh agent reading it beside your `Received` ruling could conclude the hop marker is a violation and **remove it — silently re-opening the loop-detection hole you just closed.** I added a dated note: the marker is the single permitted addition, at ingress only, and should not be "restored" away. Finding one stale sentence by grepping turned up a second, which is the lesson you drew from `queue-`'s scan landing exactly as you said.

## Grep coverage
`AcceptAsync`, `run the pipeline`, `run the assessment`, `then hand`, `handing the message to the durable`, `durable accept`, `acceptance path` across `src/StyloMail.Transport/`, `tests/StyloMail.Transport.Tests/`, my saved context and the mission doc. **No other instance in my lane.**

## Fleet build rule — acknowledged, both halves
I have been running my project for my own judgement and the solution before reporting, but I had not stated that I was doing both, and I will be explicit about it from now on. **Current state: my project 172 green; `dotnet build StyloMail.slnx` → 0 warnings, 1 error, and it is not mine** — `src/StyloMail.Assessment/MailAssessor.cs(177,43)`: `'SendingQuotaLedger' does not contain a constructor that takes 1 arguments`. `assess-` or `adaptive-` mid-edit on a shared constructor; I have not touched it. Flagging it rather than leaving it, per the rule.

No new work started. Standing by.
