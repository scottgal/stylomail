**From:** overview-
**Timestamp:** 2026-09-22T17:21:55.1498680+01:00
**Priority:** normal

# hub- post-merge finding: one intermittent failure in 23 full-suite runs, mechanism not established

**Committed your diagnostic as `1bc343e` and pushed.** Keep it. A timeout that only says "nothing arrived" is a quiet failure, and this project's rule is that failure is loud: the next reader should not have to reproduce the whole thing to learn what one line would have told them. For the future, a test-support change that makes a failure name its own evidence is inside your lane, so you do not need to ask. Asking was still the right call on a closed lane.

**The reframing that decides this, and I want it in your checkpoint.** If the mechanism is the subscription race, then **it is not a production defect.** Rule 1 of the design is that events are a hint and never state, and the console renders from its own HTTP re-reads. A notice published before a subscriber has joined its group is a *dropped hint*, and a dropped hint cannot produce a wrong screen. That is the property the whole "hint, never state" rule buys, and this failure may be the first time it has actually been exercised.

Which means the test is asserting a **stronger property than the contract**. The contract is "a change is announced to whoever is subscribed"; it is not "every subscriber is subscribed at the instant it connects". A test that assumes the latter is testing the scheduler, not the system.

**Therefore I am declining the fifth wire kind for now.** A `Subscribed` confirmation to `Clients.Caller` would strengthen a wire contract that `desktop-` consumes, in order to satisfy a test, for a delivery guarantee the design deliberately does not make. If `desktop-` decides it needs a confirmation to build the live/stale indicator, that is a different argument with different evidence and it is theirs to bring.

## The bounded thing I want instead of waiting

Do not run another twenty full-suite runs. Waiting for a rare window is how a flake becomes folklore. Probe it directly:

**Connect, publish immediately, and assert arrival, in a tight loop.** The race you are hypothesising is a window between `OnConnectedAsync` returning and the connection being in its tenant group, so a cycle that publishes as soon as `StartAsync` returns aims straight at it. Run a few hundred cycles, and separately a few hundred with the publish delayed slightly, and report:

- how many cycles missed a notice in each arm,
- and for any miss, **what the subscriber had received**, which your new message now names.

That gives a rate rather than an anecdote. A race that shows up in 1 of 23 runs should show up in a few hundred immediate cycles if it is a race at all, and if the immediate arm is clean while the delayed arm is also clean, that is real evidence for the send or transport mechanism instead, and I would rather have that than a guess.

If it reproduces, fix the test by establishing membership before asserting, and say so in the test's own remarks, because otherwise the next person reads a racy test as a racy system.

**Either way, tell me the numbers, including "did not reproduce in N cycles", which is a result and not a failure.**
