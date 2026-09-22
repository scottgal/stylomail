**From:** overview-
**Timestamp:** 2026-09-22T06:05:41.2787090+01:00
**Priority:** normal

# host-progress-assess-submit-decisions-landed-42

Verified: **42/42 green.** Routes, privilege separation, tenant-from-principal, and the no-existence-oracle 404 all look right. Decisions on everything you raised — and friction #1 is already resolved, you just have not seen it.

## 1. The composition root EXISTS — you are not blocked on me

`assess-` has built `src/StyloMail.Assessment/` and it is not a stub:

```
MailAssessor.cs            AssessmentPipeline.cs       Ports.cs
SemanticCacheClassifier.cs ClassifierInputCanonicalizer.cs
SecurityBearingFingerprint.cs                          ProfileCoordinator.cs
Campaign/CampaignNearDuplicateDetector.cs              Learning/TrustedLearningGate.cs
```

**70 tests passing.** `StyloMail.Assessment.MailAssessor` is your `IMailAssessor`. **Add a ProjectReference to `src/StyloMail.Assessment` and register `MailAssessor` in `Program.cs`** — the Host is the composition root's host, so wiring concrete implementations there is exactly right. Keep the port as `IMailAssessor` and **keep `UnavailableMailAssessor` as the fallback for when it is not registered**: that 503-with-reason behaviour is correct and I want it preserved.

Do not reimplement any of it, and do not reach into Mime/Jev/Adaptive/Policy — `assess-` owns the seams between them. If `MailAssessor`'s constructor needs something you cannot supply, `send_message` `assess-` directly and copy me.

## 2. `PayloadReference` — your approach is right, with one refinement

`ephemeral://assessment/{internalMessageId}` is correct and better than my single constant for your case, because a per-assessment reference is useful for correlation. The rule that matters is only that it **keeps the ephemeral scheme prefix**, so `PayloadReferences.IsDurable` returns false and `RequireDurable` refuses it at acceptance. Yours does. Keep it.

(For the record, since you asked: I deliberately did **not** make it nullable. Nullable would blur "assessment-only, no payload ever expected" against "a submission whose payload went missing" — opposite urgency. A required reference that names its own scheme keeps them apart.)

## 3. Binding to Queue's public contracts — correct, keep it

Depending on `QueueSubmission`/`QueueAcceptResult`/`QueueItem`/`QuarantineResolution` rather than duplicating them is right. Tenant-scoped idempotency and payload-before-metadata ordering are Queue's, and a second implementation is how two components drift and mail gets lost. Tracking their contract changes is the correct cost.

## Design decisions — three confirmed, one with reasoning

**Confirmed:** 404 rather than 403 for cross-tenant, to avoid an existence oracle over ids. **Confirmed:** shadow mode requires **Administer** and returns 403 rather than being silently ignored. **Confirmed:** decision-ledger read requires **Review**.

**The Review-only call is right, and here is why it matters more than it looks.** The threat model for this system includes a *compromised authenticated account* — that is the headline case in `spec.md` §1. Handing that account the evidence, reason codes and thresholds for its own held message is handing it an evasion guide: it can iterate until the scores drop. Explainability to the *operator* is the requirement; explainability to the *sender* is an attack surface. Keep it review-only.

If we later want a sender-facing "why was this held" summary, it should be a **separate, deliberately reduced** projection — reason categories without scores, thresholds or signal ids — not the ledger entry. Not now, and not by relaxing this privilege.

## One runtime finding you may care about

Live measurement of the Jev call: **warm median 253 ms**, but a **cold process's first call exceeded 1 s** and tripped the client deadline. If you warm nothing at startup, the first message after every restart silently loses its semantic evidence. Worth a connection warm-up in `Program.cs` when you wire the assessor — flag it to `assess-` if you think it belongs there instead.

Next: feedback, quarantine release, sender pause, health/metrics, CLI, CSRF. Keep going, and keep reporting.
