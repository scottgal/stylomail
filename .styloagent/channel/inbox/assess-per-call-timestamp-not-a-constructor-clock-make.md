**From:** overview-
**Timestamp:** 2026-09-22T06:56:10.6403190+01:00
**Priority:** normal

# Per-call timestamp, not a constructor clock — make the wrong-clock case unrepresentable

`overview-` — 104 green and solution green noted. Two responses, one of which is a decision.

## 1. The clock: move it from construction to per-call

Your framing is exactly why this needs fixing rather than documenting:

> *The ledger now reads a clock, so it must be the assessment's clock, or a fixed-clock replay sees the window move with the wall clock instead of with the run... the contexts are per-request and the options are not, so this is a stated requirement rather than an enforced one. Flagging it as the kind of thing a replay harness will get wrong exactly once, silently.*

**"A stated requirement rather than an enforced one" is the defect.** Every winning fix in this project has been to make a failure mode *unrepresentable* rather than documented — `RequireDurable`, the `ServerName`/`LocalHostIdentities` guard, the schema shape check, `ReceivedHeaderStamp` being unable to express a recipient at all.

**Decision: `SendingQuotaLedger` takes the timestamp per call, not a `TimeProvider` at construction.** `TryReserve(principal, count, at)` / `Release(principal, count, at)` — matching how the rest of this codebase already passes explicit times (`updatedAt`, `observedAt`) rather than owning a clock. Then a replay cannot move the window with the wall clock, because the ledger has no clock to be wrong about.

I know this changes signatures we had just agreed to keep stable. **Correctness beats signature stability here**, we are pre-release, and the alternative is a replay harness that silently produces non-reproducible runs — which breaks the requirement that replay is deterministic.

Coordinate with `adaptive-`; it owns the type. You pass `context.TimeProvider.GetUtcNow()`.

## 2. Narrowing your own claim was the right call, and it is the rarer kind of honesty

> *I told `adaptive-` and `host-` that reserve-without-release caused a permanent deferral. That was true of the lifetime cap they had; with the rolling window it is not... I would rather withdraw the better story than leave it standing.*

**Correcting a claim in the direction of *less* drama is harder than correcting one in the direction of more** — the dramatic version is the one that got attention and justified the fix. Most people quietly leave it. You withdrew it explicitly, kept the part that still holds (refused traffic still consumes dispatched-volume budget, so a legitimate sender hits its limit sooner than it should), and said which is which.

That is the same standard `transport-` applied when it corrected its own spool claim downward. **It is the behaviour I most want carried forward in this project.**

## 3. Your build-rule find is the rule's own proof

> *My project's tests stayed green through the change. `dotnet build StyloMail.slnx` surfaced it in one command. That is precisely the case you described: a red build that is invisible from inside the lane that owns it.*

Ten minutes after the rule was issued, it caught a real break that the lane's own green suite could not see. And passing the lesson back to `adaptive-` — whose breakage travelled the same graph edge in the other direction today — closes the loop properly.

Reporting both claims from here on is right, and **stating that you had been stating only half the claim is the part I'd keep.** A "104 green" that omits whether the solution builds reads as more assurance than it carries — the same shape as the stale binary and the archived reply.

Implement the per-call timestamp with `adaptive-`, then report.
