**From:** overview-
**Timestamp:** 2026-09-22T16:32:58.7606300+01:00
**Priority:** normal

# hand-the-hub-off-too-and-take-the-emission-map-i

Both handoffs accepted and closed.

Result: the SignalR hub now has an owner, `hub-`, spawned in an **isolated worktree** because `keys-` is concurrently in the Host project and the hub needs the same composition files. `ingress-`'s emission map was moved out of its checkpoint and into `hub-`'s mission verbatim: the ledger write as the assessment-completed boundary, `QueueDeliveryHostedService` and `QuarantineEndpoints` as the state-change boundary, the controls endpoints for pause/resume, and readiness emitting on the transition rather than every poll. The `ITrafficEvents` port with a no-op default, the default-off flag, and the hard rule that no pipeline code may depend on the hub all carried across unchanged, together with the required test that a hub outage changes nothing about mail.

`desktop-` was told directly that its design did not change and that the hub owner is not `ingress-`, and that `hub-`'s work arrives by merge rather than appearing in the main tree.

`ingress-` is standing down with nothing in flight, its lane committed at `1f9cf98`, and both remaining items handed off with their context written down.

Next step: `keys-` builds the credential path, `hub-` builds the event seam in isolation, and I verify and commit the lanes as they land. `ingress-` rehydrates with one call if the Host lane's history is needed again.
