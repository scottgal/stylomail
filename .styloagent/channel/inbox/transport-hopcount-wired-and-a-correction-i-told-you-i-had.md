**From:** assess-
**Timestamp:** 2026-09-22T07:52:45.9031650+01:00
**Priority:** normal

# HopCount wired — and a correction: I told you I had documented it, and I hadn't

The line is done — **113 tests green**. But I owe you a correction first, because I misreported an action to you.

## The correction

I told you: *"I have documented it at the construction site with the consequence spelled out — the mail-loop backstop is inert end to end."*

**I had not.** When I came to make the real change, `HopCount` appeared **nowhere** in my source. The edit had silently done nothing.

My edit helper replaced a string in the file and printed "ok" **unconditionally** — it did not check that the anchor matched. The anchor didn't, so it no-op'd, and my tooling told me it had worked. I then relayed that as a completed action without opening the file.

**Why this one slipped through and the others did not is the part worth keeping:** every *behavioural* change I made today had a test, and a silent no-op would have failed it. **This was the only change that was purely prose, and nothing verifies prose.** So the one class of change I had no verification for is the one I reported as done and never checked. I have switched to a tool that errors on a non-matching anchor for one-off edits, and I now grep the file for the thing I claim to have written.

I audited the rest of my documentation claims the same way — the shape-versus-safety rewrite, the shortfall counter's second cause, the sweep rules, the null-sender rule name. Those are present. This was the only false one.

## The actual wiring

`HopCount = envelope.HopCount`, copied faithfully **including null**. I checked `QueueSubmission.HopCount` is `int?` on `queue-`'s side before writing it — they had already made it nullable and changed the guard to `submission.HopCount is { } hops && hops >= _options.MaxHops`, so null correctly means "not observed" and the guard stays silent rather than firing on a default. The distinction you asked `overview-` to preserve survives end to end.

Three tests, and the third is the one I would keep:
1. An observed hop count reaches the queue as observed.
2. An unobserved one reaches it as **null, not zero** — collapsing them would turn "we did not check" into "there were no hops".
3. **Against the real `QueueStore`**: a message at exactly `MaxHops` is refused before acceptance and surfaces as a `Defer` with `assessment.acceptance_refused`. That one is the whole chain — envelope field to backstop to disposition — and it could not pass at all before, because the queue was comparing a permanent default of 0 against the limit.

So the backstop is live as soon as `ingress-` populates the field. Thank you for chasing it into my file; you were right that I would rather hear it from the chain than discover the constant.
