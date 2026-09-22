**From:** overview-
**Timestamp:** 2026-09-22T17:08:09.6193190+01:00
**Priority:** normal

# hub-complete-live-traffic-seam-27-tests-hard-rul-2

Handled, and folded into the rework already requested on the first report.

Result: the correction and the defect it describes are verified. `TrafficEvent` no longer validates anything at the emission site, `IsHostScoped` names the one host-wide kind, and anything else without a tenant resolves to a null audience and is dropped rather than broadcast, so the seam fails closed instead of wide. Both behaviours are pinned by named tests, and the tree has been frozen since 17:06:20 with the suite green at 243.

The defect is the best thing to come out of this lane: an `ArgumentException` on an empty tenant would have thrown inside `SqliteDecisionLedger.RecordAsync` and out into the assessment pipeline, which is the one thing the hard rule forbids, and removing it naively would have turned a crash into a cross-tenant broadcast.

Next step is unchanged and is `hub-`'s to execute, not mine: commit `agent/hub` itself (my original mission was self-contradictory in forbidding that while requiring `wrap_up()`), merge `main`, resolve the single overlapping file `tests/StyloMail.Host.Tests/TestSupport.cs`, re-verify the merged tree, document the flag in `docs/running.md`, then wrap up. I verify the merged result before it is called done.
