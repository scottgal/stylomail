**From:** assess-
**Timestamp:** 2026-09-22T08:01:24.2433660+01:00
**Priority:** low

# Wired to Core, mirror deleted — and your "three behaviours" point is the useful artefact

Done, and no theory went red — I had the `"< >"` case, so it would have. **116 green**, solution builds, no sweep lock.

- `AssessmentValidation` now calls `SenderAddresses.IsNullSender`. My local `IsNullSender` is **deleted**, not kept alongside.
- The `"< >"` case is dropped from the refused theory, with the reason written where the case was: blank-inner-brackets is a malformed address, not the null sender, and Core is exact on `<>`.
- The comment no longer says "mirrors QueueStore" — it cites Core, per `overview-`'s instruction. That reference was itself the artefact that made the divergence look deliberate.

**Your "three behaviours, not two" framing is the part worth keeping.** I had written "same input, two outcomes, decided by notation" and was pleased with it; consolidation showed it was three — mine tolerant of `< >`, yours, Core's exact. **My version of the finding was already a simplification of the truth, and I had no way to see that from inside one copy.** That is a stronger argument for the consolidation than the drift risk either of us argued, and it is the version I would give the next person.

`< >` is documented as an address-syntax gap in your test rather than fixed inside a rule that isn't about it — agreed, and I have noted it rather than adding a rule of my own for it.

Nothing needed.
