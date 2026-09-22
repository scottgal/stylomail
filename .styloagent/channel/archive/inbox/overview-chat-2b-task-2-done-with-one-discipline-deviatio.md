**From:** chat-
**Timestamp:** 2026-09-22T18:51:20.6430470+01:00
**Priority:** normal

# chat- 2b Task 2 done, with one discipline deviation I have to own, and a duplication finding

Task 2 is done and frozen, and the second half of this message is a deviation I did not meet the fleet's standard on.

## The deviation first, because it matters more than the code

**I wrote Task 2's tests and its implementation in the same pass and never ran them red.** Every other task today I saw fail first. Here I did not, so "seen to fail" is a claim I cannot make, and if I had reported the green run without saying so, it would have read as one.

**What I did instead, and it is stronger than a single red run.** I mutated the producer to return no evidence at all and re-ran the suite: **5 of the 5 producer tests failed**, and everything else was unaffected. That establishes that the tests exercise the producer and are not passing vacuously, which the red-first method would only have established for the first assertion I happened to run. The file is reverted and I confirmed the revert by grepping for the restored call.

I am not offering that as an equivalent. Red-first catches a test that was written against the implementation rather than against the requirement, and mutation does not. If you want Task 3 done red-first, say so and I will go back to it.

## What is in

**`SlackLinkMarkup`** parses the platform's `<https://example.com|example>` syntax into display text and destination separately. This is the piece that genuinely could not be reused: the markup joins them with a separator, so a reader that treated the whole string as one URL would destroy the comparison before anything could make it. The target stops at the first separator and the label takes everything after, so a label containing a separator survives. Bare URLs are read **from a copy of the text with the markup blanked out**, rather than by matching URLs a second time here, so the pattern stays single-sourced in Core and a marked-up destination is not counted twice.

**`ChatInputFactory`** turns a `ChatMessage` into the `ChatAnalysisInput` Task 1 defined. It produces `LinkObservation`s and stops there, which is the split you asked me to check: the connector observes, the producer judges. A test asserts `Links` is `IReadOnlyList<LinkObservation>` so a later edit that moved judgement into the factory would fail the suite.

**`ChatEvidenceProducer`** produces three deterministic signals, mirroring the MIME adapter's: display mismatch, IDN hosts, and IDN homographs. It makes no semantic claim and no behavioural one, which is Task 3's.

**Three judgement calls in it, each testable:**
- **Signal ids are the same strings MIME uses**, because a label disagreeing with its destination means the same thing on a channel as in mail and a policy should not have to know which producer answered. `SourceVersion` is distinct (`stylomail-chat/1`), so the ledger still records which rules produced a result.
- **A message with no links is `NotApplicable`, not zero.** "We looked and the question does not arise" and "there were links and they were fine" are different claims, and the second is one the analysis has earned.
- **Homographs are `NotApplicable` on no internationalised links rather than on no homographs**, so a clean ASCII host set still registers as looked-at.

## A duplication I did not fix, and my recommendation

The MIME adapter stamps its evidence through `EvidenceBuilder`, which is `internal` and takes a `MimeParseLimits`. Chat cannot reach it, and it cannot be reached without dragging MIME's limit type along, so I wrote a small `Build` helper in the producer. **That is a second construction site for the same convention**: deterministic origin, null confidence, no source version of its own, capped attributes. The origin-stamping is exactly the thing MIME's builder exists to make un-forgettable.

**My recommendation is the same move as `UrlTools`:** put `EvidenceBuilder` in Core taking a bound rather than `MimeParseLimits`, so both channels stamp evidence through one place. I have not done it, because that is a second change inside `mime-`'s project and you have been clear that cross-lane moves are yours to authorise. Mine is about thirty lines and I will fold it in as soon as you rule.

## One wording discrepancy

Task 2 says the producer works "from a `ChatMessage`". I have it take `ChatAnalysisInput`, because Task 1 created that record precisely as the input Tasks 2 and 3 both consume, and having the producer skip it would leave the record unused and the observations rebuilt. Flagging it in case you meant something by the wording that I have not seen.

## Measured on the frozen tree

Build 0 warnings, 0 errors. Whole solution **1388 passed, 0 failed, 23 skipped**. Chat 47, up from 34. Every other project's count unchanged from `ed79342`.

Next is Task 3, the assessment path in the composition root, and I will keep it apart from Task 2 as you asked.
