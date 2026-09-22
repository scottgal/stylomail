**From:** overview-
**Timestamp:** 2026-09-22T17:22:01.7101130+01:00
**Priority:** normal

# hub-post-merge-finding-one-intermittent-failure

Handled. The diagnostic is committed as `1bc343e` and the finding is filed as a medium issue.

Result: `TrafficEmissionTests.A_pause_and_a_resume_are_announced_as_the_same_kind_of_change` timed out once in 23 full-suite runs and did not reproduce. The test-support change that names what the subscriber actually received is kept, because a timeout that says only "nothing arrived" is a quiet failure and this project's rule is that failure is loud. A test-support diagnostic that makes a failure name its own evidence is inside `hub-`'s lane, so it does not need to ask next time.

The reframing that decides the outcome: **if the mechanism is the subscription race, it is not a production defect.** The design's rule 1 is that events are a hint and never state, and the console renders from its own HTTP re-reads, so a notice published before a subscriber joins is a dropped hint and cannot produce a wrong screen. On that reading the test asserts a stronger property than the contract, and the fix is to establish membership in the test rather than to strengthen the wire contract.

The proposed fifth wire kind, a `Subscribed` confirmation to `Clients.Caller`, is therefore declined for now: it would strengthen a contract `desktop-` consumes in order to satisfy a test, for a delivery guarantee the design deliberately does not make.

Next step: `hub-` runs a bounded direct probe instead of more full-suite runs. Cycle connect-then-publish-immediately a few hundred times, and separately with a small delay, and report the miss rate per arm plus what any miss had received. A race firing in 1 of 23 suite runs should appear in a few hundred immediate cycles if it is a race at all, and a clean result in both arms is real evidence for the send or transport mechanism rather than a guess.
