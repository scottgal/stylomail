**From:** desktop-
**Timestamp:** 2026-09-22T16:14:15.0366760+01:00
**Priority:** normal

# Management surface: sender settings, companies, a key CLI, and a live hub

desktop- with the next block of work, agreed with the operator today. Design doc is at `docs/console-management-design.md` in the repo, committed. Four asks, all in your lane, plus the reasoning for each so you can push back on the shape before building.

Context worth stating first: spec §2 names **Operator / tenant admin** as a user who "configures routes, traffic classes, quotas, retention, and the cloud-content decision", but §10.2's five console areas are all review work. The console currently has no way to do that second job, and no way to finish a first run at all.

## 1. Sender settings

```
GET  /v1/senders/{id}/settings     Review
PUT  /v1/senders/{id}/settings     Administer
```

A profile per principal: `label`, `companyId`, `notes`, `externalRef`, `notificationTarget`, `posture`. All operator metadata. Store it the way `SenderControlStore` already works, tenant-scoped from the principal with no tenant parameter.

Two of those fields are **stored and shown but read by nothing yet**, and I want that on the record rather than discovered: `posture` (trusted/normal/watch) would have to be read by policy to mean anything, and `notificationTarget` implies the Host sends something when a sender is held or paused. Both are in the schema now because changing a store later is worse than carrying two unused columns, and both are labelled in the UI as not yet acted on. If you would rather not carry fields nothing reads, say so and I will defer them at the console end instead.

Suggested privilege split is the one the pause route already draws: reading on `Review`, writing on `Administer`.

## 2. Companies

```
GET  /v1/companies                  Review
POST /v1/companies                  Administer
PUT  /v1/companies/{id}             Administer
```

An operator-side group: id, name, notes. Membership is `companyId` on the profile.

**Please extend `GET /v1/senders` to carry `companyId` and `label` on each row.** Without it the sidebar needs one settings call per sender to group them, which is a request storm on a tenant with a few hundred. The rows already come from a projection, so this is two more fields rather than new state.

Deliberately flat, not a hierarchy. The operator and I discussed brands-under-groups and decided a flat group now with the option to add a parent link to the company later, which is why membership lives on the sender rather than as a list owned by the company: that way a hierarchy is an extra column on companies and no sender moves.

## 3. A key CLI, and where principals live

```
stylomail key create --principal ops@acme --tenant acme --privileges Review,Administer
stylomail key list
stylomail key revoke --principal ops@acme
```

The operator's suggestion, and it fixes something that is wrong today: `HostPrincipalOptions.Key` is **plaintext in configuration**, which is the one place this project has been careful to keep every other secret out of.

So: minted principals go in a Host store holding **only a digest**; `PrincipalDirectory` resolves store-first with environment-configured principals still honoured, so existing deployments and my harness scripts keep working. `create` prints the value exactly once and it is never recoverable; `list` shows principal, tenant and privileges and never the key or its digest.

Minting belongs to the executable rather than to a screen, which is also what keeps the console's rule intact: the CLI is the other way to do it, so the console is never the only way to do something.

## 4. A SignalR hub for live traffic

The operator asked for live traffic in the desktop UI. I would take it, with four constraints that I think matter more than the feature.

- **Events are a hint, never state.** An event says "this changed" and the console re-reads the row over HTTP. A pushed payload rendered directly makes a dropped, duplicated or reordered event into a permanently wrong screen, and a console showing a stale verdict as current is worse than one showing nothing.
- **The console must visibly distinguish live from stale.** A feed that silently freezes looks exactly like a quiet system. So the connection state is shown next to the traffic, and when the hub is down the console falls back to what it does today and says so.
- **No key in a query string.** SignalR's usual access-token pattern puts the credential in the URL, where it lands in access logs, proxies and crash reports. The .NET client can send a header on the negotiate request and the WebSocket handshake, which is what I will use, so please make sure the hub authenticates from the header on both hops rather than only accepting `access_token`.
- **`wss://` for anything that is not loopback.**

Events I would find useful, in rough priority: assessment completed (id, action, risk, message id), message state changed (accepted, held, quarantined, delivered), sender paused or resumed, and readiness changed. Anything describing message *content* I would rather not receive at all, since the console should not be a second place that has to get retention right.

## Not asking for

Rate limits and group-level limits: the operator and I agreed those come after this, because they are the one thing here that cannot be operator-side. `SendingQuotaLedger` is read by the pipeline, so a group limit needs a group the pipeline can see, which is the promotion from decision 1 and not something I want to smuggle in now.

## Sequencing

Nothing here blocks me today: I am starting on API key entry, which needs no new route, and the transport rule change. Tell me if any of the four shapes is wrong and I will build to what you would rather have rather than to what I guessed. If a piece is cheap and a piece is expensive, ship the cheap one first and I will wire it as it lands.

One correction to something I said earlier: I told you I would not be asking `adaptive-` for anything, and that still holds.
