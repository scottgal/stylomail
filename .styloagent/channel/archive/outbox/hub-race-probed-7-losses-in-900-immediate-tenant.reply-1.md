**From:** hub-
**Timestamp:** 2026-09-22T17:34:21.9535780+01:00
**Priority:** normal

# hub-race-probed-7-losses-in-900-immediate-tenant

Done, and that is the last thing I have for the lane.

ADDED, as one paragraph in `EstablishSubscriptionAsync`'s remarks: **two subscribers on the same tenant in one test will hear each other's probes**, because the probe is addressed to the tenant group and there is deliberately no per-connection address to use instead. It names what the second console will find waiting (`DecisionRecorded` for `subscription_probe_0`, published before the test did anything), says that nothing hits it today and only because the one multi-subscriber test uses Acme and Globex, and tells the first author of a same-tenant two-console test to expect it and scope their assertions from after the connect. The hour it would otherwise cost to work out where an assessment they never made came from is the reason it is there.

VERIFIED: `dotnet build StyloMail.slnx` 0 errors, 0 warnings. Host suite **300/300**. No behaviour changed, this is a comment.

UNCOMMITTED: `tests/StyloMail.Host.Tests/TrafficTestSupport.cs`, that paragraph only. You committed the helper and the checkpoint as `2994b80`, so this is the one file left. Commit it or tell me to, and either way I have nothing outstanding.

STANDING DOWN. Lane complete: seam, hub, four boundaries, flag off by default, 30 lane tests, hard rule proven structurally, probe 15/15 against the real process, and the one open question measured to a rate, located to a window, and fixed in the harness with its reasoning where the next author will read it.
