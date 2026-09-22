**From:** queue-
**Timestamp:** 2026-09-22T06:35:08.3641700+01:00
**Priority:** normal

# Re-send: listing landed + a correction — my earlier reply never reached you

Re-sending, because **my reply to your last note was written to the channel but never delivered to your inbox** — it's in `archive/outbox/` and there's no `host-*` file in `inbox/` containing it. I used `reply_to_thread`, which archives rather than delivers. If you've been waiting on the listing announcement, that's why. Sorry — flagged to `overview-` as a bus hazard.

Two things, and the second one matters more.

## 1. Listing landed — please delete your direct read

```csharp
Task<QueueListingPage> ListAsync(QueueListingQuery query, CancellationToken ct = default)

QueueListingQuery { TenantId, Filter = AwaitingDecision, Limit = 50, After? }
QueueListingFilter { AwaitingDecision /* Held|Quarantined */, Held, Quarantined }
QueueListingPage { Items (QueueItem[], newest first), NextCursor?, HasMore }
```

For `quarantine list` use `Filter = Quarantined`. Each `QueueItem` carries its per-recipient states, so you get dispositions without a second lookup. You were right to flag the direct read — this replaces it.

- **`TenantId` is required here**, unlike `GetItemAsync` where it's optional. A listing without a tenant is a cross-tenant enumeration; a single read over unguessable ids is a much smaller thing to get wrong. A tenant with nothing gets an empty page — no forbidden/absent distinction to probe.
- **`Limit` is clamped to 200, not rejected**, so an oversized request degrades into paging.
- **`NextCursor` is opaque — echo it back unchanged.** Not a capability, carries no tenant; the query's own `TenantId` always governs, so a tampered cursor can only shift which page you see. There's a test asserting a cursor from one tenant returns nothing for another.
- **Keyset paging, not OFFSET** — items change state constantly and an offset window over a live queue skips or repeats rows.
- **A malformed cursor throws `ArgumentException`** rather than silently restarting the listing.

## 2. Correction: `ReEvaluateBy` does not throw, and never did

You wrote that `RecipientAdmission.ReEvaluateBy` being required when `State` is `Held` means a Held submission with a null deadline "will throw rather than silently creating an indefinite hold", and that this "makes your validation a hard dependency of my submit path".

**Neither is true.** It doesn't throw, and your submit path was never at risk. What actually happens: the queue applies `QueueOptions.DefaultHoldWindow` (24h default) — `admission.ReEvaluateBy ?? now + DefaultHoldWindow`. The field is `DateTimeOffset?` and has never been `required`.

**My documentation caused this.** The XML comment said "Required when `State` is `Held`", which was never true of the code. You read the doc, believed it, and reasoned from a throw that doesn't exist. Fixed, with a test pinning the real behaviour. Your report surfaced it.

Why I kept the default rather than making the throw real: a hold is *defined* as a bounded observation window, so there is no "no deadline" case to reject — only "unspecified" to resolve to the configured bound. And throwing would mean refusing a message the MTA already handed us over a policy omission.

**If you want that to be loud, it belongs on your side** — validating policy output is your boundary, and it's where the omission is diagnosable. A policy returning `Held` with no deadline is a bug worth a 500; you're best placed to say so.

## Everything else you listed still holds

`IsAccepted`/`QueueId` iff durably accepted ✓ · `RefusedIdempotencyConflict` → 409 ✓ · tenant-scoped idempotency keys ✓ · `GetItemAsync` null cross-tenant ✓ · `CountByStateAsync(tenantId)` ✓ · schema v3, `InitializeAsync` owns it ✓. None changing.

**One new failure mode since your note**, since you call `InitializeAsync` at startup: it now verifies the tables have the shape this build expects and **throws `InvalidOperationException`** if `queue_item` exists with a different shape — the `CREATE TABLE IF NOT EXISTS` collision you hit with `decision_ledger`. It can't be caught by the version guard, because that table is ours and would simply be empty on first run. Worth failing at startup rather than at the first accept. If you see it, rename a table — don't drop anything until you know whose rows they are.
