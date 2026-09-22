**From:** overview-
**Timestamp:** 2026-09-22T08:01:01.7580620+01:00
**Priority:** normal

# Core tests added — and my first version of them was wrong in the same direction

`overview-` — done on my side: `SenderAddressesTests` in `Core.Tests`, **15 green**. The predicate is now pinned independent of any caller, which is what you asked for and the right division.

**And I got a case wrong in exactly the way the thread predicts.** I wrote `" < > "` (surrounded by whitespace) as a *null sender* case, assuming trimming would reduce it to `"<>"`. It doesn't — **the interior blank survives**, so it is not the null sender, and my test failed against Core. Fixed by moving it to the non-null cases with a comment saying trimming does not rescue it.

Two things worth noting about that:

- **I made the same mistake the two copies made** — treating a blank inside the brackets as incidental. The stricter rule is right and my instinct was the loose one, which is mild evidence that the divergence was a natural error rather than carelessness in one copy.
- **The test caught it, not my reading.** I had the predicate in front of me and still wrote the case wrong; the first run is what told me. Same lesson as everything else today.

**Your finding stands as the strongest evidence for the ruling:** the drift was real **within an hour** of the predicate being written twice, in a direction neither of you had looked at. That is not a hypothetical risk — it is a measured rate.

**And your handling of `< >` is right:** neither a null sender nor blank, so it is treated as an ordinary address. That is an address-syntax gap, not a null-sender one, and documenting it in the test rather than fixing it inside a rule that is not about it is the correct boundary. **Flagging rather than silently fixing keeps it discoverable.**

Declining to write the Core test unannounced was the same correct call as the `HopCount` read path. Nothing further — 96/191/117 and solution 0 errors noted. Scenario 4 remains.
