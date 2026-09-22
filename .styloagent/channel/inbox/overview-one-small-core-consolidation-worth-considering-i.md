**From:** queue-
**Timestamp:** 2026-09-22T07:57:50.7337120+01:00
**Priority:** normal

# One small Core consolidation worth considering — IsNullSender has two definitions

One item, small, and yours to decline. Raised because you said nothing further was needed, so I'll keep it to a decision rather than a request.

**`IsNullSender` now exists twice** — mine in `QueueStore` and `assess-`'s in `AssessmentValidation`, mirrored deliberately and both citing each other. Their message flagged it rather than hiding it, and it's a real divergence class: **if the two ever disagree, the assessor and the queue disagree about whether the same message is a DSN**, with one refusing before provider spend and the other after. They found that exact defect in their own copy an hour ago — it recognised `""` but not `<>`, so identical input was decided by notation.

**The precedent is already in Core.** `PayloadReferences` is exactly this: a two-line static class holding one shared, security-relevant rule, created for this precise reason ("only a `spool://` reference may reach durable acceptance"). `IsNullSender` is the same category and the same size.

**My recommendation: move it to Core** alongside `PayloadReferences`, with the contract you already ruled — `""` is the travelling value, `<>` normalised at the boundary, predicate tolerant of both.

**The argument against the status quo is not the duplication, it's the mitigation.** We'd fall back on "ping me if it changes", which depends on whoever edits one copy remembering at that moment that another exists. **That's exactly the condition that fails** — and it fails silently, which is how today's divergences have all worked. I'd rather not ship a mitigation whose failure mode is "someone forgot".

**Your call, and I don't want to expand scope late.** If you'd rather keep the convention, both comments already cite each other and that is genuine mitigation. If yes, it's small: one Core type, then two call sites — mine and `assess-`'s, which I'd coordinate rather than edit.

Everything else is done: **Queue 97, Transport 191, Assessment 117, solution 0 errors, tree clean.** Scenario 4 remains the only outstanding item.
