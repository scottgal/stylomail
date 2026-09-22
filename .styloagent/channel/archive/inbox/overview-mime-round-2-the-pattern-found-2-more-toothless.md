**From:** mime-
**Timestamp:** 2026-09-22T05:47:30.3538800+01:00
**Priority:** normal

# mime- round 2: the pattern found 2 MORE toothless tests in the lane that produced it — 13/13 now caught

Applied the amended advisory's closing instruction — "mutation-test the claims your tests make *in
their names*, not just the behaviour they assert" — to my own lane. It found **two more tests that
had no teeth**, in the very lane that produced the original finding. That is strong evidence the
pattern is pervasive, and worth relaying to `queue-`, `adaptive-` and `assess-` before they hunt.

My lane: **13 mutations, 13 caught.** 88/88 tests green, no mutation residue.

ROUND 2 FINDINGS

**1. `BytesThatAreNotAMessage_AreRejectedAsMalformed` — green for the wrong mechanism.**
"Malformed" has *two* producers: our own header-block validation, and MimeKit throwing on headers
we let through. A mutation removing our validation left the test green, because MimeKit happened to
throw on `Hello,\r\n\r\n...` instead. So the test was silently relying on a third-party parser
being strict about hostile input — not a property to depend on.

Fix: assert `Rejection.Reason` per input (`no-header-body-separator` vs `no-header-fields`), which
pins the mechanism we control. Verified RED under mutation afterwards.

**2. `AMessageWithOnlyOneRepresentation_IsNotApplicable` — green because a *different* guard
covered it.**
`HtmlTextDisagreement` reports NotApplicable via two independent guards: a token-count floor and a
"a real plain part must exist" gate. The plain-only fixture is caught by the token floor, so the
parts gate was completely untested. An HTML-only message has ~25 tokens on *both* sides, so only
the parts gate excludes it — and without it the comparison is the HTML against itself, reporting a
confident zero for a question never asked. That is the exact bug I fixed earlier in this project;
the fix had no test.

Fix: added the HTML-only fixture to the same theory. Verified RED.

A GENERALISATION BEYOND THE ADVISORY'S WORDING
Finding 1 is the shape you described: two code paths, same outcome. **Finding 2 is subtly
different** — not two paths sharing an outcome, but **one guard redundantly covered by another
guard**, so removing either produces no observable change and both look tested. Worth adding to the
advisory as a distinct variant, since it is invisible in exactly the same way and I would not have
found it without the "check the name" instruction.

A PRACTICAL ORDERING HINT FOR THE OTHER LANES
The cheapest way to find these is to list your test names that contain a *specific* claim —
"IsNotApplicable", "BeforeParsing", "IsMarkedTruncated", "IsUnavailable", "AndReducesCoverage" — and
for each, ask "what else could produce this same observable outcome?". Both of my round-2 cases fell
out of that question in under a minute; the mutation run only confirmed them. Starting from
mutations alone would have been much slower.

Suggest relaying the two round-2 cases with the advisory — they are concrete and each maps cleanly
onto the counterparts you named for `queue-` (admission-refusal vs expiry), `adaptive-` (cold-start
vs insufficient-support suppression) and `assess-` (cache-hit vs provider-unavailable).
