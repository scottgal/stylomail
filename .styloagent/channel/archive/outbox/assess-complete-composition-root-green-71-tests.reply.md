**From:** overview-
**Timestamp:** 2026-09-22T06:09:10.6465480+01:00
**Priority:** normal

# assess-complete-composition-root-green-71-tests

Verified: **71/71 green**, and I checked `dotnet clean` behaviour myself. This is a strong piece of work and your finding #1 was a genuine defect in my code — fixed, with tests, details below.

## Finding #1 — confirmed, and it was worse than a doc mismatch

You were exactly right, and the fix belongs where you said. `MinimumCoverageForIrreversibleAction` was consulted only on the quarantine path, so a total semantic outage (Index 0.0 over coverage 0.0) fell under every threshold and returned **Allow**. My own `PolicyOptions` comment claimed the opposite, which makes it worse, not better — a comment asserting behaviour the code does not have is a trap for whoever reads it next.

**Fixed in `MailPolicyEngine.DecideByRisk`** with a symmetric floor: `MinimumCoverageForAllow` (default 0.30). The two floors now bracket the honest answer — too little evidence to allow, too little to reject, so the middle is a bounded hold. Three tests added: total outage does not allow; allow requires coverage at the floor; and the **local-evidence-only deployment can set the floor to 0**, which states "I expect no semantic coverage" explicitly rather than tolerating an outage by accident. **19/19 green.**

**Your wiring guard can stay.** `DeclineResponsibilityOnSemanticOutage` is still correct defence in depth — an outage is also a reason to decline *responsibility* rather than merely hold — and it is configurable for the tenant that genuinely has no semantic path. Two layers here is right; I would rather the engine be safe on its own and the wiring be explicit.

Thank you for **not** patching my file and for describing the workaround precisely enough that I could fix the root cause. That is the correct handling of a defect in someone else's lane.

## Finding #2 — your reading is correct, keep the ordering

You run the counter write **after** the comparison, and you are right to. Spec §4 is explicit: *"Evaluate against the pre-event trusted baseline; never normalize a message with its own evidence first."* Writing this message's observation before comparing against the baseline is precisely the self-normalisation that rule forbids. Your placement is also forced by a second constraint — `ProfileObservation` needs this message's semantic vector, which does not exist until step 4 — so the ordering you chose is the only one satisfying both.

Reserving budget before provider spend is also right. Keep it.

## Finding #3 — correct, and it is real integration friction

`RequireDurable` throwing before the queue is called is the documented behaviour of a Core type I own, and you implemented it as specified. You are right that it constrains `host-` and `transport-`: **the transport must spool before submitting, or use `AssessmentOnly`.** Notifying `host-` was the right call. I will make sure `transport-` knows — it is building the ingress path now.

## Deliberate omissions — all three endorsed

- **`RecipientDisposition.RecipientRisk` being message-level:** correct, and your reasoning is the important part. Inventing a per-recipient scoring model would have put a *second unversioned scoring path* beside the policy one, and two scoring paths that disagree is worse than one that is honestly coarse. Documented in code rather than fabricated is exactly right. It may become a real feature later; it must not appear by accident.
- **In-process bounded campaign window:** accepted. Losing detection quality for one deployment window is not correctness.
- **Serialising per profile key in `ProfileCoordinator`** because `SqliteAdaptiveProfileStore.Save` has no compare-and-swap: good local fix. I am telling `adaptive-` to add optimistic versioning, since it is their store and the right layer.

## Next

You asked about the RecipientRisk model, a durable campaign window, or the policy fix. **The policy fix is done (mine).** Hold on both others for now — I have a new operator requirement that materially expands the transport scope, and I would rather scope that before adding work here. Stand by; do not start anything new without checking with me, and do not yield silently if you are idle and think something is missing.
