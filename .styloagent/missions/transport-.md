# `transport-`, SMTP/MTA transport, delivery port, Cloudflare connector

## Your scope
Own `src/StyloMail.Transport/` and `tests/StyloMail.Transport.Tests/`. Create them.
Do **not** modify any other project. Other agents own them. If a contract must change,
`send_message` the owner and copy `overview-`.

**You are the last unstarted component.** Everything else exists: `Core`, `Persistence`, `Jev`,
`Policy`, `Mime` (91 tests), `Adaptive` (100), `Queue` (54), `Host` (42), `Assessment` (70).

## Read first
- `email-proxy-spec.md` (repo root), **§10 "Mail transport correctness" is your detailed brief.**
  This is the specification of record for mechanics.
- `.styloagent/spec.md`, **§8 is now settled: Cloudflare Email Routing only.** Read it before
  planning anything.
- `.styloagent/architecture.md`, where you sit, and structural decisions 6 and 7 in particular.
- `src/StyloMail.Core/`, `MailEnvelope`, `MailDirection`, `MailAnalysisInput`,
  `PayloadReferences`, `RecipientDisposition`/`DeliveryState`, `IMailAssessor`.
- `src/StyloMail.Queue/`, `QueueStore`, `SpoolStore`, `QueueSchema` v3. The queue **deliberately
  owns no SMTP**; it consumes an injected delivery port, which is yours to implement. Coordinate
  with `queue-` on that port's exact shape rather than inventing a parallel one.

## What to build

**1. SMTP/MTA handoff (the default deployment).** Impress and egress behind an established MTA, **do not build a public MX service from scratch.** Enforce outbound authentication and authorised
sender identities; accept inbound only for configured recipient domains; **no unauthenticated
third-party relay**; require TLS wherever credentials or a trusted internal handoff are used.

**2. The delivery port** the queue's worker calls. It must not leak SMTP into the queue.

**3. Cloudflare Email Routing connector, inbound only.** A Worker binding receives the raw message
and hands it to StyloMail. **No OAuth, no stored credentials, no mailbox read.** This keeps the
default deployment at **zero provider secrets**, which is precisely why it was chosen.

## Hard constraints, these are the correctness rules, not style

1. **An SMTP `250` after `DATA` transfers delivery responsibility.** Either the payload, routing and
   queue metadata are durable **before** acceptance, or defer without accepting. Disk full or
   unavailable storage must never yield a successful acceptance. `queue-` already enforces this,    do not reimplement it.

   > **Correction (2026-09-22, `overview-` ruling).** The sentence above previously read "route
   > through it", which is ambiguous enough to be read as *call the queue's accept method*. It must
   > not. **The assessor is the only component that accepts.** The transport's ingress hands the
   > message to an `ISmtpIngressSink` implemented by the composition root, which calls
   > `IMailAssessor.AssessAsync` with `AssessmentOnly = false` and reads `MailAssessment.SubmissionId`
   > (non-null exactly when a durable row exists). Two components accepting the same message under
   > different idempotency keys is how one message queues, and is delivered, twice.
2. **Preserve signed content.** Do not rewrite subject, body or links by default; your analysis
   annotations belong in the ledger, not in the message. **Note (2026-09-22):** `overview-` ruled
   that ingress *does* prepend exactly one `Received:` hop marker, byte preservation exists for
   signature integrity, not byte-identity for its own sake, and a relay that does not mark its own
   hop cannot detect itself in a loop. Body and all existing headers stay byte-for-byte; the hop
   marker is the only addition, at ingress only. Do not "restore" byte-identity by removing it.
   Outbound signing happens only after any
   intentional modification. Rewriting signed content invalidates DKIM.
3. **Never send bespoke warnings or bounces to an unverified, possibly spoofed `From`.** Permanent
   failures are recorded and left to the upstream MTA's DSN policy.
4. **Enforce loop detection and hop limits.**
5. Accept `Authentication-Results` only from configured trusted boundary verifiers, never from a
   header the message author supplied. SPF needs the original sending connection context, not the
   proxy's own address. (The agreed detail convention is documented on `AuthenticationResult.Detail`
   in Core, `dkim` carries `d=`/`s=` from the verifier, never from the message's own header.)
6. **Bounds are features.** Connection limits, message size, timeouts, in-flight caps. A transport
   that can be made to hold unbounded resources is a denial-of-service vector against the mail it
   is supposed to protect.
7. **Use injected `TimeProvider`**; no `DateTimeOffset.UtcNow` in logic.

## Explicitly NOT in scope
Gmail/Outlook mailbox access, Mailchimp/Mandrill, SendGrid. The operator **declined** all three and
the source spec's exclusions stand. Do not build toward them.

## Tests
xUnit, no real network, use an in-process fake SMTP endpoint or an injected transport seam.
Cover at minimum: acceptance refused when durable storage is unavailable (no 250); original bytes
preserved byte-for-byte through the handoff; unauthenticated relay refused; inbound for an
unconfigured recipient domain refused; hop limit trips; loop detection trips; TLS required where
credentials are used; the delivery port reports per-recipient outcomes without leaking SMTP types.

## Build notes
- `dotnet` is NOT on PATH:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- SDK 10.0.201, TargetFramework `net10.0`. Solution file is `StyloMail.slnx`, but **several agents
  edit it concurrently**, for your own work use
  `dotnet test tests/StyloMail.Transport.Tests/StyloMail.Transport.Tests.csproj`. **Do not judge
  your work by the solution build**; that is `overview-`'s job.
- Analyzers run as **errors** here (`CA*`/`IDE*` fail the build).
- Do not run `git add` or `git commit`. **Never read, print, or reference `jevkey.pvt`.**

## Done when
Your tests are green and you have seen them run. Report to `overview-` with: files created, test
count, the delivery-port shape you agreed with `queue-`, contract friction, and anything
deliberately not done. **If you are blocked, `send_message` immediately, do not yield silently.**