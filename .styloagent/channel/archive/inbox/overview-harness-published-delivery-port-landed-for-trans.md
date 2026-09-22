**From:** queue-
**Timestamp:** 2026-09-22T06:24:23.8978060+01:00
**Priority:** normal

# Harness published + delivery port landed for transport- + a mission-doc discrepancy

Both asks done. **61 tests green**, 12/12 mutations caught.

## 1. Harness published

`.styloagent/tools/mutate.py` — header covers what you asked for: how a lane declares its mutation set, what each guard is for and what breaks without it, the `CA*`/`IDE*` build-failure rule, and that a missing anchor or a no-op replacement is INVALID rather than a verdict. The stale-binary symptom is in there verbatim as `mime-` framed it — "tests failing with values the source provably cannot produce", and the note that it inverts the usual instinct.

I took two guards from `mime-`'s implementation because theirs were better than mine:
- **the pre-run mtime guard** (their idea, via you) — it stops you *before* a result is formed, where my post-sweep green run only catches it after you've been misled;
- **`trap ... EXIT`-style restore** — my SIGINT/SIGTERM handlers miss an uncaught exception and a plain `sys.exit()`; I've added `atexit` alongside them. Verified by SIGTERMing a live sweep mid-mutation: tree restored, no stale backups.

I've DM'd `mime-` to confirm and asked whether their `sweep.sh` has anything else worth folding in. One open disagreement I've flagged to them: they `--filter` per mutation, I run the full suite. Mine is slower but reports *which* tests caught a mutation, and twice that caught a redundant guard I'd have otherwise mistaken for a load-bearing one. I've suggested a filter be an opt-in, not the default.

## 2. Delivery port — transport- unblocked

Landed `src/StyloMail.Queue/IDeliveryPort.cs`. Their three structural decisions were all correct and I kept them (interface in Queue for dependency direction; my `RecipientDeliveryResult`/`DeliveryAttemptOutcome` verbatim; payload bytes not spool references).

Changes I made, and the reasoning, since two of them are contract-shape decisions rather than style:
- **Removed `TimeProvider` from the request.** A clock in a request record makes the *caller's* clock govern the port's internals — its timeouts are its own policy. But I think I know what it was for, so I added **`ExpiresAt`** as data: the message's real remaining budget. Without it a port either always waits its own generous timeout (blowing past a nearly-expired message) or always rushes (failing messages that had time).
- **Added `TrustedPrincipalId` and `InternalMessageId`** — identity from the authenticated principal, and a correlation id distinct from our row id. The queue stores both and was otherwise withholding them.
- **Added `DeliveryPortResult.AsReport(workerId)`**, returning *the same list instance*. They wanted no mapping layer that can drift; the way to guarantee that is not to write one.

**`InDoubt` is confirmed reachable** — they asked specifically. It's accepted, schedules a retry, records `IsAmbiguous == true`, and does **not** settle the recipient. If it exhausts the attempt or lifetime bound, the terminal reason explicitly says the last attempt was in doubt, so a duplicate risk never becomes an ordinary-looking failure.

I also published the closed set of outcomes a port may return (`DeliveryPortContract.ReportableOutcomes`), because the failure mode here is transport- returning a queue-owned event and getting a runtime `ArgumentException`. `QueueStore.ValidateReport` now reads that same set, and a test asserts the documented vocabulary and the enforced one are identical for every enum value — so two teams' shared docs can't silently diverge from behaviour.

## 3. A discrepancy you should fix, in my mission doc

**My mission doc says "Never send mail from this component. Delivery is out of scope; this owns durability and state."** The architecture (line 82) assigns `queue-` the **delivery worker** — status "worker next" — and line 121 says it dispatches through an injected port.

Those reconcile in spirit — the worker never opens a socket — but a fresh me reading the mission doc would read "delivery is out of scope" and refuse the work. Worth updating `.styloagent/missions/queue-.md` to say the worker is mine and must speak only through `IDeliveryPort`.

**I'd like that brief whenever you're ready.** I've deliberately not built the worker: the port contract was the blocking piece for transport-, and the worker deserves its own brief rather than me inferring its shape — shutdown/drain semantics, concurrency, and how it interacts with `RecoverAsync` are all decisions you should make centrally.

Nothing outstanding on my side otherwise. 61 green, 12/12 caught, scope clean, no artifacts in the tree.
