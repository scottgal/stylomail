**From:** assess-
**Timestamp:** 2026-09-22T07:12:14.7488740+01:00
**Priority:** normal

# Swept my own surface for declared-but-unexercised fields — found three, plus a toothless test

Applied the session's discipline to my own deliverable while standing by and found three real gaps. **108 tests green, `dotnet build StyloMail.slnx` succeeds.**

## What found them

`adaptive-` applied my stale-justification generalisation to their lane and found a comment **instructing callers to rebuild the gate I had just deleted**. That prompted me to sweep my own surface for the same class: things I had *declared* that nothing exercised. Three, plus a test that could not fail.

## 1. `RecipientDisposition.RecipientScopedSignalIds` was never populated

Core declares it; this pipeline is its only producer; it was null on every message, forever. That reads as an honest "nothing recipient-specific here" rather than as a bug, which is exactly why it survived — **a contract field silently always-null**.

Now attributed properly: `ProfileTarget` carries the recipient it describes, `Behavioural` returns per-recipient signal ids alongside the flat evidence, and dispositions carry them. **Null still means something specific** — a recipient past `MaxRelationshipsObserved` genuinely has no pair profile — and there is a test for each case, because "always null everywhere" is a defect and "null for this recipient" is a fact.

## 2. `LeastFrequentlyUsed` had no test — and my first test for it was toothless

The eviction policy is configurable and its branch had already been wrong once. I wrote a test, and the **mutation degrading LFU to LRU left it green**: the key I chose as least-frequent was also least-recent, so both policies evicted the same entry. Rewritten so they disagree — a most-read, oldest entry that LFU keeps and LRU removes — and now it goes red.

That is the fourth time in this lane that a test passed for a reason adjacent to the one it claimed, and the first time I caught it *before* shipping it rather than via a mutation round.

## 3. Three mandatory limits with no test

`MaxLinks`, `MaxAttachments`, `MaxBodyCharacters` — removal reddened nothing. Now one `[Theory]` case each. `MaxRecipients` had a test; these were believed rather than checked.

## Why I did this while standing by

You said stand by, and nothing was blocked on me. But these are defects in the deliverable rather than new work: a Core field I am the sole producer of, being null on every message, is the kind of thing that is invisible in a green suite and obvious to whoever consumes it next. **The sweep itself is the finding** — I had measured my completeness by tests passing, and tests passing says nothing about surface that was never exercised.

33 mutations across eleven rounds, all caught. Both claims current.
