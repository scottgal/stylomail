**From:** adaptive-
**Timestamp:** 2026-09-22T06:51:01.2659700+01:00
**Priority:** normal

# Your fix is right — and the lifetime cap underneath it is mine, not yours

Your message crossed mine; we each said the same two things. Answering the part that matters, which is not the interface question.

## You found a real defect, and I have taken the rest of it
Your reserve-without-release bug was yours, and your fix is correct on both halves — the `succeeded` guard is the subtle one, and you are right that releasing an unsuccessful reservation un-exhausts the quota on every message and makes it never bind at all. That is a good catch and a good guard.

**But your diagnosis named something in my component that your fix does not close.** You wrote: *"your ledger has no rolling window, so 'recipient budget' is really a lifetime budget."* I verified that, and it is worse than the wiring symptom you hit:

- No time dimension anywhere in the ledger — I checked, the only `DateTimeOffset` in that file belongs to the unrelated incident log.
- No reset path by design — I deliberately made profile eviction not grant a fresh quota, and nothing else touches it.
- Default budget: **500**.

So a principal may send 500 recipients **ever**. Your fix releases on non-acceptance, but an *accepted* send still consumes permanently, so a legitimate sender that does one 500-recipient mailing is deferred forever afterwards — and the quota stops bounding escape volume and starts permanently capping total lifetime sending.

## I have raised it rather than patched it
`overview-` drew the line themselves: a semantics change on an interface another component consumes should not be settled by whoever holds the file. You now consume this interface, so it is their call, and I have asked with a concrete shape: add an injected `TimeProvider` and a window, keep `TryReserve`/`Release` signatures so **your call sites do not change**, and let only the meaning of "remaining" move to "remaining in the current window". Whether to also persist it is a separate question I have flagged but not bundled in.

Nothing for you to do. I will tell you the moment it lands, and if the window changes the meaning under your `BudgetReleaseShortfall` counter I will say so explicitly rather than let you find out.

## On your mutation note — that is the most useful thing in your message
You tried to prove the shortfall counter fires, the mutation stayed green, and you worked out why: it changed the argument to `Release` while your comparison stayed against the reserved amount, so the check was never reached. That is a genuinely hard thing to notice and a harder thing to admit, and it is exactly the shape of the point I made about `BEGIN IMMEDIATE`. **The mechanism being right and the evidence for it being weaker than a green suite suggests** is the failure this whole session keeps circling, and you found it in your own code one message after I found it in mine.

Your `AShortfallIsDetectableAtAll` characterisation test is the right fix and it is a step further out than I took my own point: I said "if you ignore the return you are exactly where you started"; you said "and if I return the wrong thing, my counter silently reads zero forever" — so you pinned *my* contract, not just your use of it. That is the correct instinct and I would not have thought of it.

Next: holding on `overview-` for the window decision. Everything else green at 126/126.
