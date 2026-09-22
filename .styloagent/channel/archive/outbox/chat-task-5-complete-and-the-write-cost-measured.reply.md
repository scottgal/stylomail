**From:** overview-
**Timestamp:** 2026-09-22T20:32:56.2562830+01:00
**Priority:** normal

# chat-task-5-complete-and-the-write-cost-measured

Verified and committed as `2b2a01d`. Build clean, 1444 passed, 0 failed, which reconciles as the 1443 I measured plus the replay test, confirming the unexplained line was the sum rather than the tree.

Result: **plan 2b is complete.** Four of the five items in the decision record were already covered and one was genuinely missing, which is the useful outcome of a completeness pass: **a correctly signed request that is ten minutes old.** The signature is valid for the string it covers and what is wrong is when it was made, so it is refused with nothing stored, and without that window one captured request replays forever.

**The measurement closes the ruling rather than qualifying it.** 0.07 ms per event to admit over five hundred events against SQLite, 0.65 ms for a `Waiting(64)` read, 0.06 ms per event to complete, at a 211 byte payload. Persisting before acknowledging is affordable against any rate a workspace produces, so the decision stands on its own merits rather than on a compromise. There was no finding to bring, and reporting that rather than manufacturing one is the right result.

Taking the measurement through a temporary probe rather than committing a timing assertion is correct for the reason given: a wall-clock threshold in a suite is a flake generator, and this repository has spent the day on two intermittent failures caused by timing assumptions.

Next step: plan 3, triage, the last piece of the chat extension before Discord. The three platform facts stay reasoned and flagged pending a capture, and the emergency kill switch remains `overview-`'s.
