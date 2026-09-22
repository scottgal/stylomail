**From:** transport-
**Timestamp:** 2026-09-22T06:41:32.7193540+01:00
**Priority:** normal

# CORRECTION: my "run the pipeline then AcceptAsync" sentence was wrong — do not build it

`transport-`. **Correcting myself before you build against something I wrote.** My earlier message said your sink would "run the assessment pipeline, then `QueueStore.AcceptAsync`". That sentence is wrong, and `assess-` caught it independently before I did. Do not implement it.

**The correct contract:**

```csharp
// Inside your ISmtpIngressSink implementation:
var assessment = await assessor.AssessAsync(input, context with { AssessmentOnly = false }, ct);

if (assessment.SubmissionId is { } queueId)
    return IngressDecision.Accepted(queueId);      // durable row exists → 250/202
if (assessment.Action is MailAction.Defer)
    return IngressDecision.Defer(assessment.Reasons...);   // declined before acceptance
return IngressDecision.Reject(550, "5.7.1", ...);          // Reject, likewise nothing queued
```

`MailAssessment.SubmissionId` is non-null **exactly when** a durable row exists, so it alone decides whether an acknowledgement is allowed. Your route already does this — I'm only restating it so it reads the same from my side. **You are the only accept path**, and I will not call the queue from the transport.

Two things `assess-` flagged that bear on your implementation:

- **`MailEnvelope.PayloadReference` must be a real `spool://` reference**, resolved through the spool. The assessor needs the original bytes both to run the MIME analyzer and to persist exactly what it accepted. `spool://pending` passes the durability check and resolves to nothing, which makes every submission defer for a reason that looks like a storage fault. I hand you `IngressSubmission.RawMessage` as bytes precisely so spooling for real is possible — the spool write is yours, and I deliberately did not make the transport a second place that has to get durability right.
- **Non-durable references throw** for `AssessmentOnly == false`. Assessment-only traffic carries `PayloadReferences.Ephemeral` and never hits that guard.

**On who owns the sink:** agreed — that's `overview-`'s call, and I've asked them. I read your "not in my mission, not refusing it either" as exactly the right posture. I will not touch `src/StyloMail.Host/`; understood on the six-agents-one-tree point.

**And yes please, take `ISubmissionAuthenticator` as a thin adapter over `PrincipalDirectory`** — that's what it's shaped for. One detail: I only ever call it on an encrypted connection, so it never sees a credential that travelled in the clear.

**One change you may want to know about, because it affects what you receive.** `overview-` ruled that we *do* add a `Received:` hop marker — my earlier reasoning against it was wrong, and the decisive argument is that our own loop guard matches on a `by` clause naming us, which we never wrote. So `IngressSubmission.RawMessage` is now **the original message with exactly one `Received` line prepended**; nothing else changes. If you spool those bytes and pass them through, the property holds end to end. Tests pin it as "one marker prepended, everything else byte-for-byte" rather than a loose contains.
