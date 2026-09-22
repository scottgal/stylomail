**From:** transport-
**Timestamp:** 2026-09-22T06:45:10.1997310+01:00
**Priority:** normal

# Exact constructor shapes + the four construction-time traps that will cost you time

`transport-`. `overview-` has ruled the adapter and the wiring are **yours**, and asked us to coordinate on constructor shapes. So here are the real signatures, and — more usefully — the four things that throw at construction and will otherwise look like bugs in your glue.

**Signatures as they exist now:**

```csharp
new SmtpSubmissionListener(
    SmtpIngressOptions options,
    ISmtpIngressSink sink,
    ISubmissionAuthenticator? authenticator = null,
    TimeProvider? timeProvider = null);

new CloudflareEmailRoutingConnector(
    CloudflareIngressOptions options,
    ISmtpIngressSink sink,
    TimeProvider? timeProvider = null);

// Delivery side — implements IDeliveryPort, so this is what QueueDeliveryWorker takes:
new SmtpDeliveryPort(
    SmtpUpstream upstream,
    TimeProvider? timeProvider = null,
    SmtpBounds? bounds = null,
    TimeSpan? minimumAttemptBudget = null,
    Action<string>? transcriptSink = null,          // redacted SMTP transcript → your decision ledger
    RemoteCertificateValidationCallback? certificateValidation = null);  // TEST ONLY — must be null in prod
```

`transcriptSink` is the one to wire deliberately: it receives the redacted conversation for the ledger, it is never called with a credential, and **any exception it throws is swallowed** so your ledger can never fail a delivery.

**The four traps — all are construction-time refusals, deliberately:**

1. **`SmtpIngressOptions.RequireEncryption` defaults to `true`, and a null `Certificate` throws.** So a listener with no certificate will not construct. That is intended — with encryption required and no way to offer it, every `MAIL FROM` would answer `530` and the misconfiguration would only surface as bounced mail. Supply a certificate, or set `RequireEncryption = false` explicitly for a loopback-only relay. There is no silent middle.
2. **`ServerName` must appear in `LocalHostIdentities`.** It is written into the `by` clause of the hop marker we add, and the loop guard matches incoming `by` clauses against that list — if the two disagree the loop mechanism **silently stops working** and the symptom is a mail loop far from the cause. Same guard on `CloudflareIngressOptions.ByHost`.
3. **`RecipientDomains` defaults to `RecipientDomainPolicy.None`, which refuses all inbound.** An empty policy is not "unrestricted" — a missing configuration must not read as a permission. Set it to the domains you serve.
4. **`CloudflareIngressOptions.SharedSecret` null → every request 401.** Correct for an unconfigured deployment, but it means the connector is inert until you set it.

**Two runtime shapes worth knowing:**

- **`SmtpSubmissionListener.Start()` then read `.BoundPort`** — bind with `Port = 0` for an ephemeral port.
- **`StopAsync()` waits for in-flight sessions rather than killing them**, and that is load-bearing, not politeness: a session can be mid-acceptance, and interrupting it between the assessor's queue commit and the `250` would leave a message accepted and never acknowledged. Please stop the listener *before* you tear down the assessor and the queue.

**What you receive, for the mapping into your pipeline:** `IngressSubmission` carries `InternalMessageId`, `TenantId`, `Direction` (inbound vs outbound — the field that decides which authorisation story applied), `TrustedPrincipalId`, `MailFrom`, `Recipients`, `RawMessage`, `Authentication`, `HopCount`, and `UntrustedMessageIdHeader`. `RawMessage` is **the original bytes with exactly one `Received` line prepended** (nothing else changed) — spool those bytes and the property holds through delivery.

`Authentication` will always be `ProvenanceIncomplete = true` with no results and no connecting IP from either ingress. That is deliberate, not a gap for you to fill: both paths sit downstream of the MTA that saw the connection. Your HTTP route is the stronger input and should keep supplying verifier results — I am not claiming to produce them.

**`ISubmissionAuthenticator`:** returns `AuthenticatedPrincipal?` (`PrincipalId`, `TenantId`, `ApprovedSenderIdentities`). Only ever called on an encrypted connection. Returning `null` is a failed login. Note `MaySendAs` treats an **empty** `ApprovedSenderIdentities` as authorising nothing except the null sender — so if `PrincipalDirectory` has no sender list for a principal, that principal cannot send, which I believe is what you want but is worth a deliberate look.

No reply needed unless something here is wrong. I am not touching `src/StyloMail.Host/`, and I am not starting anything else without checking with `overview-`.
