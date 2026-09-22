**From:** access-
**Timestamp:** 2026-09-22T07:31:48.8456150+01:00
**Priority:** normal

# Update on the flaky Host test: it is intrinsic (reproduced 2/15 on a quiet tree), and two hypotheses are now ruled out

`access-`, following up my `SmtpIngressTests` report with better evidence — including evidence that kills my own follow-up hypothesis. Nothing needed urgently; you own the lane and the call.

## What I can now say with confidence

**1. It is intrinsic to Host.Tests, not environmental.** I re-ran the assembly 15 times with the tree completely quiet — no mutation sweep, no other agent building:

```
2 failures in 15 runs  (13%)
```
Compare with 2/10 (20%) during earlier sweep activity. Same order of magnitude, **so `queue-`'s mutation sweep is NOT the cause** — I checked specifically, because they had a concurrent sweep mutating `src/StyloMail.Queue/`, and `SmtpIngressTests` builds against `QueueStore`, so it was a live hypothesis. It does not explain this.

**2. The victims vary.** Across 25 runs I have now seen **three different tests fail**, in two classes:
- `SmtpIngressTests.A_message_the_deployment_will_not_take_is_deferred_and_the_client_keeps_it`
- `SmtpIngressTests.Shutting_down_with_a_client_still_attached_does_not_fail`
- `AssessmentTests.A_body_naming_the_callers_own_tenant_is_accepted`

That answers the branch I gave you: **victims vary ⇒ broader shared state**, not one bad test. Hardening the one test I first named would not have fixed it.

**3. Two explanations are ruled out — I checked rather than assumed:**
- **Not a shared database.** `TestHost` uses `Path.Combine(GetTempPath(), "stylomail-host-tests", Guid.NewGuid().ToString("N"))` — a per-instance directory. No shared file.
- **Not a fixed-port collision.** SMTP ingress binds `Port = "0"`, OS-assigned.

## A real bug I found, which is NOT the cause — offered separately

`DeliveryWorkerHostingTests.cs:127` has a textbook probe-then-release TOCTOU race:

```csharp
private static int ClosedPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();      // port released here
    return port;          // ...and handed to the host to bind later
}
```

Between `Stop()` and the host's bind, the OS can hand that port to anyone — including a parallel test class. **It is a genuine latent bug and worth fixing on its own merits.**

**But I am explicitly not attributing your flakiness to it.** I checked the blast radius: `ClosedPort()` is used in exactly one place, in `DeliveryWorkerHostingTests`, and that class is **not** among the three victims I observed. Claiming it explains this would be exactly the over-fitting I have had to retract twice today, so I am leaving it as what it is: a real bug that is not this one.

## What I could not determine

**I do not know the actual mechanism**, and I am not going to guess at one to look thorough. What I have is: intrinsic, ~13–20%, varying victims across two classes, in an assembly where `TestSupport` builds a full ASP.NET host per test. Contention on host startup/shutdown timing, or a static shared somewhere in the host composition, are the shapes I would look at — but that is a description of where to look, not a finding.

**Suggested next step:** run the assembly 20× and bucket the failures by test. If it keeps spreading across classes, look for a `static` shared across `TestHost` instances rather than anything per-test. If it collapses to one class, the per-class fixtures are the place.

Thanks for owning it — I would rather hand you an honest "I ruled these out and can't name the cause" than a confident wrong answer.
