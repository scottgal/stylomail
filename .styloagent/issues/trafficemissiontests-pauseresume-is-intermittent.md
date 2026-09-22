**From:** overview-
**Timestamp:** 2026-09-22T17:21:57.3091100+01:00
**Severity:** medium
**Status:** open
**Source:** internal

# TrafficEmissionTests pause/resume is intermittently red: 1 timeout in 23 full-suite runs, mechanism unknown

`TrafficEmissionTests.A_pause_and_a_resume_are_announced_as_the_same_kind_of_change` timed out once waiting for the second of two notices, on the first full-suite run on merged main (commit range around e06e7d8). It has not recurred across 22 further full-suite runs, including 8 with two instances concurrent, so 1 failure in 23 and no reproduction.

Two candidate mechanisms, and they are distinguished by what the subscriber received:

1. **Subscription race.** `OnConnectedAsync` adds the connection to its tenant group after the client's `StartAsync` returns, so a notice published in that window reaches a subscriber that has not joined. Predicts the subscriber saw one notice.
2. **A notice lost in the send or the transport.** Predicts the subscriber saw none.

Discriminating evidence is now available: `TrafficTestSupport.NextAsync` was changed in `1bc343e` to name the kinds and subject ids actually received before it gives up, so the next occurrence is diagnosable from the failure line rather than by reproduction.

**If the mechanism is (1), it is not a production defect.** The design's rule 1 is that events are a hint and never state, and the console renders from its own HTTP re-reads, so a notice published before a subscriber joins is a dropped hint and cannot produce a wrong screen. In that case the test asserts a stronger property than the contract, and the fix is to establish membership in the test before asserting, not to strengthen the wire contract.

A bounded direct probe has been requested from `hub-` rather than further full-suite runs: cycle connect-then-publish-immediately a few hundred times, and separately with a small delay, and report the miss rate per arm. A race that fires in 1 of 23 suite runs should appear in a few hundred immediate cycles if it is a race at all.

Impact: one test, contained to the traffic emission suite. No other suite is affected and no production path is implicated unless the probe establishes mechanism (2). Do not raise the timeout: the test waits 15 seconds for a notice, so time is not the missing thing, and a longer window makes a real defect rarer rather than absent.
