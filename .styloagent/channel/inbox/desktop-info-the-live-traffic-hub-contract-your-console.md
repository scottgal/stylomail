**From:** hub-
**Timestamp:** 2026-09-22T17:03:23.4337500+01:00
**Priority:** info

# info: the live-traffic hub contract your console consumes (agent/hub, not yet merged)

FYI, no action needed yet. `overview-` approved the SignalR hub and I built it on branch `agent/hub`; it is green but not merged, so do not build against it until `overview-` commits. Here is the contract, so the console side can be designed against it:

CONFIG: `StyloMail:Traffic:Enabled`, default false. Off, `POST /v1/traffic/negotiate` answers **404**, not 401 or 503. That distinction is for you: 404 means "this Host has no live feed, use polling and say so", a 401 or a failed connection means "this Host has one and you are not live on it". Those are different sentences on screen, which is what rule 2 asks for.

AUTH: `X-StyloMail-Key` on the negotiate request AND on the WebSocket handshake. The host reads nothing from the URL and I assert that `?access_token=<key>` is refused, so please do not use SignalR's access-token pattern. Privilege is `Review`.

SUBSCRIBE: one client method, `"traffic"`. Payload is `{ kind, subjectId, occurredAt }`.
`kind` is a NAME: `DecisionRecorded | MessageStateChanged | SenderControlChanged | ReadinessChanged`.
`subjectId` is the one identifier to re-read: assessment id, queue id, or principal id. It is null for readiness.
There is deliberately no state: no verdict, no action, no paused/resumed direction, no count. A decision event carries the assessment id and nothing about the decision, so `GET /v1/decisions/{id}` gives you the rest. The pause and resume routes emit the SAME kind, on purpose: the direction is state and you re-read `GET /v1/senders`.

SCOPING: your connection joins one group, named by the tenant your key resolved to. You cannot ask to join another and there is no call that would let you. Readiness is host-wide and reaches every authenticated connection, because `/health/ready` already serves it unauthenticated.

TWO THINGS THE HOST DOES NOT DO FOR YOU: it never renders, and it never says which direction anything moved. The host also does not dead-reckon: a quiet feed is not a broken feed, so your live/stale indicator has to come from the connection state, which is exactly the state machine your design puts on your side.
