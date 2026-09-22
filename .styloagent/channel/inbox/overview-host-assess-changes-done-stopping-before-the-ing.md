**From:** host-
**Timestamp:** 2026-09-22T06:50:20.0500240+01:00
**Priority:** normal

# host-: assess- changes DONE; stopping before the ingress adapter, as you authorised

host- here. Taking your permission to stop rather than pushing through. **94/94 green.**

**Done and verified since my last report — both of `assess-`'s changes:**

1. **`MailAssessment.SubmissionAdmission` adopted.** I had *not* wired the reason-code lookup — I asked the question and got the field back, so there was nothing to undo. I now read `Submission` (the fact, not the prose) and map `Created` → 202, `Duplicate` → 200, consistent with what my client-key fast-path already returned. `assess-`'s warning was the right one to send: a reason-code lookup would not have failed to compile, it would just never have matched — but it never existed.
2. **`Idempotency-Key` is now required** on `POST /v1/submissions`; missing → 400 `idempotency_key_required`. I inverted the test that previously asserted the opposite, so the change is pinned rather than silently covered. I did **not** touch the MTA/Cloudflare paths, per the ruling.

I also fixed a fidelity gap I would have been embarrassed to leave: my fake wasn't setting `Submission`, so the new field was untested — the identical hole I'd argued against an hour earlier. It now models the admission the way the real pipeline does.

**Stopping before the ingress adapter.** You asked me to check my budget before starting and to stop and say so rather than push through. The assessor wiring consumed it. I have not started `ISmtpIngressSink` — no half-built sink, no partially constructed listeners.

**The handover is in my checkpoint, written for a cold start.** It names the deliverables, the files, the durability rule, and one specific trap: `transport-`'s original sketch says the sink should "run the pipeline, then `QueueStore.AcceptAsync`". That would reintroduce the two-accepts-two-keys duplicate we spent this session removing. I flagged it to both of them and it is recorded prominently, because it is the kind of instruction that reads as obviously correct and costs real mail.

**One thing worth saying about the method, since it is the third time today it has paid.** Almost every defect I found in my own lane came from *running* the thing rather than reading it back: the lazy-singleton fail-fast that booted healthy while half-configured, the 422-vs-500 probe that proved DI actually constructed the assessor, the mutation that showed my own replay-test "fix" was cosmetic. None of those would have surfaced in a green test run or a clean build. My checkpoint records them as gotchas so the next agent does not have to rediscover them.

Everything I was asked to build is built, green, and verified. Nothing is blocked; the remaining work is simply not started, which is a different thing and I would rather it be recorded as such.
