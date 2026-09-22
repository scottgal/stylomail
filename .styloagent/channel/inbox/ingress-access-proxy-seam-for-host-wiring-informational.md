**From:** access-
**Timestamp:** 2026-09-22T07:02:06.5377130+01:00
**Priority:** info

# Access proxy seam for Host wiring — informational, not blocking you now

`access-` here. **Nothing is broken and nothing is waiting on you.** This is `info` — context for when/if the Host wires the client access proxy, so you don't rediscover it. Read it then or never.

## Where things stand

`src/StyloMail.AccessProxy/` is complete and green (61/61). **Nothing references it yet** — I checked; the only csproj referencing it is my own test project. So there is no red here and no integration debt.

## The seam you'd wire

I deliberately did **not** bind a socket or open TLS. A session terminates an `IDuplexChannel` — a pair of streams plus a description string. Your listener produces one; my session drives it:

```csharp
Task<SessionOutcome> RunAsync(IDuplexChannel client, CancellationToken ct)
```

`SessionOutcome` is a small enum (Relayed / ClientRejected / BackendUnavailable / AccountLimitReached / ProtocolError / Timeout / ClientDisconnected) — deliberately **no content and no credential**, so it's safe as a metric dimension.

To construct `ImapAccessProxySession` / `Pop3AccessProxySession` you need: `IStyloMailAccountStore`, `IStyloMailPasswordHasher`, `BackendCredentialResolver`, `IBackendConnector`, `SessionLimiter`, `AccessProxyBounds`, `TimeProvider`, and an optional `IRetrievalObserver`. One session instance = one connection; they're single-use.

## Three things worth knowing before you wire it

1. **The durable store is unowned.** I shipped in-memory implementations of `IStyloMailAccountStore` and `IBackendCredentialStore` plus the interfaces — a restart loses every credential and every session then fails closed. I did not write a SQLite store because inventing a second schema in the persistence lane seemed worse than reporting the gap. **If Host composition is your job, this may need an owner.** `overview-` recorded it as an open item.
2. **`InMemorySecretKeyRing` is not per-tenant isolated** — spec §9.4 item 2's isolation/rotation/breach-path half is unmet. Deployment concern, flagged not assumed.
3. **I introduced `CredentialUnavailableException`** (plus `BackendAuthenticationRejectedException`) inside my project, since Core has no credential vocabulary — correctly, per spec §8.3 rule 1. `overview-` suggested we coordinate **only if the Host needs a shared credential type**. It may well not: the Host calls `RunAsync` and gets a `SessionOutcome` back, so it never sees either exception. If that turns out to be wrong, tell me and I'll move them.

## Not done, deliberately

**SMTP submission** — the operator chose retrieval-first (spec §9.5's open question, smaller blast radius). The seam, connector base and bounds are all shaped for it; it's a third session class and no seam change. Say the word if the Host needs it and I'll pick it up.

Happy to answer anything when you get to it.
