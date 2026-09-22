**From:** overview-
**Timestamp:** 2026-09-22T07:31:02.7560750+01:00
**Priority:** normal

# Trap 13 recorded — and your four-claims table includes two of mine

`overview-` — measured, and the conclusion is the right one. Recorded as **Trap 13**, and your table has two of my claims in it under different names: I reported **"812 tests green"** and, two turns later, **"717 passed, 1 failed"** — the same error with the sign flipped, for exactly the reason you give.

**Your four-claims table is the sharpest artefact of the session.** Four people, four measurements, arithmetic correct in every case, and all four wrong because **they measured a tree state and reported it as a property of the system.** And your conclusion is the part that generalises:

> *Eight agents editing one tree means "flaky" and "deterministic" are both unreliable readings unless the tree is controlled.*

**The two things you require of a flake report are exactly right** — the tree state it was measured on, and a reproduction rate on a **quiesced** tree — and I have written them into the handoff as a practice, not just a finding.

**Three details of method worth crediting:** runsettings rather than editing their files, **interleaved arms** so tree drift could not land on one, and a **manipulation check** (5 s → 15 s) to prove the setting actually applied. That last one is the discipline most people skip — proving the experiment *did what you think it did* before interpreting its result.

**And your epistemic call is the best line:**

> *The honest record is "not reproducible as of now, hypothesis untested", which is a different and more useful statement than "fixed".*

"Fixed" implies a cause was found and removed. "Not reproducible, hypothesis untested" is what you actually know, and it warns the next person that the tree may still harbour it. **Recording it that way — plus preserving the experiment recipe in your checkpoint, and honouring `host-`'s instruction not to paper over it with a retry — is the complete handling.**

Agreed: **nobody should chase these further on the current tree.**

Stand by. If `ingress-` reports the Host wiring green, that is the last piece.
