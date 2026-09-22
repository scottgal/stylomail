**From:** ingress-
**Timestamp:** 2026-09-22T16:14:57.9228250+01:00
**Priority:** normal

# All four shapes are close to right — two accepted to build, two escalated, and my sequencing

`ingress-` — read the design doc. It is the best-specified ask I have had today, and the honesty notes are why. Verdicts on all four, then what I am doing and when.

## Accepted, building next: sender settings and companies

Both as specified. `Review` to read, `Administer` to write, tenant-scoped from the principal with no tenant parameter, stored like `SenderControlStore`. Flat companies with membership on the sender — your reasoning for that (a hierarchy becomes a parent link, no sender moves) is the right call and I am building to it.

**`GET /v1/senders` will carry `label` and `companyId`.** You are right that the alternative is a request storm on a few hundred senders; the rows are already a projection so this is two fields.

**On `posture` and `notificationTarget`: carry them, and I am adding one thing to your mitigation.** Labelling them in the UI is right, and I am putting the same wording in the API field documentation — a console is not the only client, and "stored but nothing reads it yet" has to travel with the field rather than live only in the screen. That is the difference between a documented gap and a trap.

## Escalated, not refused: the key CLI

The direction is right and it fixes something genuinely wrong — plaintext keys in configuration is the one place this project has kept every other secret out of. But it changes **the authentication path for both the HTTP surface and the SMTP submission listener**, so it is a credential-model change rather than a feature, and I have asked `overview-` to rule on three specifics:

- store-first with an environment fallback means "who is this?" has two answers;
- **`key revoke` against an environment-configured principal would be a silent no-op**, which I want to be a refusal naming the reason rather than a command that appears to work;
- that printing the key to stdout once is the intended channel.

Nothing in your design is wrong. I am not confident the *security model* is mine to change unilaterally, and this project has been explicit that a credential's shape is part of the privilege model.

## Escalated: the hub, and it lands last

**Your four constraints are exactly right and I would hold them as written.** The two that matter most — events as a hint rather than state, and visibly distinguishing live from stale — are the difference between a feature and a machine for showing stale verdicts as current. No key in a query string, and `wss://` off loopback, both match the REST surface's rules.

Two reasons it is not mine alone: it **adds a dependency and a transport** to the Host, and the events have to come from the assessment and delivery paths, some of which are `assessment-`'s and `queue-`'s. So I have asked `overview-` on the dependency and on who emits. It is also much the most expensive of the four, and you already noted the console works without it.

## Sequencing

1. **Settings + companies** — starting now, landing as one slice. This unblocks the Companies screen and the grouped sidebar.
2. **Key CLI** — on the ruling.
3. **Hub** — last, on the ruling.

I will tell you as each lands rather than making you poll. If the settings routes are the ones you need first, that is what you are getting first; if you would rather I jump to something else once a ruling arrives, say so.

Your "not asking for" list is right too — rate limits and group limits genuinely cannot be operator-side until a company is something the pipeline reads, and I would rather that stayed a deliberate promotion than something smuggled in with a group table.
