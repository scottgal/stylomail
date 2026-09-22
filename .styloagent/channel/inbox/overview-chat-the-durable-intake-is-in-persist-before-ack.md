**From:** chat-
**Timestamp:** 2026-09-22T20:15:57.8883870+01:00
**Priority:** normal

# chat- the durable intake is in: persist before ack, bound by refusal, and a mark rather than a delete

The durable intake is in and frozen, built to the ruling.

## What it is

**`host_chat_intake`**, in the host's own database alongside the decision ledger: the event id, when it arrived, the payload, and `assessed_at` null until it has been dealt with. The endpoint will write the row **before** answering the platform, which is what makes the answer true.

**`assessed_at` is null-until-done rather than a row that goes away, and that is the decision worth your eye.** A retry arrives *after* the answer and inside the platform's retry window, so if completing an event removed the row, the retry would be admitted again and assessed a second time. That doubles every observation the behavioural engine counts, which is a rate change it cannot tell from real traffic. Marking is what recognises the retry; the row is then pruned by age, and the retention window has to exceed the platform's retry window or the same hole reopens later. There is a comment saying so.

**Full rolls back rather than storing past the bound.** Storing and then refusing would leave a row whose event the platform retries, and the retry would find the row, be told "already known", and the event would sit unassessed with nobody coming back for it. Refusing without storing means the retry is a genuine retry.

**`Waiting` is ordered oldest first**, so a burst cannot starve what arrived before it, and the index is partial on the waiting rows so it covers only what the drain reads.

## Verified

**The durability test reopens the database through a second host** (`ReusingStorageOf`), which is the closest this suite gets to a restart, and the row is there with its original timestamp.

**And I mutation-checked the property I reasoned hardest about.** Changing `Complete` from marking to deleting fails exactly `An_event_already_assessed_is_not_admitted_again`, out of six. That is the double-assessment hole, and it is now pinned rather than commented.

## Measured

Build 0 warnings, 0 errors. Whole solution **1425 passed, 0 failed, 23 skipped** (six new). Sweep signals clean, no em-dashes in anything added.

## Next

The endpoint itself: the `url_verification` challenge, signature verification, the own-post drop, and the hand-off that writes the row before answering. The drain comes after it, and the per-event write cost you asked me to bring numbers for if it turns out disproportionate is something I will measure once the drain exists rather than guess at now.
