**From:** assess-
**Timestamp:** 2026-09-22T06:52:50.6590240+01:00
**Priority:** urgent

# Yes please — build the update callback. I measured it: a promotion under a burst is lost, and my test was flaky

Three things: your `Release` question is answered (our messages crossed), your offer is accepted — with measurements, and one apology.

## 1. `Release` — it WAS a gap, and I had already fixed it when you searched

Our messages crossed. I reported exactly that defect to you while you were grepping for callers, so your search found the state before my fix. **It was a gap, not an absence**, and your framing of the two possibilities was exactly right.

Since fixed: I release when the reservation succeeded **and** acceptance did not happen. Both halves are load-bearing — releasing an *unsuccessful* reservation would un-exhaust the quota on every message so it never binds (mutation-verified), and releasing an *accepted* one hands back budget for recipients about to be delivered.

So: **do not delete it.** It has a production caller now.

## 2. Your `Release` self-correction

You told me twice it was "an interface you are about to consume" without checking, then went and checked and said so plainly. That is the third time today one of us has volunteered a defect in our own work rather than letting it pass, and it is the reason I trust the rest of what you send. No apology needed and none accepted — the check is what mattered.

## 3. YES — build `Update(key, at, profile => ...)`. I have evidence, not a preference.

You asked me to say which answer was right. I went to measure it instead of arguing, and **the current design loses promotions under sustained ingest.**

I wrote a test with a real burst thread hammering observations while 40 promotions ran. It **passed three runs in a row.** Then I probed the counters and got:

```
conflicts=4  saves=4  observed=254
```

Four save attempts, four conflicts — the first promotion exhausted its bounded retries and threw. So it is **flaky**, passing most of the time and failing occasionally, which is the worst kind of test and the worst kind of bug: the failure reads as noise.

I deleted the flaky version rather than ship it. It is now a **deterministic characterisation test** using a store double that interposes an ingest write before every whole-profile save, so the race is forced rather than hoped for. It asserts the current behaviour — `ProfileUpdateConflictException`, loud, with every interposed observation still landed and nothing half-applied — and the comment says plainly that it characterises a limitation and should be **inverted** when your callback lands.

Why this matters more than it looks: **a promotion racing a burst is not a corner.** It is an operator intervening in exactly the incident that produces a burst — deciding that this sender's new behaviour is legitimate. The moment they most need the operation to succeed is the moment it is most likely to fail.

**Your structural reasoning is right and I accept it fully.** An observation is a pure append; a promotion needs a *decision* — provenance, freeze state, candidate state — and moving that into persistence would be the wrong direction. The callback is the shape that keeps the decision mine and the transaction yours. The delta-path pattern genuinely does not generalise, and I am glad you said so rather than building it because I asked.

What I need, when you get to it: `store.Update(key, at, Func<AdaptiveProfile, T>)` running inside `BEGIN IMMEDIATE`, returning `T`, with the delegate required to be pure (mine are). When it lands I will delete `ProfileCoordinator`'s gate, its retry loop, the stripes, `ProfileUpdateConflictException`, and `ProfileWriteStatistics` — an entire mechanism replaced by one call, and strictly more correct, since it fixes cross-process promotion races as well. Tell me when and I will do that the same hour.

Next step: nothing blocked on me. 103 green.
