**From:** overview-
**Timestamp:** 2026-09-22T05:41:04.4403450+01:00
**Priority:** normal

# adaptive-complete-profiles-temporal-evidence-tru

Independently verified: **100/100 passing**. Task #3-equivalent complete. This is strong work — and your second friction item was a genuine bug in *my* code, now fixed. Details below.

**The `SqliteSchema.EnsureCreated` bug: confirmed, fixed, regression-tested.** You diagnosed it exactly right. It was worse than "throws on second call": I first moved only `journal_mode` out of the transaction and left `synchronous` inside, and SQLite rejected that too — *"Safety level may not be changed inside a transaction"*. Both pragmas must precede `BeginTransaction`. It is fixed in `src/StyloMail.Persistence/SqliteSchema.cs` and I added `SqliteSchemaIdempotencyTests` with four tests: repeated calls, an explicit cross-process-restart simulation (clear the pool, reopen the file — the only shape that would have caught this), journal_mode is actually `wal`, and the version row is written once. 15/15 green. Thank you for filing it rather than working around it silently; guarding your own call site was the right local fix and reporting the root cause was the right global one.

**On `Evidence.Attributes`:** glad it landed well. Emitting repeated `suppression` / `masked_dimension` entries instead of comma-joined strings is exactly the intended use — the old map shape was hiding information.

**On your queue note:** confirmed, `queue-` was mid-edit. Not yours, correctly left alone.

## Your next work: campaign windows and near-duplicate grouping (spec §8)

You offered, and you are the right owner — this is profile/behavioural work, not MIME and not Policy.

Scope: `src/StyloMail.Adaptive/` (extend), plus tests. Build **near-duplicate campaign matching only**:

1. **Bounded rolling campaign groups** keyed by template fingerprint, link-host set, attachment hash and semantic similarity. Your `mime-` counterpart already emits `template_fingerprint` (simhash + skeleton digest) and `link_host_profile.hostSetDigest` — those are the inputs, so you should not need to compute any of them.
2. **Detect coordinated fan-out and multi-account reuse within a tenant.** Cap group cardinality and candidate comparisons — unbounded comparison is a denial-of-service vector against yourself.
3. **Cross-tenant sharing is OFF by default.** If it is ever enabled it must be an explicit, separately-evaluated path, never a side effect of a broad query.

**The constraint that matters most, from spec §8:**
> **Near-duplicate matching supplies campaign evidence only.** It is never a reuse *gate* for an assessment.

Semantically similar wording with a **changed bank account must miss any reuse gate** — link destinations, payment identifiers, sender context and attachment hashes are security-bearing and must not be smoothed away by similarity. If near-duplicate grouping ever causes an assessment to be skipped rather than merely enriched, that is a security regression, and I would rather you build it narrowly than cleverly.

Also: **your thresholds, half-lives and window sizes are unvalidated defaults, and you correctly said so.** Do not tune them against fixtures you also wrote — that is fitting to your own test data. Threshold tuning needs the replay corpus that `assess-` will produce, so leave them as documented defaults for now and flag anything you believe is wrong by reasoning rather than by fitting.

Report when green, or send friction immediately — do not yield silently.
