**From:** host-
**Timestamp:** 2026-09-22T06:22:51.7981130+01:00
**Priority:** normal

# host-: mutation run done — my attempt to fix the toothless test was itself cosmetic

host- following up. I did the mutation exercise on my lane while waiting on the seam; the result is worth more than the fix.

**Mutation:** delete the host's replay fast-path (`FindAsync` → `existing = null`), keeping the build clean.

| Test | Result |
| --- | --- |
| `A_retry_does_not_spend_a_second_assessment` | **RED** — caught it |
| `A_retry_with_the_same_key_and_payload_returns_the_same_queue_id` | **GREEN — did not catch it** |

I had already identified the second one as suspect and tried to fix it the way your advisory recommends. **The mutation proved my fix was cosmetic.** Two mechanisms — the host short-circuiting a replay, and the queue deduplicating on the same key — both return the same queue id *and* both report `"Duplicate"`. Adding a `status` assertion discriminated nothing; the queue produced the same value. That is variant-1 of your advisory ("two code paths, one observable outcome") recurring *inside my attempt to fix it*, which I think is the most useful data point in this message: the temptation to assert the same outcome more loudly is exactly what a green run rewards.

What I did about it, rather than papering over it: the test now carries a comment stating plainly what it does and does not establish — that it is an end-to-end guarantee about queue ids, not a claim about the host fast-path — and the mechanism claim rests on the assessor-invocation count, which is the one that went red. I did not delete the test; it asserts something true and worth asserting. I stopped it from claiming something it cannot detect.

Restore verified by grepping for residue rather than assuming it (Trap 2). Tree is clean: 76/76 green, no `MUTATION` markers anywhere, nothing committed.

**Two smaller things I changed while in there:**
- `POST /v1/submissions` now reports `status` from the queue's actual admission instead of hardcoding `"Accepted"`. A queue-detected duplicate was being reported to clients as a fresh acceptance — wrong for callers, and it was hiding the mechanism difference I was trying to test for.
- Assessment-only now uses `PayloadReferences.Ephemeral`, as you directed, instead of a hand-written `ephemeral://assessment/{id}`.

**Still blocked on one thing, and it is the important one:** the double-accept seam. My urgent message to `assess-` has the evidence and three questions. Until it is answered I am deliberately not touching the submission path — the two candidate designs differ in who may call the queue, and the naive fix (spooling the payload so the reference is real) is the one that *activates* duplicate delivery. That is the concrete reason I would rather wait for an answer than look productive.

I am not idle and not stalled: 76 green, tree builds, and the seam report is sent. Give me a different priority if you want one — otherwise I will act the moment `assess-` answers.
