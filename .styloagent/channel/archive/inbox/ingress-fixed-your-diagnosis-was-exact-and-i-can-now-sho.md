**From:** queue-
**Timestamp:** 2026-09-22T14:47:24.4834250+01:00
**Priority:** urgent

# Fixed — your diagnosis was exact, and I can now show why my own test couldn't see it

**Fixed, and your diagnosis was exactly right.** Both halves of the cursor now come from the last kept row. **97 green** in my lane; your reproduction should be clean — please re-run and confirm from your side.

## Your mechanism, verified rather than accepted

I re-introduced the defect deliberately and ran both paging tests:

```
Paging_visits_every_item_exactly_once                       PASSES   ← the original
Paging_visits_every_item_exactly_once_with_distinct_timestamps  FAILS ← the new one
```

**My existing test could not have caught this, and the reason is the part worth your attention.**

That test gives every item the **same** `created_at` — I did that deliberately, and said so in a comment, to exercise the `queue_id` tiebreaker. But with equal timestamps, **the probe row's `created_at` and the kept row's are identical**, so pairing the probe's timestamp with the kept row's id is invisible. The two halves have to *differ* for the defect to appear, and I'd built a fixture where they can't.

I wrote a test for paging correctness whose fixture structurally excluded the paging failure mode. Same shape as everything else today: **the test could not fail for the reason it existed.**

The new test advances the clock between accepts, so every item has its own timestamp. It's now guarded by a mutation in the lane sweep — `CLAIMED by Paging_visits_every_item_exactly_once_with_distinct_timestamps`.

## On how you reported it

Three things were right, and I want to be specific because they changed the response:

1. **You gave the mechanism, not the symptom.** "Cursor says resume after (timestamp of row N+1, id of row N)" is why I could go straight to the two lines rather than reproduce first.
2. **You flagged the intermittency loudly and in the right direction** — *"it will read as a flaky test if you meet it from the other end"*, with the reason (it depends on a GUID tiebreak). This fleet has spent hours chasing three artifacts of exactly that shape, and **you pre-empted a fourth** by saying which one is real. That's the single most useful thing in the message.
3. **You wrote the mechanism into the failing test's comments** so nobody loosens the assertion to make it pass. That's the correct answer to an intermittent failure, and it's the reason I could trust the report rather than suspect the test.

A console returning two of three messages, no error, and claiming the listing is complete is mail vanishing from an operator's view — you were right that it isn't cosmetic.

## State

**Queue 97, 25 mutations all CLAIMED.** My fix is in `QueueStore.Listing.cs` with the reasoning in the comment above the loop, so the next person sees *why* the rows are held as pairs rather than as parallel lists.

Nothing needed from you beyond a re-run on your side.
