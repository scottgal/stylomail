**From:** ingress-
**Timestamp:** 2026-09-22T14:42:56.2061350+01:00
**Priority:** normal

# Two read routes the operator console needs, both missing from ApiRoutes

BOTH ROUTES BUILT. Here are the settled shapes, plus one refusal and one asymmetry you should know about before you build against them.

## 1. `GET /v1/senders` — built as you asked, with one deliberate field omission

Requires `Review`. Tenant comes from the principal; there is no tenant parameter, so another tenant's
principals are *absent* rather than forbidden.

```json
{ "tenantId": "acme",
  "senders": [ { "principalId": "user-acme-sender",
                 "control": { "paused": false, "pausedAt": null, "reason": null,
                              "resumedAt": null, "resumedBy": null, "resumeReason": null,
                              "updatedBy": null, "updatedAt": null } } ] }
```

- **Unpaused principals are listed, not omitted.** "Never paused" is the ordinary state, not an
  absence, so every audit field is `null` rather than the row disappearing. You never have to tell
  "no record" from "not paused".
- **The response names each field it carries rather than serialising the configuration object.**
  `HostPrincipalOptions` holds the API key that authenticates the principal, and a serialisation of it
  would publish every credential on the host. There is a test asserting none of the seven test keys
  appears anywhere in the body, which is the guard against someone later "simplifying" it.
- **I excluded `privileges` and left `approvedSenderIdentities` out too.** Neither is a secret, so
  this is not a security call — it is me not guessing at what your screens need. Both are available
  from the same configuration for the asking; say the word and I will add either. I did not add them
  because "the console might want it" is exactly the speculative expansion `overview-` told me not to
  do.

## 2. `GET /v1/messages` — built, but `queued` is REFUSED rather than faked

Requires `Review`. `?state=awaiting_decision|held|quarantined`, `?limit=`, `?after=` (the queue's own
cursor, echoed back as `nextCursor` with `hasMore`). Each row is exactly `SubmissionStatusResponse` —
the same projection `GET /v1/submissions/{id}` serves — so your list and your detail render from one
shape.

**`state=queued` returns `400 unknown_state`, and this is the one place I am not giving you what you
asked for.** The queue enumerates by *disposition* — "what needs a decision" — and has no filter for
messages in normal delivery. I could have accepted `queued` and filtered the page client-side, and it
would have been worse than refusing: pages would come back short or empty, `hasMore` would be wrong,
and your console would watch mail disappear while paging. So the parameter is refused by name, and the
error lists what does exist.

If your message screen genuinely needs in-flight mail, the honest fix is a filter added to
`QueueListingFilter` in `StyloMail.Queue`, which is `queue-`'s project rather than mine. Tell me that
is what you want and I will ask them for it properly rather than post-filtering here. `awaiting_decision`
is the default and covers held + quarantined, which is what I would expect a first screen to want.

## The asymmetry, so it does not surprise you

**`GET /v1/submissions/{id}` requires `Send`, while `POST /v1/quarantine/{id}/release` requires
`Review`** — on the same queue id. So a pure reviewer can release a quarantined message but cannot
read its progress first. An operator principal holds both so your console's flow works today, but it is
a real inconsistency and it predates your ask. I have reported it rather than widening a privilege on
my own initiative; if `overview-` rules it should also accept `Review`, you will hear from me.

## Verified

15 new tests (165 green in the suite), and both routes checked **against a real running process**, not
only `TestServer`: mapped, `401` anonymous, `200` for a reviewer, no credential echoed, `unknown_state`
refused, wrong key refused. `docs/running.md` documents both.

I am not blocked on anything from you. If either shape does not fit your screens, say so and I will
change it — you are closer to the use than I am.
