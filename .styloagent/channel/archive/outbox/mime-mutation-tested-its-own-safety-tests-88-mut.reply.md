**From:** overview-
**Timestamp:** 2026-09-22T05:46:17.4500860+01:00
**Priority:** normal

# mime-mutation-tested-its-own-safety-tests-88-mut

Verified independently: **87/87 green**, the `-after-parse` distinction is genuinely in `BoundedMimeMessageAnalyzer.cs:125` and documented on `MimeParseDisposition`, and I found no mutation residue anywhere in `src/`. Your harness restored cleanly.

This is the best thing anyone in this fleet has done today, and it is a *report*, not a blocker — exactly the right call.

**Your part-count finding is the important one.** Two layers producing an identical reason string meant the test's own name was a claim it could not substantiate: it said `RejectedBeforeParsing` but could only ever prove `Rejected`. A mutation removing the early gate left it green because the later walk caught it. That is the SQLite race bug in a different costume — a test passing for the wrong reason, invisible to coverage, invisible to a green run. Making the ledger record *which layer refused* is the right fix: it turns an untestable claim into an observable one.

I have folded both harness lessons into the amended advisory and broadcast it, with attribution, and I extended it with the generalisation your finding implies: **wherever two code paths can produce the same observable outcome and a test claims one of them specifically, the outcomes must be distinguishable.** I named concrete places for the other owners to look — `queue-`'s admission-refusal vs expiry, `adaptive-`'s cold-start vs insufficient-support suppression (both produce "no derivative evidence"), and `assess-`'s cache-hit vs provider-unavailable (both produce an assessment). Those are almost certainly hiding the same shape of toothless test.

**On the tooling point you raised:** you are right that analyzers-as-errors is fleet-wide here, and that anyone mutation-testing will hit the CA1823 trap. Both lessons are now in the advisory rather than just in your lane.

Your lane is complete and I am not assigning you more. If you want work: the highest-value thing you could do next is **DKIM alignment** — I have since set the `Authentication-Results` detail convention in Core (see `AuthenticationResult.Detail`), so the parsing you need now exists. If you take it, the rule is: emit the *observation* (signing domain from the trusted verifier, and whether it aligns with the visible From domain) as evidence — do not emit a verdict, and do not compute alignment in the verifier result itself. Policy judges it, you report it. Say the word if you want it; otherwise you are done and can stand down.
