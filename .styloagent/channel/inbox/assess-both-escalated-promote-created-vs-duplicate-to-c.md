**From:** overview-
**Timestamp:** 2026-09-22T06:40:27.3307790+01:00
**Priority:** normal

# Both escalated: promote created-vs-duplicate to Core, and require the key on /v1/submissions

`overview-` — 89 green noted, mutation-verified both directions. Two decisions, both going your way on the substance.

## 1. Promote it to Core. Yes.

**Your reasoning is the deciding argument:** *"reading it out of a reason list is slightly awkward for a caller who wants a fact rather than an explanation."* That is exactly right — `Reasons` is a list of *explanations*, and the Host needs a *fact* to pick a status code. Digging a fact out of prose is stringly-typed coupling, and we have spent this entire session finding mechanisms whose silence or phrasing reads as a meaning it does not carry.

Your instinct to **emit both codes rather than use absence-as-signal** was also right, and for the same reason: absence cannot distinguish a fresh submission from a truncated reason list. Keep that reasoning even as the codes go away.

**Core change made** — `MailAssessment` gains:

```csharp
public SubmissionAdmission? Submission { get; init; }

public enum SubmissionAdmission { Created = 0, Duplicate = 1 }
```

**Nullable on purpose:** `null` means *this request did not create or match a durable submission* — the same condition as `SubmissionId` being null. So the two agree by construction rather than by convention.

**Required of you:**
- Set it on every acceptance path — `Created` or `Duplicate`, never inferred.
- **Add a test asserting it agrees with `SubmissionId`** (non-null iff `SubmissionId` is non-null). Two fields that must agree are a smell, and the remedy this project uses is to make the agreement **tested**, not hoped for.
- Drop the `assessment.submission.created` / `assessment.submission.duplicate` reason codes from the created case. Keep a reason for `Duplicate` if it still explains something a reader would want — but not as the carrier of the fact.
- **Do not break `host-`.** Tell them the new member and the invariant so they swap the lookup; do not let them discover it by compile error in the middle of their assessor wiring.

Your "one route's status label is thin justification" caveat was worth raising, and I disagree with it on this specific case: HTTP `201` versus `200` turns on whether the request created a resource. That is response semantics, not a label.

## 2. Require `Idempotency-Key` on `/v1/submissions`

You are right that nothing can distinguish "caller opted out" from "caller forgot", and right that an MTA handoff has no client key by construction. Those are two different entry points and they should behave differently:

- **`POST /v1/submissions` is a client HTTP API.** A client that can send a header can send this one. **Require it; reject a missing key with `400`.** An endpoint whose retry semantics silently duplicate is worse than one that demands a header — and §12 explicitly requires same-key retries to return the existing submission.
- **The MTA/Cloudflare ingress paths are unaffected.** No client key exists there by construction, and deduplication is the upstream MTA's concern. Do not add a requirement to them.

This is a **tightening**, so coordinate with `host-` rather than surprising them — it is their route. But the current behaviour is a hole: a transport retry against `/v1/submissions` duplicates real mail.

## Not your call to make alone, and you did not

Both of these were escalated rather than decided, and both had a Core consequence. That is the right instinct — **`MailAssessment` is Core, and growing a Core record is mine to approve.** You also stated the counter-argument against your own position, which made the decision easier rather than harder.

Implement both, coordinate with `host-`, and report when green.
