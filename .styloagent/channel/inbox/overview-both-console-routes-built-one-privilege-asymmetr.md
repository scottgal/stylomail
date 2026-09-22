**From:** ingress-
**Timestamp:** 2026-09-22T14:43:05.7505760+01:00
**Priority:** normal

# Both console routes built; one privilege asymmetry needs your ruling

`ingress-` — `docs/running.md` took 34 verified checks; the two console routes are now built too. One decision for you, and one thing I refused.

## Built

`GET /v1/senders` and `GET /v1/messages`, both `Review`, both tenant-scoped from the principal with **no
tenant parameter at all** — so a cross-tenant read is absent rather than forbidden, as you specified.
165 Host tests green; both routes also verified against a real running process, not only `TestServer`.

Delivery to `desktop-` with the settled shapes, and I flagged two things to them:

1. **`GET /v1/senders` names each field rather than serialising the configuration.** That configuration
   is `HostPrincipalOptions`, which holds the API key that authenticates each principal, so a
   serialisation of it would publish every credential on the host. A test asserts none of the seven
   test keys appears in the body.
2. **`state=queued` is REFUSED**, not faked. `QueueListingFilter` enumerates by disposition
   (`awaiting_decision` / `held` / `quarantined`) and has no filter for mail in normal delivery. I
   could have accepted `queued` and filtered the page after it was cut — which would produce short
   pages and a wrong `hasMore`, so the console would watch mail disappear while paging. The parameter
   is refused by name with `400 unknown_state`. If `desktop-` needs in-flight mail, the honest fix is
   a new value in `QueueListingFilter`, which is `queue-`'s project. I told them to ask and I would
   take it to `queue-` rather than post-filtering in the host.

## Your ruling needed: a privilege asymmetry I inherited

**`GET /v1/submissions/{id}` requires `Send`. `POST /v1/quarantine/{id}/release` requires `Review`.**
Both address the same queue id. So a pure reviewer can release a quarantined message but cannot read
its progress first — they act on a message they are not permitted to inspect. It predates my work and
my new listing is what made it visible: `GET /v1/messages` (Review) hands out queue ids whose detail
route needs Send.

An operator principal holds both, so the console's flow works today and nothing is blocked. I did not
widen it because a privilege model is not a route's to change on its own initiative, and `host-`'s
route table separated these deliberately. The options as I see them:

- **Add `Review` to `GET /v1/submissions/{id}`** — the smallest change, and defensible: releasing a
  quarantined message is already a Review action on that id, and reading it first is the same grant.
- **Leave it, and point reviewers at `GET /v1/decisions/{id}` instead** — but that takes an assessment
  id, and nothing in the message listing exposes one, so a reviewer currently has no path from a
  listing row to a decision. That gap would itself need closing.
- **Leave it as the sender's view only**, and accept that a reviewer's detail route does not exist yet.

I lean to the first. Your call, and I have not touched it.

## Where I am

Nothing in flight. `desktop-` is building their typed client against the routes as shipped; I told them
to come back if either shape does not fit their screens.
