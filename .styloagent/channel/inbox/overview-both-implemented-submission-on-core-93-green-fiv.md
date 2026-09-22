**From:** assess-
**Timestamp:** 2026-09-22T06:42:26.2251230+01:00
**Priority:** normal

# Both implemented: Submission on Core, 93 green, five mutations red — and host- told before they hit it

Both decisions implemented. **93 tests green.** `host-` was told before touching anything, as you asked.

## 1. `SubmissionAdmission` wired

- `MailAssessment.Submission` set on every acceptance path, `Created` or `Duplicate`, never inferred.
- Null on assessment-only, `Defer`, `Reject`, and a refused acceptance — so it is null exactly when `SubmissionId` is null.
- **Agreement is structural where it can be and tested where it cannot.** `AcceptanceOutcome.Accepted` takes the id and the admission from one place, so they cannot be set independently; and the invariant you asked for is asserted from outside by `TheSubmissionAdmissionAlwaysAgreesWithTheSubmissionId`, across both the created and duplicate cases.
- Also added `AResultThatTookNoResponsibilityReportsNeitherField`, covering **both** routes to "we did not take this" — the assessment path and a policy decline on the submission path. Your instruction was to test the agreement; testing it only on the accepting paths would have left the null case as an untested claim about two fields that could drift apart there instead.
- `assessment.submission.created` is gone. `assessment.submission.duplicate` stays as an explanation only — a replay is worth a line in the ledger — and no longer carries the fact.

**Five mutations, all five red:** admission never reported; admission reported while the id is not (the drift case); always-`Created`; always-`Duplicate`; and a refused acceptance still claiming an admission. That last one is the one I would have missed without writing it — `Refused` builds from a different factory, so the agreement held by accident of construction until I mutated it to check.

## 2. The key requirement is with `host-`

Sent as coordination rather than instruction, and I told them the MTA/Cloudflare paths are explicitly unaffected so they do not over-apply it. I also flagged that the status-code change is theirs to document. If they push back on the `400` with a case I have not considered, I will bring it to you rather than compromise it myself.

## One thing worth recording from this exchange

You disagreed with my "one route's status label is thin justification" caveat, and you were right — `201` versus `200` turns on whether the request created a resource, which is response semantics rather than a label. I mention it because I stated the counter-argument against my own position and you overruled it on the substance, which is the outcome that caveat was for. If I had not raised it I would have shipped the reason-code version and it would have been worse.

**One correction I sent `host-` that matters for your picture of the fleet:** they report 85/85 green with a real spool reference, but their host tests use a fake assessor that does not accept — the same hole they volunteered about their replay fast-path. So the submission route's 85/85 may not exercise the seam end to end. I pointed them at the shape of my integration test rather than asserting their suite is wrong, since only they can tell me whether it covers it.

Next step: idle and available.
