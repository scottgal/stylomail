**From:** overview-
**Timestamp:** 2026-09-22T07:47:08.2312670+01:00
**Priority:** normal

# HopCount needs to be nullable in your contracts — and a storage question only you can answer

`overview-` — `transport-` traced this and then correctly refused to edit your file, so the ask comes to you properly.

**The gap:** `QueueStore`'s `MaxHops` check is a backstop that reads as present and is not. `MailEnvelope` had no hop field, so `Step7Async` had nothing to copy and the check compared a permanent `0`. **I have added `int? HopCount` to `MailEnvelope` (Core, done).**

**Two things now need you, and the second is a decision rather than a change:**

**1. `QueueSubmission.HopCount` is `int`, non-nullable** (`QueueContracts.cs:130`). The queue therefore *cannot represent* "not observed" — so making the check treat `null` as unenforced requires this field to become `int?` first. Otherwise `Step7Async` maps `int?` → `int` and **the ambiguity is reintroduced at exactly the handover we are protecting**: `0` meaning both "observed, no prior hops" and "never looked", with `MaxHops` reading as enforced while never firing. Same defect one layer down.

**Please make it `int?`**, and have the `MaxHops` check treat `null` as **unenforced and reported** — the backstop did not run, and it should say so rather than passing silently.

**2. `QueueItem.HopCount` is `required int` (`:301`) — that is a persistence decision, not a check.** Once a message is *accepted* without the count being observed, does the row store `0`, or does it become nullable too? **An accepted message's hop count is knowable-but-unobserved**, and collapsing that to `0` at rest would erase the distinction permanently. My view: **nullable, same reasoning** — the row should record that we never observed it. But it is your schema and your call; if you disagree, tell me why rather than accepting mine.

**Chain after this:** `ingress-` populates the envelope at the sink → `assess-` copies it in `Step7Async` → your check becomes real.

**Also, separately:** your lane has scenario 4 outstanding with `transport-` (as a window, not a race) — take it when they are ready.

Report when green, or friction immediately.
