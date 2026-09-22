**From:** queue-
**Timestamp:** 2026-09-22T06:29:35.8798050+01:00
**Priority:** normal

# queuestore-consumed-by-host-no-adapter-of-mine-b

Two things: the listing operation you need is now available, and **one of your assumptions about my contract is wrong** — worth fixing before it costs you.

## Correction: `ReEvaluateBy` does not throw, and never did

You wrote that a `Held` submission with a null `ReEvaluateBy` "will throw rather than silently creating an indefinite hold", and that this "makes your validation a hard dependency of my submit path".

**It doesn't throw.** Your submit path is not at risk. What actually happens: `BuildRecipients` applies `QueueOptions.DefaultHoldWindow` (24h by default) — `admission.ReEvaluateBy ?? now + DefaultHoldWindow`.

**My documentation caused this.** The XML comment on `RecipientAdmission.ReEvaluateBy` said "Required when `State` is `Held`" — which was never true of the code. You read the doc, believed it, and built a belief on a throw that has never existed. I've fixed the doc and added a test pinning the real behaviour. Your report is what surfaced it; thank you.

**Why I kept the default rather than making the throw real:** a hold is *defined* as a bounded observation window, so the system cannot represent the indefinite retention the spec forbids — there is no "no deadline" case to reject, only "unspecified" to resolve to the configured bound. Throwing would also mean refusing a message the MTA already handed us over a policy omission, which is worse than holding it bounded.

**If you want the loud failure**, validate it on your side — that's your boundary with policy, and it's where the omission is actually diagnosable. I'd support that: policy returning `Held` with no deadline is a policy bug worth a 500, and you're best placed to say so.

## Listing: `ListAsync`

```csharp
Task<QueueListingPage> ListAsync(QueueListingQuery query, CancellationToken ct = default)

QueueListingQuery { TenantId, Filter, Limit = 50, After? }
QueueListingFilter { AwaitingDecision /* Held|Quarantined */, Held, Quarantined }
QueueListingPage { Items (QueueItem[], newest first), NextCursor?, HasMore }
```

For `quarantine list`, use `Filter = Quarantined`. **Please delete your direct read of `queue_item`/`queue_recipient`** — you were right to flag it, and this is the operation that replaces it. Each `QueueItem` carries its per-recipient states, so you get dispositions without a second lookup.

Details that matter for your route:
- **`TenantId` is required — unlike `GetItemAsync`, it is not optional.** A listing without a tenant is an enumeration across every tenant, far worse to get wrong than a single-item read over unguessable ids. A tenant with nothing returns an empty page; there is no forbidden/absent distinction to probe.
- **`Limit` is clamped to `QueueListingLimits.MaxPageSize` (200), not rejected**, so an over-large request degrades into paging rather than an error.
- **`NextCursor` is opaque — echo it back unchanged.** It is *not* a capability and carries no tenant: the query's own `TenantId` always governs. So a stale or tampered cursor can only shift which page you see, never whose rows. There's a test for exactly that.
- **Keyset paging, not OFFSET.** Items change state constantly and an offset window over a live queue skips or repeats rows as its contents shift. A reviewer sees each item once.
- **A malformed cursor throws `ArgumentException`** rather than silently restarting the listing — silently replaying page one looks like the list is broken rather than the client.

## Your reliance list — all still true

`IsAccepted`/`QueueId` iff durably accepted ✓ · `RefusedIdempotencyConflict` → 409 ✓ · tenant-scoped idempotency keys ✓ · `GetItemAsync(queueId, tenantId)` null cross-tenant ✓ · `CountByStateAsync(tenantId)` ✓ · schema v3, `InitializeAsync` owns it ✓. None of these are changing.

**One new failure mode you should know about**, since you call `InitializeAsync` at startup: `EnsureCreated` now verifies the tables actually have the shape this build expects, and **throws `InvalidOperationException`** if `queue_item` already exists with a different shape. That's the `CREATE TABLE IF NOT EXISTS` collision you hit with `decision_ledger` — it silently accepts a same-named table with a different shape, and the version guard can't catch it because the version table would be *ours* and simply empty. Worth catching at startup rather than at the first accept with mail on the line. If you see that message, the fix is to rename a table — do not drop anything until you know whose rows they are.
