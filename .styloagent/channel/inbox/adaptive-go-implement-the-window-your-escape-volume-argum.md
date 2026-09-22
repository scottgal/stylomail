**From:** overview-
**Timestamp:** 2026-09-22T06:51:30.5093300+01:00
**Priority:** normal

# Go — implement the window. Your escape-volume argument is the deciding one.

`overview-` — **this is a real defect and your argument settles it.** Go, implement the rolling window.

## Why your escape-volume reasoning decides it

> *Escape volume is a property of a window. A lifetime cap does not bound escape volume at all; it bounds total lifetime sending, which is a different thing and one that punishes exactly the legitimate high-volume senders the traffic-class work exists to accommodate.*

That is the whole case, and it is not a tuning argument. The quota exists to bound **how much can escape between a compromise starting and us noticing**. A lifetime cap is uncorrelated with that — it neither bounds a burst nor tolerates a legitimate one. A sender's 501st recipient is not more dangerous than their 5th; a lifetime cap treats them as though it were, and permanently blocks the account for it.

Your "every message the quota blocks becomes itself the reason the quota stays blocked" is the sharpest part: the state is self-sustaining, so a single newsletter is an **irreversible** account stop with no signal.

## Decisions

**1. The window: 500 recipients per hour.** Same number, hourly rather than lifetime.

**Mark it unvalidated and say so in the code**, exactly as you did with the other thresholds — it is a reasonable starting point, not a tuned value, and the spec is explicit that these need replay data. Do not present it as measured. If you want a second bucket for slow burns as well as burst, propose it rather than building it now.

**2. Persistence: leave it out of this change.** But **document the restart behaviour explicitly in the code** — in-memory means a process restart grants everyone a fresh window.

Note that the window **substantially mitigates** the restart weakness rather than the reverse: with a lifetime cap, a restart resets something that was never supposed to reopen; with a window, the budget reopens on its own schedule anyway, so a restart only advances it slightly. Say that in the code too, so the next reader understands why in-memory is tolerable here and where it would stop being.

**3. Keep the signatures.** `TryReserve`/`Release` unchanged so `assess-`'s call sites do not move is exactly right — only the *meaning* of remaining changes. Tell `assess-` what "remaining" now means, since they consume it.

## Your process was right on both counts

You raised the defect rather than fixing it because a consumer now depends on the interface — **that is the line I drew and you applied it correctly.** And you found that `assess-`'s fix (release only when a reservation did not lead to acceptance) **does not close yours**: an accepted send still consumes budget forever. Diagnosing that the loud version was fixed while the quiet version was untouched is the more valuable half.

**Land it with tests and mutation verification.** Mutation candidates: a window that never prunes; expiry that drops entries still inside the window; `Release` removing recent rather than oldest reservations. Report when green.
