**From:** hub-
**Timestamp:** 2026-09-22T17:31:45.1030100+01:00
**Priority:** normal

# hub- race probed: 7 losses in 900 immediate tenant cycles, 0 in 300 delayed, 0 in 300 broadcast. Fixed in the helper.

You asked for rates rather than another twenty suites. Here they are, and the race is real and located.

THE NUMBERS
| Arm | Cycles | Missed |
| --- | --- | --- |
| Publish immediately after `StartAsync`, tenant-scoped | 300 | **2** |
| same arm, second run | 300 | **3** |
| same arm, third run | 300 | **2** |
| Publish 250ms after `StartAsync`, tenant-scoped | 300 | **0** |
| Publish immediately after `StartAsync`, **broadcast** | 300 | **0** |

**7 losses in 900 immediate tenant cycles, 0.78%.** In every single loss the subscriber had received **nothing at all**, which is what a notice addressed to a group the connection has not joined looks like: it is not queued for later, it is delivered to nobody.

So it is the subscription window, and it is specifically the **tenant group**. The broadcast path is clean, and that is not luck: the transport tracks a connection earlier than the hub's own connection callback runs, so a broadcast window and a group window are different widths. The delayed arm bounds it too: 250ms is enough for the group join, every time.

YOUR FRAMING IS WHAT THE DATA SAYS
A notice published before a subscriber has joined is a **dropped hint**, and a dropped hint cannot produce a wrong screen, because the console renders from its own re-reads. That is the property "hint, never state" buys, and this is the first time it has been exercised rather than asserted. It also confirms the test was asserting a stronger property than the contract.

THE FIX, IN ONE PLACE
`TrafficSubscriber.ConnectAsync` now waits until the connection has demonstrably heard a **tenant-scoped** notice before it returns, retrying if the first attempt is lost, and forgets what it consumed so every assertion still reads only what the test published. Hearing a tenant-scoped notice *is* proof of group membership, because the group is the only way it can arrive, so the fix is sound by construction rather than by luck. It lives in the helper so no test has to know, and the mechanism, the measured rates and the "this is a harness affordance, not a system guarantee" reading are in its remarks, which is where the next person writing a hub test will look.

`TestPrincipals.TenantFor(key)` is new, so the helper can address the tenant it just connected as. Test-support only.

VERIFIED
**12 full-suite runs green with the fix**: 4 sequential, then 8 with two instances running concurrently, which is the load the original failure appeared under. 300/300 on every one, 0 warnings, 0 errors.

One honest limit: I cannot demonstrate the fix by a before/after rate on the suite, because the original rate was 1 failure in 23 suites and 12 runs cannot resolve that. What I can say is that establishment fails loudly if a connection is never heard from, and that 900 measured cycles put the window at under 1%.

The probe harness was scaffolding and I have deleted it, per the suite's rule that scaffolding goes once it has served. Its arithmetic is in the helper's remarks and in the checkpoint.

UNCOMMITTED, TWO FILES: `tests/StyloMail.Host.Tests/TrafficTestSupport.cs` and `TestSupport.cs`. You committed my last test-support change yourself; say the word if you would rather I commit these, or commit them as you did before.
