# `access-`, client access proxy (IMAP / POP3 / SMTP submission)

## Your scope
Own `src/StyloMail.AccessProxy/` and `tests/StyloMail.AccessProxy.Tests/`. Create them.
Do **not** modify any other project. Other agents own them.

**Read `.styloagent/spec.md` §9 in full first.** It is the design of record and it contains
decisions you must not re-derive.

## What this is, and what it is not

Users point ordinary mail clients at StyloMail. The client authenticates with **StyloMail-issued
credentials**; StyloMail authenticates to the real provider (Gmail first) using a stored backend
credential, and proxies the session.

**This is a session access proxy, not a transport adapter of the MTA path.** Spec §9.1 has the table.
It shares Core contracts and the assessment pipeline, but **nothing about the queue, spool or
delivery worker applies**. Do not model it as a `transport-`-style adapter.

**The product argument, from §9.3, keep it in mind, because it shapes what "working" means:**
Gmail now requires OAuth, and much client software still cannot do it. A client that only speaks
`LOGIN user pass` cannot reach Gmail at all any more. StyloMail absorbing that **restores access
Google's change removed**. That is the point of the feature.

## Build order for this first slice

1. **The credential seam, do this first, and get it right.** `spec.md` §9.5: the backend credential
   must be **pluggable from the first commit**. Define one seam with an app-password implementation
   now and an OAuth refresh-token implementation later. **Nothing above that seam may branch on which
   kind of credential it is**, and the credential store must carry a discriminator rather than
   assuming a password shape. If the OAuth work later forces changes in callers, the seam was
   implemented wrongly. This is the single most important decision in your brief.
2. **IMAP retrieval** (the safer first slice, no send, smaller blast radius).
3. **POP3 retrieval.**
4. **SMTP submission** last, see the constraint below.

## Hard constraints

1. **Never store a credential in plaintext, and never let one reach a log, exception, decision
   record, or metric.** The same rule the Jev key already follows. If a credential is ever exposed,
   stop, report it without repeating the value, and identify the rotation path, **never rotate a
   shared or production credential on your own initiative.**
2. **The client's StyloMail-side password and the backend credential are separate secrets with
   separate rotation.** Neither may be derivable from the other.
3. **A revoked backend credential must fail closed and surface as an authentication failure**, never
   a silent retry loop against the provider.
4. **Do not rewrite message content passing through retrieval.** Session proxying is not a licence to
   normalise mail; the analysis view is separate from the bytes.
5. **Assessment of retrieved messages is optional and non-blocking.** A client fetching mail must not
   be held hostage by the semantic provider. Decide and document how assessment applies (if at all)
   on this path rather than assuming the store-and-forward pipeline drops in unchanged.
6. **Bound everything**, concurrent sessions, per-session buffers, idle timeouts. A proxy that can be
   made to hold unbounded resources is a denial-of-service vector against the mail it protects.
7. **Use injected `TimeProvider`.** No `DateTimeOffset.UtcNow` in logic.
8. **Mailbox hosting is out of scope** (§9.4 item 6). If a design starts requiring us to hold message
   state *because* the client is offline, that is mailbox hosting, stop and ask.

## Provider constraints you must not design around

- **Google removed username/password auth for IMAP/SMTP/POP** (enforced 1 May 2025). The operator
  chose **app passwords as a documented transitional path** while OAuth verification proceeds. App
  passwords require **2-Step Verification per user**, onboarding must surface that, because the
  failure otherwise presents as an authentication bug.
- **App passwords are being wound down.** Build to the seam, not to the mechanism.

## Tests
xUnit, **no real network and no real Google account**. Fake the backend at the seam. Cover at minimum:
a client authenticating with StyloMail credentials and reaching a faked backend; a wrong client
password rejected without touching the backend; the backend credential never appearing in any log,
exception message or serialized record; a revoked backend credential failing closed rather than
retrying silently; session and buffer bounds enforced; credential-provider selection driven by the
stored discriminator, with both implementations exercised.

## Build notes
- `dotnet` is NOT on PATH:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- SDK 10.0.201, TargetFramework `net10.0`. **Several agents edit `StyloMail.slnx` concurrently**, for
  your own work use `dotnet test tests/StyloMail.AccessProxy.Tests/StyloMail.AccessProxy.Tests.csproj`.
  **Do not judge your work by the solution build**; that is `overview-`'s job.
- Analyzers are **errors** here (`CA*`/`IDE*` fail the build).
- Do not run `git add` or `git commit`. **Never read, print, or reference `jevkey.pvt`.**

## Done when
Your tests are green and you have seen them run. Report to `overview-`: files created, test count,
**the credential seam's shape and why it survives the OAuth migration**, contract friction, and
anything deliberately not done. **If blocked, `send_message` immediately, do not yield silently.**