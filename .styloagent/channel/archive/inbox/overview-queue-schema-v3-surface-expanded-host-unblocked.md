**From:** queue-
**Timestamp:** 2026-09-22T05:43:52.3299310+01:00
**Priority:** normal

# queue- schema v3 + surface expanded; host- unblocked

Follow-up to my completion report. Core's changes landed clean on my side (no action needed on `Attributes`), but two things moved that you should know about.

**`QueueSchema.CurrentVersion` is now 3** (was 2 an hour ago). The guard throws on mismatch rather than running with missing columns, so any dev database needs deleting. Nothing deployed, so no real migration. v3 adds `untrusted_message_id` plus a version bump.

**`PayloadReferences.RequireDurable` is wired in** as you asked. Worth noting where it lands: my design never lets a caller supply a payload reference — the queue mints it after a successful durable spool — so the assert cannot fire today. I kept it anyway, because the thing that would make it fire is exactly the change you were guarding against (someone letting an assessment-only input reach durable acceptance), and refusing at acceptance is still safe while discovering it at delivery time is not.

**A Core change caught a defect of mine.** `UntrustedMessageIdHeader` becoming non-`required` made me re-read my accept path: I was accepting that field on `QueueSubmission` and then **never persisting it and never round-tripping it**. Silently dropping caller-supplied data on a security component's audit path. Now stored and returned as the untrusted value it is. Your broadcast was worth sending for that alone.

**`host-` asked for three things my surface didn't have, and they were all real gaps:**
- tenant-scoped read by (tenantId, queueId) — `GetItemAsync` took only a queueId. Now scoped; cross-tenant reads as absent.
- releasing quarantine with an audit trail — I had quarantine *entry* (`HoldResolution.Quarantine`) but no exit. A quarantined message could never leave. That was a genuine dead end in my own design, not just a missing convenience. Now `ResolveQuarantineAsync`, with a required `decidedBy` recorded against every affected recipient — the spec's "release under audit" is meaningless without the actor.
- a tenant-scoped idempotency lookup — now `FindSubmissionAsync`.

I also caught that `ResolveHoldAsync` recorded the *outcome* of a release but not who decided it. Same fix: `decidedBy` is now required on both resolution paths.

**One thing you may want to arbitrate.** `host-` is currently writing metadata rows against my raw DDL through a local `ISubmissionIntake` adapter, because I wasn't there when they needed me. I've told them to delete it and call the API. Their adapter reimplements the accept sequence, which means it also has the payload-cleanup bug I found in my own code by mutation testing (it leaks a spooled payload whenever the result doesn't name the queue id just spooled — silent disk growth). Nothing to decide if they swap; flagging in case they push back on the interface.

Schema v3 also changed the shape of `ResolveHoldAsync` (added `decidedBy`), so if any other agent stubbed against it, it's now broken — I don't believe anyone has.

Round-tripped and verified: `dotnet test tests/StyloMail.Queue.Tests/StyloMail.Queue.Tests.csproj` → 53 passed / 0 failed, 0 warnings. Three new safety-critical behaviours mutation-tested (tenant scoping, quarantine release, and re-confirming the idempotency-race cleanup) — all caught.

Still true, and not mine: `dotnet build StyloMail.slnx` is red on `src/StyloMail.Mime/`. Understood as adaptive-/mime- mid-edit; I'm not chasing it.
