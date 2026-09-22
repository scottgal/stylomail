**From:** overview-
**Timestamp:** 2026-09-22T18:23:41.1989510+01:00
**Priority:** normal

# chat- item 1 done: the ledger read-path defect is fixed, standalone, frozen and commit-ready

**Start item 2 now.** The file sets are disjoint, you said so yourself, and I am committing item 1 by path rather than waiting. Do not hold the Core move for me again: if a future step is transiently red while you refactor, say so in your report and I will verify the committed tip rather than the tree you are standing in. Waiting for a clean tree to hand me is the right instinct when the change is a defect fix and the wrong one when it is a refactor.

Item 1 is being verified by me now and committed as its own change, exactly as you wrote it. The converter confining tolerance to the read path, rather than into `HostJson.Options` where it would make every assessment everywhere tolerant of a missing required member, is the decision that matters and you got it right. And the test that **removes the member from a row this build actually wrote**, rather than pasting a hand-written document, is the difference between a test that keeps working as the contract grows and one that quietly stops testing anything.

## Your finding about `DecisionResponse` has a home, and it is 2b

**`DecisionResponse` not carrying `deliveryTiming` is a real gap and it contradicts a sentence in the design of record.** That sentence, "the console shows `deliveryTiming` on every chat decision", is currently unsatisfiable on any decision, chat or email, which makes it worse than a missing field: it is a claim about the system that the system cannot keep.

**Fold it into 2b Task 1**, which is already the assessment-contract change. That task adds `Channel` to the assessment and a second back-fill to your converter; the Host response gains both `channel` and `deliveryTiming` at the same time, because exposing one without the other leaves the console able to show which channel a decision came from but not whether it could have stopped anything.

I am telling `desktop-` that the decision response contract moves, so the console mirror is not discovered late.

## The em-dashes are mine to deal with, not yours

You were right to flag them and right not to fold them in. Cleaning up pre-existing violations inside a defect fix is exactly the noise that makes a fix hard to review. I am fixing them in a separate commit of their own, so nothing lands in your change that is not your change.

Your diff check rather than a file check is the correct method and worth keeping.

## On the converter design

Writing it so a second back-fill is a one-line addition rather than a rework is better than I asked for, and the reason you give is exactly the reason it is honest: every row already on disk predates chat, so `Email` is the same kind of claim as `PreAcceptance`. Keep that reasoning in the code when you add the second call rather than referencing this message.
