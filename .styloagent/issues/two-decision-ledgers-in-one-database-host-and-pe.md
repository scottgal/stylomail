**From:** host-
**Timestamp:** 2026-09-22T06:19:29.3267080+01:00
**Severity:** medium
**Status:** open
**Source:** internal

# Two decision ledgers in one database — Host and Persistence both declare one

Discovered while wiring the Host's storage.

`src/StyloMail.Persistence/SqliteSchema.cs` declares `decision_ledger`, `feedback` and `recipient_disposition`.
`src/StyloMail.Host/Storage/HostDatabase.cs` had declared `decision_ledger` and `feedback_record`.

Both end up in the SAME SQLite file, because Program.cs points `SqliteConnectionFactory` (Persistence) and
`HostDatabase` at `StyloMail:Storage:DatabasePath`. Result: the Host's `CREATE TABLE IF NOT EXISTS decision_ledger`
silently no-opped against Persistence's differently-shaped table, and then `SqliteSchema.EnsureCreated`'s
migration tried to `ALTER TABLE decision_ledger ADD COLUMN assessed_at` on a table whose columns are different —
failing at startup with `SQLite Error 1: 'no such column: assessed_at'`. Every test that booted the host broke.

Immediate fix applied (in my own files only): renamed the Host's tables to `host_decision_ledger` and
`host_feedback_record`, so the two definitions no longer collide and both are addressable. 76 tests green.

UNRESOLVED — needs an owner's decision, which is why this is filed rather than fixed:

- Spec §5 assigns "SQLite profiles, queue metadata, feedback and decision ledger" to Persistence. On that reading
  Persistence's tables are canonical and the Host's are redundant.
- I did NOT switch the Host to read/write Persistence's `decision_ledger`. Its column semantics
  (`reasons_json`, `coverage_json`, `recipient_disposition`, …) are its owner's intent and guessing the mapping
  would mean writing into another agent's subsystem.

Decision needed: either (a) the Host adopts Persistence's schema and deletes its own tables, or (b) Persistence's
unused ledger/feedback tables are removed and the Host's document-shaped ledger is canonical, or (c) the split is
deliberate and the `host_` prefix becomes the convention.

One concrete difference worth noting for the decision: Persistence's `decision_ledger` has
`assessment_id` as its sole PRIMARY KEY, whereas the Host's puts `(tenant_id, assessment_id)` in the PK so a
cross-tenant read cannot address a row at all. If (a) is chosen, the tenancy scoping has to be preserved in the
query layer or that structural guarantee is lost.
