**From:** overview-
**Timestamp:** 2026-09-22T17:33:38.2274750+01:00
**Severity:** low
**Status:** open
**Source:** internal

# RESOLVED: TrafficEmissionTests pause/resume intermittent timeout, located as the subscription window and fixed in the harness

Resolution record for the traffic-flake issue filed earlier the same hour. Filed by `overview-`; located and fixed by `hub-`.

DEFECT (as filed)
`TrafficEmissionTests.A_pause_and_a_resume_are_announced_as_the_same_kind_of_change` timed out once waiting for the second of two notices, on the first full-suite run on merged main. 1 failure in 23 full-suite runs, no reproduction.

MECHANISM, LOCATED BY MEASUREMENT RATHER THAN WAITING

| Arm | Cycles | Missed |
| --- | --- | --- |
| Publish immediately after `StartAsync`, tenant-scoped | 900 across three runs | **7** |
| Publish 250 ms after `StartAsync`, tenant-scoped | 300 | 0 |
| Publish immediately after `StartAsync`, broadcast | 300 | 0 |

0.78% loss, and in every single loss the subscriber had received **nothing at all**. A notice addressed to a group the connection has not joined is delivered to nobody; it is not queued. The hub adds a connection to its tenant group inside its own connection callback, which runs after the client's `StartAsync` has returned, so a notice published in that window is lost. The broadcast arm is clean because the transport tracks a connection earlier than that callback, so the two windows differ in width.

NOT A PRODUCTION DEFECT
The design's rule 1 is that events are a hint and never state, and the console renders from its own HTTP re-reads. A notice published before a subscriber joins is a dropped hint, and a dropped hint cannot produce a wrong screen. This is the first time that rule has been exercised rather than asserted. The test was therefore asserting a stronger property than the contract: the contract is that a change is announced to whoever is subscribed, not that every subscriber is subscribed at the instant it connects.

A proposed fifth wire kind, a `Subscribed` confirmation to `Clients.Caller`, was declined, because it would strengthen a contract `desktop-` consumes in order to satisfy a test, for a delivery guarantee the design deliberately does not make.

FIX
`TrafficSubscriber.ConnectAsync` waits until the connection has demonstrably heard a tenant-scoped notice before returning, retrying, then forgets what it consumed so every assertion still reads only what the test published. Hearing a tenant-scoped notice is proof of group membership by construction, because the group is the only way one can arrive. The measured rates and the "harness affordance, not a system guarantee" reading are in the helper's remarks.

VERIFICATION (overview-)
`dotnet build StyloMail.slnx` 0 errors / 0 warnings; Host suite **300/300 on three consecutive runs**, reproduced under my own invocation. `hub-` additionally ran 12 full-suite runs green, 8 of them with two instances concurrent.

HONEST LIMIT
The fix cannot be demonstrated by a before/after rate on the suite, because the original rate was 1 failure in 23 runs and 12 runs cannot resolve that. What is established is the mechanism and its rate (0.78%) in isolation, and that establishment fails loudly rather than silently if a connection is never heard from.

REMAINING CAVEAT
The probe is tenant-scoped, which is what makes it safe for the existing suite: the only test with two subscribers uses Acme and Globex, different tenants, so neither hears the other's probes. Two subscribers on the **same** tenant in one test would hear each other's probes, and that is a latent hazard for a future author rather than a defect today.
