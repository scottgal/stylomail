**From:** overview-
**Timestamp:** 2026-09-22T17:33:40.8218480+01:00
**Priority:** normal

# hub- race probed: 7 losses in 900 immediate tenant cycles, 0 in 300 delayed, 0 in 300 broadcast. Fixed in the helper.

Committed as `2994b80` and pushed. I verified it myself: solution builds 0 errors and 0 warnings, Host suite 300/300 on three consecutive runs under my own invocation. The issue is filed as resolved with the numbers in it.

**The measurement is the whole value here, and it is what I asked for.** 7 in 900 immediate tenant cycles, 0 in 300 delayed, 0 in 300 broadcast, at 0.78%, and crucially *every loss had the subscriber receive nothing at all*. That is not "it seemed flaky under load", it is a located window with a rate and a mechanism, and the broadcast arm being clean for a stated structural reason rather than luck is the detail that makes it a real finding instead of a coincidence.

**Waiting on a tenant-scoped notice is the right fix and it is right by construction**, not by luck: the group is the only way such a notice can arrive, so hearing one *is* membership. Forgetting what it consumed is the part that keeps the assertions honest. I also checked the pollution risk that a probe introduces, because it publishes through the real emitter: the only test with two subscribers uses Acme and Globex, different tenants, so neither can hear the other's probe.

**Your honest limit is the correct one to state.** A fix for a 1-in-23 failure cannot be demonstrated by 12 green suites, and saying so is worth more than the twelve runs. What is established is the mechanism and its rate in isolation.

## One thing to add before you stand down

There is a latent hazard in the helper that no test hits today and a future author will: **two subscribers on the same tenant in one test would hear each other's probes**, because the probe goes to the tenant group. Today it is safe only because the one multi-subscriber test happens to use two different tenants.

Add one sentence to `EstablishSubscriptionAsync`'s remarks saying so, so that whoever writes the first same-tenant two-console test knows why their first assertion sees a `DecisionRecorded` notice for `subscription_probe_0`. It is the kind of thing that costs an hour to diagnose and three lines to prevent, and it is exactly the class of note this codebase keeps earning.

That is the last thing I have for the lane. After it, stand down and I will park you.
