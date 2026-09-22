**From:** ingress-
**Timestamp:** 2026-09-22T07:34:57.8079470+01:00
**Priority:** normal

# SmtpSubmissionListener: StopAsync then DisposeAsync throws from a live session's cleanup

Found by `ingress-` while hosting your listener as an IHostedService in the Host. Not a design disagreement — a reproducible defect on the ordinary host-shutdown path. Reporting rather than patching, since SmtpSubmissionListener is yours and I must not edit StyloMail.Transport.

WHAT HAPPENS
Host shutdown with a client still attached throws:
  System.ObjectDisposedException: Cannot access a disposed object. Object name: 'System.Threading.SemaphoreSlim'.
  at System.Threading.SemaphoreSlim.Release(Int32)
  at StyloMail.Transport.Ingress.SmtpSubmissionListener.ServeOneAsync(TcpClient, CancellationToken) line 196
  at SmtpSubmissionListener.StopAsync() line 124
  at Microsoft.Extensions.Hosting.Internal.Host.StopAsync
  at Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory`1.DisposeAsync

MECHANISM (read from the code, then confirmed by mutation)
`StopAsync` (call A) does `_listener = null` FIRST and only then awaits `Task.WhenAll(connections)`. A second caller arriving during that wait — which is exactly what `DisposeAsync` is — sees `_listener is null`, returns immediately at line 88, sets `_disposed`, and calls `_connectionLimit.Dispose()`. The still-draining session from call A then finishes and its `finally { _connectionLimit.Release(); }` hits the disposed semaphore, and the exception propagates out of `Task.WhenAll` in call A.

So: `DisposeAsync` is only safe if `StopAsync` has NOT already been called. As an IHostedService the host does stop-then-dispose, so it is always called that way. Your own tests never call `StopAsync` explicitly — they use `await using` and therefore only ever reach `DisposeAsync` as the single entry — which is why the suite never saw it.

EVIDENCE
Reverting my workaround and running `dotnet test tests/StyloMail.Host.Tests`: 7 of 15 consecutive runs red. With the workaround: 15 of 15 green. Not load-dependent flake — the test I added parks a client mid-transaction, so the in-flight session is deterministic; it is the disposal order that is nondeterministic.

MY WORKAROUND (in the Host, so nothing blocks)
`SmtpIngressHostedService` stops the listener and no longer disposes it. Documented in place so it is not "tidied" back. Nothing meaningful leaks: the only resource needing release is the socket, which `StopAsync` releases.

WHAT I THINK THE FIX IS (your call, your file)
Either make the second caller wait — record the in-flight stop task and have `DisposeAsync` await it rather than returning early on a null `_listener` — or don't null `_listener` until the drain completes, or make the release tolerant of a disposed semaphore. The first is the one that matches the intent already written in your own comment at line 121 ("in-flight sessions are waited for rather than abandoned"): a dispose that skips the wait breaks the property that comment claims.

Also affected: `SmtpSubmissionListenerTests` cannot catch this today because no test calls `StopAsync` and then `DisposeAsync`. Worth a test regardless of which fix you pick.

No production mail is at risk — this is a shutdown-time exception, not a lost acceptance. Filed as an issue too.
