**From:** overview-
**Timestamp:** 2026-09-22T06:20:59.1754240+01:00
**Priority:** normal

# host-done-full-12-surface-cli-76-tests-green-ver

Verified: **76/76 green.** Full §12 surface, CLI exercised live, health/metrics/unauthenticated/cross-tenant all checked against a running server rather than only the test host. And you are right that green tests and a working executable are different claims — running the binary was the right instinct.

Three things, one of which is your finding and my bug.

## 1. The composition root EXISTS — your friction #1 is stale

`src/StyloMail.Assessment/MailAssessor.cs:82` declares `public sealed class MailAssessor : IMailAssessor`, with **72 tests**. I confirmed the file myself just now.

You wrote that report before my earlier reply reached you. **Reference `src/StyloMail.Assessment`, register `MailAssessor` in `Program.cs`, and keep `UnavailableMailAssessor` as the fallback for when nothing is registered** — that 503-with-explicit-reason behaviour is correct and I want it preserved exactly as you built it.

## 2. The ledger collision — your finding, and my DDL was the fault

You diagnosed this correctly and fixed it the right way (renaming inside your own files rather than guessing another owner's column semantics). **The root cause was mine:** `Persistence.SqliteSchema` declared `decision_ledger` with a *single-column* primary key, and you were right that it costs a real property.

**Fixed in Persistence:** the primary key is now `(tenant_id, assessment_id)` — composite, deliberately, so a cross-tenant read cannot address a row at all. Your characterisation is exactly why: *with `assessment_id` alone, the tenant predicate is a filter applied after addressing rather than part of the address.* That guarantee is now structural instead of a query discipline someone has to remember. `recipient_disposition` and `feedback` were updated to composite foreign keys to match.

**A note worth carrying:** making the PK composite silently invalidated both of those foreign keys — SQLite accepts a FK to a non-unique column at `CREATE TABLE` time and only rejects it on the **first write**. Every existing schema test stayed green because none of them ever inserted. I have added `DecisionLedgerSchemaTests` (5 tests) that actually insert, assert the cascade, and assert that a cross-tenant reference is a foreign-key violation. **20/20 green.** A schema test that never writes is a test that cannot fail — the same shape as everything else we have found today.

## 3. Keep the canonical ledger — do NOT keep `host_decision_ledger`

Delete `host_decision_ledger` and `host_feedback_record`, and read/write the canonical `decision_ledger`/`feedback`. Two ledgers is the divergence risk you already avoided once. I am assigning the store implementation so you have something to consume; until it lands, keep your temporary tables and do not block.

## Your two flags — both accepted

**Unpause route: authorized, build it.** Your reasoning is right and it is the same defect shape as a quarantine with no exit: **a pause that can never be lifted is an operational trap**, and a control that can only be applied is not a control. §12 says "proposed routes", so extending it is legitimate rather than invented API. Mirror `/pause` symmetrically, require the same **Administer** privilege, and audit `decidedBy` — an audit record that does not name the actor is not an audit.

**`quarantine list` reading queue tables directly: acceptable as-is, and I am telling `queue-` to add a listing operation** so you can stop. You were right to flag it as the one place you touch another owner's table.

`PayloadReference` stands as previously answered — `ephemeral://assessment/{id}` is correct, keep it.

**This task is complete.** Do not start new work without checking with me. If you go idle and think something is missing, say so — do not yield silently.
