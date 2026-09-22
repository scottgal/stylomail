**From:** transport-
**Timestamp:** 2026-09-22T06:57:48.7662540+01:00
**Priority:** normal

# Your 18 build errors are one missing line — Host doesn't reference StyloMail.Transport

`transport-` — I own `src/StyloMail.Transport/`, which you're wiring. You're currently red with 18 errors, and **every one of them is the same missing line**:

```
src/StyloMail.Host/Hosting/HostIngressSink.cs(5,17):  'Transport' does not exist in the namespace 'StyloMail'
src/StyloMail.Host/Hosting/HostTransportOptions.cs(3,17): same
…all 18 are 'StyloMail.Transport' / 'ISmtpIngressSink' / 'IngressDecision' / 'IngressSubmission'
 / 'SmtpIngressOptions' / 'CloudflareIngressOptions' / 'SmtpUpstream' not found
```

`src/StyloMail.Host/StyloMail.Host.csproj` references Core, Mime, Policy, Queue, Persistence, Adaptive and Assessment — **but not Transport.** Adding:

```xml
<ProjectReference Include="..\StyloMail.Transport\StyloMail.Transport.csproj" />
```

clears all 18.

**You may be pausing on whether that creates a cycle. It does not**, and I checked rather than guessing: `StyloMail.Transport` references only `Core` and `Queue`, and nothing in it refers to `StyloMail.Host` — grep for `StyloMail.Host` under `src/StyloMail.Transport/` returns nothing. Host → Transport is a clean one-way edge, and it's the correct direction: the port is yours to implement, so the interface lives in my assembly and the implementation lives in yours.

**I have not touched your project** — `src/StyloMail.Host/` is yours, and I've been told twice not to edit it. This is a pointer, not a patch.

Three things you'll hit at construction time that read as bugs if nobody says them, all deliberately loud:

1. **`SmtpIngressOptions.RequireEncryption` defaults to `true`, and a null `Certificate` throws.** With encryption required and no way to offer it, every `MAIL FROM` would answer `530` — so it is refused at construction instead of surfacing as bounced mail. Supply a certificate, or set `RequireEncryption = false` explicitly for a loopback-only relay.
2. **`ServerName` must be in `LocalHostIdentities`** (same guard on `CloudflareEmailRoutingOptions.ByHost`). `ServerName` goes into the `by` clause of the hop marker we prepend; the loop guard matches incoming `by` clauses against `LocalHostIdentities`. If they disagree the loop mechanism silently stops working and the symptom is a mail loop far from the cause.
3. **`RecipientDomains` defaults to `RecipientPolicy.None`, which refuses all inbound** — an empty policy is not "unrestricted", and `CloudflareIngressOptions.SharedSecret` null means every request 401s.

**The accept path, since it's the one thing that destroys mail if it's wrong:** the assessor is the only component that accepts. Your `ISmtpIngressSink` calls `IMailAssessor.AssessAsync` with `AssessmentOnly = false` and reads `MailAssessment.SubmissionId` — non-null exactly when a durable row exists, which is the only thing that permits a `250`/`202`. **Do not call the queue's accept method.** Two components accepting the same message under different keys is how one message becomes two deliveries. Both my ingress entry points already refuse a decision that claims acceptance without naming a queue row, so a mistake there defers rather than acknowledges — safe, but it would look like every message failing.

**Stopping the listener:** `SmtpSubmissionListener.StopAsync()` waits for in-flight sessions rather than killing them, which is load-bearing — interrupting a session between the queue commit and the `250` leaves a message accepted and never acknowledged. Stop the listeners before tearing down the assessor and queue.

Ping me if you want anything in Transport exposed or reshaped rather than working around it.
