**From:** chat-
**Timestamp:** 2026-09-23T00:47:16.4693560+01:00
**Priority:** normal

# chat- not stalled: the drain integration and separated write are in, with two findings and check 4 still to come

Not stalled and not blocked, and I was between turns rather than silent by intention. Carried on, and the drain integration is in.

## The separated write

**`ChatObservationRecorder`** now owns the profile keys and the observation, and `ChatAssessor` delegates to it, so **the read and the write cannot disagree about which profile a message belongs to**. That was the risk in separating them: a record written to one key and read from another is a behavioural history that silently never grows.

**The drain runs triage before the assessor**, inside the per-event guard so a failure leaves the event waiting rather than killing the loop. Three paths: dismissed on scope is **not recorded**, a dismissal past scope is **recorded then completed without assessment**, and an escalation is assessed, which records. **Nothing is counted twice**, because the drain writes only where the assessment is skipped.

## Two findings, one of which makes part of this unreachable

**The drain builds its triage context without a campaign, so check 2 can never settle in production.** The record-but-do-not-assess path I just built is therefore **unreachable until a campaign is wired into the drain**. The code is right and the wiring is missing, and I would rather name that than let a passing suite imply the path is exercised.

**And enabling the ingress with no watched channels configured dismisses everything on scope.** That is the "empty watches nothing" decision working as designed, and it is precisely the case the dismissal count exists for: a deployment that turns the ingress on without configuring what it watches looks like it is assessing nothing, and only the count distinguishes that from a drain that is idle.

**The drain tests found it, which is why they failed at first**: they configured no channels, so every message was dismissed and nothing was assessed. The fix was to have them say which channel they watch, and it is the same lesson as the call-site one, one layer up.

## Still outstanding

**Check 4 (behaviour)** is not done. It is the one whose safer error depends on whether anything acts, so its disposition is a function of what the deployment has enabled rather than a fixed answer, and I would rather write it with that stated than as a threshold I picked. **The campaign wiring above**, and **the counts on the operator surface**, which you said are part of the work rather than a follow-up.

## Verified

Build 0 warnings, 0 errors. All fourteen projects reported: **1476 passed, 0 failed**. Both new files and the modified ones are in the tree uncommitted, and my checkpoint is current with all of the above.
