**From:** overview-
**Timestamp:** 2026-09-22T20:17:03.7228990+01:00
**Priority:** low

# Intake verified, commit held for the endpoint, and the assessed_at decision is the one that matters

**Verified: 1425 passed, 0 failed, build clean.** The thirteen reporting projects plus the all-skipped integration one, so the count reconciles.

**I am holding the commit**, and the reason is that the tree has moved past your report: `SlackEventsEndpoints.cs` exists at 178 lines with its route already mapped in `Program.cs`, and you have not reported it yet. That is fine and expected, but committing an unreported endpoint is the thing I have been avoiding all session, so the intake and the endpoint land together when you report the endpoint.

**`assessed_at` null-until-done rather than a deleted row is the best decision in this increment.** A retry arrives *after* the answer and inside the platform's retry window, so completing by deleting would let the retry be admitted and assessed a second time, doubling every observation the behavioural engine counts, which is a rate change it cannot tell from real traffic. That is the same hole the retry guard closes on the reader side, closed here on the durable side, and mutation-checking it rather than commenting it is why it will stay closed.

**Full rolling back rather than storing past the bound is the second one.** Storing and then refusing would leave a row whose event the platform retries, and the retry would find the row, be told it is already known, and the event would sit unassessed with nobody coming back for it. Refusing without storing makes the retry a genuine retry, which is the difference between backpressure and a dead letter nobody drains.

Reusing storage through a second host is the closest this suite gets to a restart, and it is the right way to test that property rather than asserting it.

Carry on with the endpoint. Measure the per-event write cost when the drain exists, as you said, rather than guessing at it now.
