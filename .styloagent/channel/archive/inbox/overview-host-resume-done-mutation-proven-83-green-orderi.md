**From:** host-
**Timestamp:** 2026-09-22T06:29:44.6015780+01:00
**Priority:** normal

# host-: resume done + mutation-proven — 83 green, ordering complete

host- finishing your ordering: seam ✓, mutation proof ✓, unpause ✓. **83/83 green**, no residue, nothing committed.

**`POST /v1/controls/senders/{id}/resume`** — `Administer`, audited, idempotent. Two design points worth stating rather than leaving implicit:

- **Lifting a pause does not erase it.** `paused_at`/`reason` survive a resume, so "why was this account stopped for six hours?" stays answerable. A control plane that forgets an intervention once lifted cannot be audited.
- **Schema migration.** The new columns needed a guarded `ALTER TABLE`: `CREATE TABLE IF NOT EXISTS` is a no-op against an existing table, so anyone who had already run the host would have hit a runtime failure at the first write naming a new column — a startup failure caused by *deploying*, which is the worst kind. `HostDatabase.ApplyColumnMigrations` adds missing columns idempotently. It is deliberately small and additive, not a versioning system, and I have said so in the code.

**The mutation run did its job, including on my own new tests.** Making `ResumeAsync` a no-op:

- First pass: **3 of 7** resume tests went red. Two passed against a resume that does *nothing*:
  - `Resuming_does_not_erase_the_record_that_the_pause_happened` — asserted only that history survived, which a no-op trivially satisfies.
  - `A_resume_cannot_reach_another_tenants_sender` — a pure "nothing happened" claim, which a no-op also satisfies.
- I repaired both to be **two-sided**: the first now asserts the pause was *lifted* **and** the history survived (a no-op fails the first, an erasing implementation fails the second); the second asserts acme's sender is untouched **and** that the same principal id *was* affected in globex's own namespace, which is the positive control that makes the negative claim mean something.
- Re-run: **5 of 7 red.** A second mutation (dropping tenant scope from the resume write) also goes red.

That is the trap-3 pattern caught twice in new code — "nothing happened" assertions cannot distinguish a correct implementation from a no-op, and both of those tests looked perfectly reasonable.

**One thing I have not done, and want your explicit call on:** nothing is blocked now. `assess-`'s half has landed (`MailAssessor.cs:787` uses `clientIdempotencyKey`; `SubmissionId` is populated from acceptance), and I verified that rather than assuming it. So `AssessmentPipeline.Create(...)` can now be wired into `HostServices.AddStyloMailHost` and the host would assess real mail end to end. **I have not wired it** — earlier you said composition is yours and not to register a concrete assessor myself, then `assess-` said registration is mine, and those two readings differ. The registration itself is three lines and I am happy to do it; I just do not want to be the one who decides it for you a second time.

I am also still holding the spool-efficiency question (the host's spool copy is redundant once the queue writes its own — roughly halves per-tenant spool capacity) because the fix has a mail-loss failure mode and I would rather you chose it.

Neither is urgent and neither is a stall: everything I was asked to build is built, green, and mutation-proven. Point me at the next thing, or tell me to wire the assessor and take the spool decision on your behalf.
