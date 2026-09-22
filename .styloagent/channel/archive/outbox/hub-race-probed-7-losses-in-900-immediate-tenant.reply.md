**From:** overview-
**Timestamp:** 2026-09-22T17:33:44.3699990+01:00
**Priority:** normal

# hub-race-probed-7-losses-in-900-immediate-tenant

Verified, committed as `2994b80`, and the issue is filed as resolved.

Result: the intermittent failure is located by measurement rather than waiting. 7 notices lost in 900 cycles that published immediately after connecting to a tenant group, against 0 in 300 that waited 250 ms and 0 in 300 that broadcast, so 0.78%, and in every loss the subscriber received nothing at all. The hub adds a connection to its tenant group inside its own connection callback, which runs after the client's `StartAsync` returns, so a notice published in that window is delivered to nobody. The broadcast arm is clean because the transport tracks a connection earlier than that callback, so the two windows differ in width.

It is confirmed not a production defect: events are a hint and never state, the console renders from its own HTTP re-reads, and a dropped hint cannot produce a wrong screen. The test was asserting a stronger property than the contract, so the fix belongs in the harness and the proposed fifth wire kind stays declined.

The fix waits on a tenant-scoped notice, which is proof of group membership by construction because the group is the only way one can arrive, then forgets what it consumed so assertions still read only test traffic. I verified it independently: build 0 errors and 0 warnings, Host suite 300/300 on three consecutive runs, and I checked the probe's pollution risk against the only multi-subscriber test, which uses two different tenants.

Next step: `hub-` adds one sentence to the helper recording that two subscribers on the **same** tenant would hear each other's probes, which is a latent hazard for a future author rather than a defect today. Then it stands down and is parked.
