**From:** ingress-
**Timestamp:** 2026-09-22T07:35:02.4970780+01:00
**Severity:** medium
**Status:** open
**Source:** internal

# SmtpSubmissionListener.StopAsync followed by DisposeAsync throws ObjectDisposedException during host shutdown

Reported by `ingress-`; owned by `transport-` (src/StyloMail.Transport/Ingress/SmtpSubmissionListener.cs).

DEFECT
SmtpSubmissionListener.StopAsync sets `_listener = null` before awaiting the in-flight connection tasks (line 94, then line 124). A DisposeAsync arriving during that await sees `_listener is null`, returns immediately at line 88 without joining the drain, and calls `_connectionLimit.Dispose()` (line 141). The still-draining session then runs `finally { _connectionLimit.Release(); }` (line 196) against the disposed semaphore, and the ObjectDisposedException propagates out of StopAsync during host shutdown.

IMPACT
Any host that uses the documented IHostedService lifecycle — stop, then dispose — throws on shutdown whenever a client is attached. Surfaced in StyloMail.Host: the exception escaped WebApplicationFactory.DisposeAsync in the Host test suite. No mail loss and no lost acceptance; it is a shutdown-time fault, which is why it reads as flake rather than as a bug.

WHY IT WAS NOT CAUGHT
Every existing test uses `await using var listener = new SmtpSubmissionListener(...)` and never calls StopAsync explicitly, so DisposeAsync is always the single entry point and the second-caller path is never taken.

EVIDENCE
Host suite, with ingress- 's workaround reverted: 7 of 15 consecutive runs red. With the workaround: 15 of 15 green. The Host test added (SmtpIngressTests.Shutting_down_with_a_client_still_attached_does_not_fail) parks a client mid-transaction, making the in-flight session deterministic.

CURRENT MITIGATION
SmtpIngressHostedService stops the listener and deliberately does not dispose it. Nothing meaningful leaks — the socket, released by StopAsync, is the resource that matters. The reason is documented at the workaround so it is not removed as untidiness.

REQUESTED
Fix in SmtpSubmissionListener (make the second caller join the in-flight stop rather than return early), and add a test covering stop-then-dispose. Once fixed, `ingress-` can drop the workaround in the Host.
