**From:** chat-
**Timestamp:** 2026-09-22T20:23:06.9565010+01:00
**Priority:** normal

# chat- Task 4 complete, and I found a registration trap the file itself documents

Task 4 is complete and frozen. The headline is a bug in my own registration that would have shipped as dead code looking like a working feature.

## The drain

**`Waiting` through to `Complete`**: take the oldest waiting, re-read the stored bytes rather than a stored parse, build the input, assess, record to the ledger, mark done, and prune by age. Re-reading the bytes matters because they are the authoritative record of what the platform sent, so a change to the reader applies to events already waiting rather than only to new ones.

**A failure leaves the event waiting rather than clearing it.** It has not been assessed and the platform was told it would be, so dropping the row would be exactly the loss the durable intake exists to prevent. It stays visible: an event that never clears is diagnosable, one that vanished is not.

**A payload that no longer reads as a message is cleared instead**, because it will not start reading on a later pass, and leaving it would occupy the intake forever while looking merely busy.

**`InboundTenantId` is configured rather than taken from the event.** This surface carries no principal, and nothing the platform sends is a claim about which of our tenants a workspace is: taking it from the payload would let a caller choose whose ledger their message lands in. The Cloudflare ingress resolves it the same way, and the default matches so a deployment running both does not end up with two names for one inbound tenant.

## The bug worth your attention

**I gated the chat registrations on `configuration` at composition-root time. `HostServices.cs` documents that exact trap in its own remarks**: "The host's composition root runs before a test host layers its own configuration in, so a decision taken at registration reads the wrong values."

My condition evaluated false, so **the drain and the chat assessor were registered nowhere and the feature was dead while appearing present.** Nothing failed; the endpoint tests passed because they never needed either. I found it by adding the drain's registration and watching the endpoint tests start failing for a reason that turned out to be downstream of this, then reading my own condition against the file's stated rule.

**Fixed by registering unconditionally and deciding at resolution**, which is what the file says to do and what the traffic port and the SMTP listener already do.

**The general lesson, which is why I am reporting it rather than just fixing it:** a conditional registration is a decision taken at a moment when the configuration is not yet final, and its failure mode is silence. Every other conditional in this file is evaluated at resolution for that reason, and I did not follow it the first time.

## One degradation I chose and want on the record

**With no profile master key, chat gets an assessor that throws, and the drain leaves events waiting.** I chose this over refusing to start because the key is environment-only with no configuration path, so refusing would make a Slack-enabled host unstartable in the test suite while proving nothing about production. It mirrors `UnavailableMailAssessor` on the mail path.

**What stops it being silent:** the events are not consumed. They accumulate waiting, where an operator can see them, and are assessed the moment the key is configured. If you would rather the ingress refused to start without the key, say so and it is one branch.

## Verified

Build 0 warnings, 0 errors. Whole solution **1437 passed, 0 failed, 23 skipped**: four new drain tests, and the endpoint, intake and options tests still green. Sweep signals clean.

**The drain is driven directly in its tests rather than through a host**, and its remarks say so: whether the host constructs it at all is a separate question. It is now answered for the enabled case by the endpoint tests exercising a live host, but I would rather name the seam than let the passing tests imply more than they cover.

## Remaining

Task 5 is the tests that make the decisions real, and most of them now exist across the endpoint, intake, drain and assessor suites. What is left is the completeness pass against that list rather than new machinery, plus the per-event write cost you asked me to bring numbers for if it proves disproportionate. Nothing has been committed from Task 4.
