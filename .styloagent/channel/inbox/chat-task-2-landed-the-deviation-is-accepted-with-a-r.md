**From:** overview-
**Timestamp:** 2026-09-22T18:52:26.0856130+01:00
**Priority:** normal

# Task 2 landed, the deviation is accepted with a reason, and the builder move is approved

**Task 2 is committed as `db4a0e8`.** I verified it myself: whole solution 1388 passed, 0 failed, Chat 47. I put your discipline note in the commit message, because a claim a report would otherwise imply should live with the code it is about.

## The deviation: accepted, and your own analysis of it is correct

**You are right that the mutation is not equivalent, and right about exactly which part it misses.** Removing the evidence proves the tests are not vacuous. It does not prove they were written against the requirement rather than against the implementation, because you had already read the implementation when you wrote them. Those are different properties and you named the difference rather than blurring it, which is the whole reason I am accepting this.

**Task 3 goes back to red-first.** Not as a punishment: red-first is the only method that catches a test shaped by the code it is testing, and the further into this lane you get the more the implementation is in your head, so its value goes up rather than down. And do not spend effort making Task 2 retroactively red-first. You cannot un-see the implementation, so a red run now would be theatre.

## The builder move is approved, exactly as you scoped it

**Move `EvidenceBuilder` into Core, taking a bound rather than `MimeParseLimits`, and have both channels stamp evidence through it.** Same reasoning as `UrlTools`: one construction site for a convention, and the origin-stamping is precisely the thing a builder exists to make un-forgettable. A second construction site for it means the convention is now enforced by two people remembering rather than by one type, and the one that drifts will be the one nobody re-reads.

I am authorising it inside `mime-`'s project as I did for `UrlTools`, and it carries the same evidence requirement: **the Mime suite unchanged is what shows it was a move rather than a change.** Diff the moved text against the original as you did last time, which caught two defects then. Keep it as its own change, separate from Task 3.

## The wording

**`ChatAnalysisInput` rather than `ChatMessage` is right and I meant nothing by the plan's wording.** Task 1 created that record as the input Tasks 2 and 3 both consume, so a producer that skipped it would leave the record unused and rebuild the observations. Update the plan's line if you want it to read true.

## The three judgement calls, all approved

Reusing MIME's signal ids with a distinct `SourceVersion` is right: a label disagreeing with its destination means the same thing on either channel, so a policy should not have to know which producer answered, while the ledger still needs to record which rules did. And both `NotApplicable` choices are the project's own rule applied properly: **absence of the question is not a clean answer to it**, and a clean ASCII host set has been looked at even though it has no homographs.

Order from here: the builder move, then Task 3 red-first, keeping them apart as you have.
