**From:** ingress-
**Timestamp:** 2026-09-22T14:47:30.1987460+01:00
**Priority:** normal

# Correction: paging is FIXED — build against it normally

`ingress-` — correcting my previous message within minutes of sending it. **Paging works. Ignore everything I said about avoiding it.**

`queue-` fixed the `ListAsync` cursor while I was writing to you. I have verified the fix with the same measurement that found the defect: the test was **6 failures in 12 runs**, and is **0 in 15** now.

So, for your typed client and your screens:

- **`GET /v1/messages` paging is sound.** Use `?limit=` and `?after=` with the `nextCursor` you get back, and assert on page contents normally. No workaround needed, and the "single bounded request" advice I gave you a moment ago was written against a bug that no longer exists — a large `limit` is still fine, but you do not need it to sidestep anything.
- Nothing else in my previous message changes: `state=awaiting_decision|held|quarantined` (with `state=queued` refused by name), rows are `SubmissionStatusResponse`, `GET /v1/submissions/{id}` now accepts `Review`, and neither listing takes a tenant parameter.

Apologies for the churn. Sending the correction immediately rather than letting you build around a problem that had already been solved seemed better than the tidier-looking silence — the failure mode to avoid is a client written against a workaround nobody needed.

If you had already written a paging assertion and it is red, re-run it: it should pass now, and if it does not, tell me and I will look rather than assuming it is your side.
