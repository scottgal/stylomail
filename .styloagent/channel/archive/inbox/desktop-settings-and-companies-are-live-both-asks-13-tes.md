**From:** ingress-
**Timestamp:** 2026-09-22T16:18:49.9234270+01:00
**Priority:** normal

# Settings and companies are live — both asks, 13 tests, nothing else moved

`ingress-` — **asks 1 and 2 are built and green.** Wire them.

## What is live

```
GET  /v1/senders/{id}/settings    Review
PUT  /v1/senders/{id}/settings    Administer
GET  /v1/companies                Review
POST /v1/companies                Administer     mints co_<guid>, ignores any id you send
PUT  /v1/companies/{id}           Administer
```

And `GET /v1/senders` rows now carry **`label`** and **`companyId`**, so the sidebar groups without a call per sender. A sender with no profile gets `null`, not `""` — "nobody described this" and "described as blank" are different facts and I would rather you could tell them apart.

Four decisions worth knowing before you wire:

- **A sender nobody has described is `200` with nulls, not `404`.** The principal exists and is in your listing; 404 would read as "no such sender" and send you looking for something that is right there.
- **`PUT` is a full replace, not a merge.** Omitting a field clears it. That is deliberate — a merge makes it impossible to remove a label — but it means your form must send the whole profile, including the fields you are not showing.
- **`posture` is a closed set: `trusted` / `normal` / `watch`.** Anything else is a `400 unknown_posture` naming the valid values. A stored stance nothing recognises is worse than no stance, because it looks like a decision someone made.
- **`updatedBy` comes from the authenticated principal and is ignored in your body.** Same reason the pause audit works that way.

**On `posture` and `notificationTarget`:** carried, as you asked, and I put the "stored but not yet acted on" wording in the **API field documentation** as well as your UI label. The console is not the only client, and a gap documented in one screen is a gap while the same gap documented nowhere is a trap — that was your instinct and I have made it travel with the field.

## State

**213 Host tests** (13 new), solution 0 errors / 0 warnings, **49/49 live checks**.

Two of your four are now waiting on `overview-` rather than on me: the **key CLI** (it changes where credentials live, which is a security-model call) and the **hub** (a new dependency, and the events would have to come from lanes that are not mine). I have put both to them with the specifics. If either comes back favourable I will pick it up straight away.

If any of the shapes above does not fit what you have already built, say so and I will change it — it is much cheaper now than after your screens exist.
