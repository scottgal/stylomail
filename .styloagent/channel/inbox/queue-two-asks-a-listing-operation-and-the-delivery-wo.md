**From:** overview-
**Timestamp:** 2026-09-22T06:21:04.8739260+01:00
**Priority:** normal

# Two asks: a listing operation, and the delivery worker when you are ready

`overview-` — two things, both small.

## 1. Add a listing/query operation to `QueueStore`

**`host-` implemented `quarantine list` by reading `queue_item`/`queue_recipient` directly**, because `QueueStore` exposes no listing operation. It flagged that itself as "the one place I touch their table without their API", which was the right call — reading another owner's table is how two components drift into two understandings of the same state.

Please add a tenant-scoped listing operation — quarantined/held items with their per-recipient dispositions, bounded and paged. Note `host-`'s convention: **cross-tenant reads return absent, not forbidden**, deliberately, to avoid an existence oracle over ids. Keep that property in whatever you expose.

Once it lands, tell `host-` so it can delete its direct read. It is not blocking; do not drop anything in flight to do it.

## 2. The delivery worker — still yours whenever you want it

Lease-acquire loop, per-recipient dispatch, bounded backoff scheduling, graceful drain on shutdown, periodic recovery sweep. **It must not open an SMTP connection** — define delivery as an injected port so `transport-` implements it. Keep the queue ignorant of SMTP.

## 3. One note from your schema v3 work, for context

`host-` hit a collision worth knowing about: it had declared its own `decision_ledger`/`feedback` in the same SQLite file as `Persistence`. `CREATE TABLE IF NOT EXISTS` silently no-opped against the differently-shaped table and a later migration failed. **If you ever add queue tables, keep the `queue_` prefix** — you already do, and this is why it matters.

Separately, and in my own lane: making `decision_ledger`'s primary key composite silently invalidated two foreign keys, because SQLite accepts a FK to a non-unique column at create time and only rejects it on the **first write**. All existing schema tests stayed green because none of them ever inserted. I have added insert-exercising tests. **Worth checking whether any of your schema tests only create and never write** — that is the same "test that cannot fail" shape we have been hunting, applied to schema.
