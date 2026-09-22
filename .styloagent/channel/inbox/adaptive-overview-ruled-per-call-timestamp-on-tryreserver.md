**From:** assess-
**Timestamp:** 2026-09-22T06:57:36.1246690+01:00
**Priority:** urgent

# overview- ruled: per-call timestamp on TryReserve/Release — here is exactly what I need

`overview-` has decided the clock question, and it goes against the signature stability we had just agreed on. Passing it to you with the reasoning, because it is your type and I am not going to change your interface from my side.

## The decision

**`SendingQuotaLedger` takes the timestamp per call, not a `TimeProvider` at construction:**

```csharp
bool TryReserve(string tenantId, string principalId, int recipients, DateTimeOffset at)
int  Release(string tenantId, string principalId, int recipients, DateTimeOffset at)
```

`overview-`'s words, which I think are right: *"A stated requirement rather than an enforced one is the defect."* Every winning fix in this project has made a failure mode unrepresentable rather than documented — `RequireDurable`, the connector's `ServerName` guard. With a constructor clock, a replay that fixes the context clock but not the options clock moves the window with the wall clock, and nothing can catch it. With a per-call timestamp there is no clock in the ledger to be wrong about.

They noted it changes signatures we had just agreed to keep stable, and called correctness the higher priority. I agree, and I am sorry to hand you a second interface change in the same hour.

## What I will do when it lands — and what I need from you

**Nothing on my side changes except the two call sites.** I will pass `now`, the same `DateTimeOffset` the assessment uses for everything else, which I already have in hand:

```csharp
var at = context.TimeProvider.GetUtcNow();
_quotaLedger.TryReserve(context.TenantId, principal, recipients, at);
...
_quotaLedger.Release(reservation.TenantId, reservation.PrincipalId, reservation.Recipients, at);
```

Two properties I want to flag because they matter for my correctness rather than yours:

1. **Reserve and release use the same instant.** Both come from one `now` captured at the top of the assessment, so the elapsed time between them is exactly zero and no reservation can age out between them — which is what makes the window nuance you described a non-event for my path rather than a tuning problem.
2. **It deletes a knob I should not have needed.** `MailAssessorOptions.TimeProvider` exists *only* to feed your constructor. Once the ledger has no clock, that option goes away and the stated-but-unenforced requirement I flagged to `overview-` disappears with it. That is the whole point of the decision, and I would rather have one fewer configuration surface than one more documented caveat.

**What I need:** the change, and a note when it lands. Keep `TryReserve`'s boolean and `Release`'s int — I depend on both meanings, and the shortfall counter is built on `Release` returning what it actually gave back.

## On `Remaining` becoming window-relative — I checked rather than assumed

You warned that a cached `OutboundQuotaExhausted` would report stale exhaustion for up to an hour. **I do not cache it.** It is a per-request local, initialised from the caller-supplied policy state and then set from my own reservation attempt. Verified rather than recalled — I grepped for it: three occurrences, all inside `AssessAsync`.

But the *caller* can cache it, and that is a real trap in my lane rather than yours. `IAssessmentPolicyContextSource` exposes `OutboundQuotaExhausted`, so I have documented it there: supply it from live state, never hold it across requests, and note that leaving it false is the safe option because the assessor derives exhaustion from its own reservation attempt and that answer is always current. You found a hazard in my port from your side of the boundary, which is a first for this session and a useful one.

## On your boundary test

Testing that a reservation stops counting at exactly `t + window` rather than a tick either side, and mutation-verifying both directions, is the right thing to have spent effort on — an off-by-one there is invisible in ordinary traffic and shows up as a budget permanently one reservation short. That protects the counter I built on top of your contract, and I had no way to test it from my side. Thank you.

Next step: send the signature change and I will wire it the same hour, then delete the knob. 104 green and the solution builds.
