**From:** hub-
**Timestamp:** 2026-09-22T17:20:42.3209100+01:00
**Priority:** normal

# hub- post-merge finding: one intermittent failure in 23 full-suite runs, mechanism not established

The lane is closed and I am standing by as instructed, but one thing outlived the closure and you should have it. Checkpoint is at `.styloagent/channel/saved-context/hub-context.md`.

WHAT HAPPENED
On the **first full-suite run on merged `main`**, `TrafficEmissionTests.A_pause_and_a_resume_are_announced_as_the_same_kind_of_change` failed once: a 15 second timeout waiting for the second of two notices. It has not happened since, across 6 isolated runs, 4 sequential full-suite runs, and **8 more full-suite runs with two instances concurrent** (raised deliberately, because the failure appeared under the load of the merge). 300/300 on every one of those. So: **1 failure in 23 full-suite runs, and I could not reproduce it.**

I did not attribute it to load and move on, because that is the guess this project punishes. Two candidate mechanisms, and the one thing that tells them apart:

1. **The subscription race.** `OnConnectedAsync` adds the connection to its tenant group after the client's `StartAsync` returns, so a notice published in that window goes to a subscriber that has not joined. The failing test connects and publishes immediately. This predicts the failure saw **one** notice.
2. **A notice lost in the send or the transport.** This predicts it saw **none**.

THE ONE UNCOMMITTED FILE
The failure message could not tell those apart, so I changed it: `NextAsync` now names **what it actually received before giving up**, kinds and subject ids. That is `tests/StyloMail.Host.Tests/TrafficTestSupport.cs`, +19/-4, and it is the **only dirty file in main**. I left it uncommitted rather than committing to `main` on my own authority after the lane closed, but I do not want to revert it either: it is what makes the next occurrence diagnosable from one line instead of a reproduction. Tell me which you want and I will do it.

I would not raise the timeout. The test waits 15 seconds for a notice, so time is not the missing thing, and a longer window makes a real defect rarer rather than absent. If it is (1), the honest fixes are a `Subscribed` confirmation to `Clients.Caller` in `OnConnectedAsync` (which is also what a live/stale indicator wants, but it is a fifth wire kind and a change to a contract `desktop-` has) or establishing membership in each affected test before asserting.

NOTHING ELSE IS OUTSTANDING. Everything else in the lane is green, merged and closed; the probe is 15/15; the flaky candidate is the only open question, and it is written into the checkpoint as an open question rather than as a conclusion.
