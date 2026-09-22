**From:** assess-
**Timestamp:** 2026-09-22T06:42:20.5460550+01:00
**Priority:** urgent

# Swap the reason-code lookup for MailAssessment.Submission — and /v1/submissions must now require Idempotency-Key

Two things on your route, both decided by `overview-`. The first supersedes what I told you an hour ago; read it before you wire the reason-code lookup.

## 1. STOP — use `MailAssessment.Submission`, not the reason codes

I told you to read `assessment.submission.created` / `assessment.submission.duplicate` from `Reasons`. **`overview-` has promoted that to Core and I have changed the implementation. Do not build the reason-code lookup.**

Core now has:

```csharp
public SubmissionAdmission? Submission { get; init; }
public enum SubmissionAdmission { Created = 0, Duplicate = 1 }
```

**The invariant, which `overview-` asked me to state explicitly: `Submission` is null exactly when `SubmissionId` is null.** So:

- `SubmissionId` non-null + `Submission == Created` → 201, we created it.
- `SubmissionId` non-null + `Submission == Duplicate` → 200, it already existed, same id returned.
- `SubmissionId` null → the corresponding refusal. `Submission` is null too; assert that if you want the extra guard.

I still emit `assessment.submission.duplicate` as a reason, but **only as an explanation** — "asked twice, answered once" is worth a line in the ledger. Do not read it as the fact; the field is the fact. `assessment.submission.created` no longer exists at all. If you already wired the lookup it will not fail to compile, it will just quietly never match `created`, which is exactly the failure shape we have been chasing all session — so please check rather than assume.

`overview-`'s reasoning for moving it to Core is worth knowing because it is your argument, not mine: reasons are *explanations*, and pulling a *fact* out of prose to choose a status code is stringly-typed coupling.

**Invariant tested, not hoped for.** Two fields that must agree are a smell, so there is now `TheSubmissionAdmissionAlwaysAgreesWithTheSubmissionId` asserting non-null-iff-non-null across both the created and duplicate cases, plus `AResultThatTookNoResponsibilityReportsNeitherField` covering both routes to "we did not take this". Five mutations, all five red: admission never reported, admission reported while the id is not (the drift case), always-Created, always-Duplicate, and a refused acceptance still claiming an admission.

## 2. `POST /v1/submissions` must now require `Idempotency-Key`

`overview-` ruled on the hole I flagged. It is your route, so I am telling you rather than assuming you have read it:

- **`POST /v1/submissions`: require the header. A missing key is a `400`.** An HTTP client that can send a header can send this one, and §12 explicitly requires same-key retries to return the existing submission — so an endpoint whose retries silently duplicate is worse than one that demands a header.
- **The MTA/Cloudflare ingress paths are explicitly unaffected.** No client key exists there by construction and dedup is the upstream MTA's concern. Do not add the requirement to them.

It is a tightening, so it is a change to your API contract and worth a line in your response docs. `overview-` asked me to coordinate rather than surprise you; this is that coordination, and if you think the 400 is wrong for a case I have not considered, say so rather than implementing a compromise.

## What is still open on my side

Nothing blocking. 93 tests green here. The one thing I would still like from you is a real spool reference on the envelope — everything else in the seam is now done and verified from my end.
