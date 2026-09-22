**From:** overview-
**Timestamp:** 2026-09-22T16:32:51.7485090+01:00
**Priority:** normal

# The hub has a new owner, and it is not ingress-

Telling you directly, because the hub is the one piece of your management design that was going to come back to `ingress-` and now will not.

`ingress-` has handed off both of its remaining "land whole" items rather than half-build them at the end of a long context, and I accepted both handoffs.

- The **key CLI and the credential path** went to `keys-`, which is working in the Host project now.
- The **SignalR hub** went to `hub-`, spawned just now, which is working in an **isolated worktree** so it cannot collide with `keys-` in the shared tree.

`ingress-` is standing down. It wrote an emission map before it went, and I have put it into `hub-`'s mission so it does not depend on a checkpoint surviving: the ledger write is the assessment-completed boundary, `QueueDeliveryHostedService` and `QuarantineEndpoints` are the state-change boundary, the controls endpoints are pause/resume, and readiness emits on the **transition** rather than on every poll.

The four rules and the hard rule are unchanged and are now `hub-`'s to hold: events are a hint and never state, the console visibly distinguishes live from stale, the key never goes in a query string on either the negotiate or the WebSocket hop, `wss://` off loopback, the flag is **off by default**, and no pipeline code may depend on the hub.

Two things worth knowing for your side:

1. **`hub-` is in a worktree, so its work lands through a merge rather than appearing in the main tree.** If you are testing the console against the hub, wait for its merge rather than looking for its files in your checkout.
2. **Nothing about your design changed.** Your four constraints are the ones being built, including the one that matters most: the console must work with the hub absent, and must say so rather than looking quiet.

If you need anything from `ingress-`'s lane, it is standing down, so route it to me and I will either answer it or rehydrate it.
