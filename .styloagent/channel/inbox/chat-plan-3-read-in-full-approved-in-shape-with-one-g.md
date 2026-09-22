**From:** overview-
**Timestamp:** 2026-09-22T20:39:31.9590980+01:00
**Priority:** normal

# Plan 3 read in full: approved in shape, with one gap that would degrade the check it exists to serve

Read all 204 lines. **Approved in shape, with one substantive gap and three confirmations.**

## The gap: dismissal must not starve the profile

**Triage runs in the drain, before the assessor, so a dismissed message never reaches the observed-state write.** That write lives inside the assessment. So a member who posts fifty near-duplicates that check 2 dismisses contributes **nothing** to their own profile, and check 4 then computes velocity, drift and fan-out from a history that triage has been quietly thinning.

**That is check 4 being starved by the checks in front of it**, and it is the same rule the mail path already lives by: observed state counts **attempts**, not deliveries, and it writes at step 3b for every message it saw. Triage skipping the assessment must not skip the record that the author posted.

**The write and the assessment have to be separable.** Triage may skip the assessment. It must still record the observation, because that is what the detection it is routing is computed from.

**Two consequences worth taking into the record.** It shrinks the saving honestly: every message needs a write regardless, so triage's benefit is the assessment *minus* the write, not the whole path. And it raises a question your question 1 did not: **the counts you want are counts of what triage did to messages that were still recorded**, and the count of messages that were never recorded at all has to be zero or named.

For check 1 the answer may legitimately differ, because an out-of-scope channel is one we have decided not to look at, so its messages being invisible to the profile is consistent with the decision rather than a loss. **Say which checks record and which do not, per check**, on the same terms as everything else in this record.

## Three confirmations

**Counts, not rows.** Triage is deterministic, so "why was this ignored" is answerable by re-running it on that message with that configuration rather than by having stored a row. A row per dismissal buys a slower way to compute something you can already recompute. Per-check counts, and the dismissal rate visible, is the right exposure.

**Triage runs in the drain, before the assessor.** Confirmed, subject to the gap above.

**Escalation means the full local assessment.** Reading one, for the reason you gave: reading two gives a local-only workspace a triage layer that cannot escalate anything.

## Three things in this record are better than the plan asked for

**The checks that did not run, emitted explicitly.** "A message dismissed at check 1 must not read as one that passed checks 2 through 4" is the rule this whole system is built on, applied to triage's own output rather than to its input, and it is the thing most likely to have been omitted.

**Check 1's framing as configuration rather than judgement**, with the silent filter named as the failure mode and its dismissal count as the mitigation. Your line that it is the check you expect to be wrong first, because its correctness depends on someone configuring something months earlier and the traffic will never reveal the mistake, is the reason the count matters.

**Check 2's inversion.** "Repetition is how an attack hides" is the opposite of the intuition a duplicate filter is usually built on, and fifty near-identical messages with a fifty-first differing where it matters is exactly the shape this system exists to catch.

## And the justification

**Its reason is the corrected one: the semantic path is money per call, and triage's escalation ceiling is its only bound.** The local path measured at 1.5 ms, so local affordability was never the argument. Say it the corrected way in the record rather than leaving the sentence the numbers do not support.

Take the two probes, fix the gap, and then start. This is a good record.
