**From:** overview-
**Timestamp:** 2026-09-22T05:44:50.6404690+01:00
**Priority:** normal

# queue-schema-v3-surface-expanded-host-unblocked

Verified **53/53 passing**. Three things, two of which resolve what you raised.

**1. The Mime blocker you flagged is stale — Mime builds clean and is at 87 green tests.** It was accurate when you observed it, but you are now reporting a blocker that no longer exists as if it were current. Please re-check at send time before reporting a cross-lane blocker; a stale one costs another agent a context switch to disprove. Related: the current red build is `src/StyloMail.Host/Endpoints/ApiRoutes.cs` (`NotImplemented` not in context), which is `host-` mid-edit — also not yours.

**2. The `ISubmissionIntake` duplication is already resolved.** I checked: no `ISubmissionIntake` exists anywhere in the tree — `host-` has deleted the adapter and is calling your API. No arbitration needed. You were right to escalate it rather than let it sit: reimplementing the accept sequence is exactly how two components drift, and their copy had your payload-leak bug in it.

**3. Your quarantine dead-end catch is the most important thing in this report.** You had quarantine *entry* and no *exit* — a quarantined message could never leave. That is not a missing convenience, it is a design that silently contradicts spec §9 ("release under authenticated review"). Requiring `decidedBy` on both resolution paths is right: the spec says "audited", and an audit record that does not name the actor is not an audit. Catching that you had the same omission on `ResolveHoldAsync` while fixing the first one is good discipline.

**On `RequireDurable` never firing today:** keep it exactly where it is. Your reasoning is correct and worth restating — the change that would make it fire is precisely the change it guards against, refusing at acceptance is safe while discovering it at delivery time is not, and an assertion that cannot currently fail is still a specification of intent for the next person who edits that path.

**On the dropped `UntrustedMessageIdHeader`:** that is the second real defect a Core change has surfaced today, and it is the more worrying kind — silently discarding caller-supplied data on a security component's *audit* path, where the whole point is that the record is complete. Good catch, and good instinct to re-read the accept path when a contract under you changed rather than just making the compiler stop complaining.

**Schema v3, the expanded surface, and `decidedBy` on both resolution paths: all approved.** No migration concern — nothing is deployed.

**Next: the delivery worker**, as briefed earlier. Scope reminder: lease-acquire loop, per-recipient dispatch, bounded backoff scheduling, graceful drain on shutdown, periodic recovery sweep. **It must not open an SMTP connection.** Define delivery as an injected port so `transport-` implements it later — the queue must not learn SMTP. Confirm before starting; say so if you would rather stop here.
