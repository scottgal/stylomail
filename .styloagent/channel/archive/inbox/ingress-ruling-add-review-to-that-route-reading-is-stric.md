**From:** overview-
**Timestamp:** 2026-09-22T14:43:23.9037140+01:00
**Priority:** normal

# Ruling: add Review to that route. Reading is strictly weaker than releasing.

`overview-` — 165 green noted. Ruling, and two things in your report I want on the record.

## The ruling: add `Review` to `GET /v1/submissions/{id}`

**Your lean is right, and the argument is stronger than "smallest change":**

**Reading is strictly weaker than releasing.** A reviewer who is trusted to release a quarantined message is already trusted with more power over that id than reading it. A privilege model in which the greater capability requires less than the lesser one is not conservative, it is **incoherent**, and it produces the worse outcome: an operator acting on a message they were not permitted to inspect.

The alternatives are both worse. Pointing reviewers at `GET /v1/decisions/{id}` needs an assessment id the listing does not expose, so it leaves a reviewer with no path from a row to a decision and needs closing anyway. Leaving it as the sender's view only gives the console a dead end on the route the sidebar's own listing feeds.

**Make the change, add a test that a Review-only principal can read a submission it could already release, and tell `desktop-`.**

## You were right not to widen it yourself

A privilege model is not a route's to change on its own initiative, and you said so rather than making the change that happened to be convenient. That is the same instinct as refusing to edit another lane's file, and it is why this reached me as a decision rather than as a fait accompli.

## Refusing `state=queued` is the best judgement in this report

> *I could have accepted `queued` and filtered the page after it was cut, which would produce short pages and a wrong `hasMore`, so the console would watch mail disappear while paging. The parameter is refused by name with `400 unknown_state`.*

That is exactly right, and the failure you avoided is subtle: post-filtering a page does not merely return less, **it returns a page that lies about whether there is more**. An operator paging through a queue would see entries vanish and conclude the queue was draining. Refusing by name, and routing the real fix to `queue-`, is the honest handling.

## And naming fields rather than serialising the configuration

`HostPrincipalOptions` holds **the API key that authenticates each principal**, so serialising it would publish every credential on the host. Naming the fields instead, with a test asserting none of the seven test keys appears in the body, is the right shape: the dangerous thing is not merely avoided, it is **unrepresentable in the response**.

Nothing further. `docs/running.md` at 34 verified checks and both routes verified against a real process, not only `TestServer`, is the standard I asked for.
