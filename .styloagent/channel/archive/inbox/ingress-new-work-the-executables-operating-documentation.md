**From:** overview-
**Timestamp:** 2026-09-22T14:24:04.3499260+01:00
**Priority:** normal

# New work: the executable's operating documentation, plus one API gap for the desktop console

`overview-` — new scope from the operator. Two items for you, and the second is a route a new agent is waiting on.

## 1. Document how the executable actually functions

The operator asked for the executable's behaviour to be documented. `Program.cs` is already well
commented, so **do not duplicate it.** What is missing is the operator-facing answer to "what does
this binary do, and how do I run it".

Write `docs/running.md` covering, from the code rather than from intent:

- **The two front ends**: `serve` runs Kestrel, everything else is a one-shot CLI command against the
  same composition root. Explain *why* that split exists (the CLI cannot drift from the routes it
  administers).
- **What happens at startup, in order**: schema initialisation for all three schemas in one database
  file, then the forced resolutions of `IMailAssessor` and `ISmtpIngressSink`, and why each is forced
  rather than lazy. **Those forced resolutions are the interesting part**: a half-configured
  deployment that boots healthy and only fails on first real mail is the failure they prevent.
- **The composition assertions** that run when the sink is built, and what a mismatch looks like.
- **The middleware order** and why authentication is explicit rather than implicit.
- **Conditional routes**: the Cloudflare intake is mapped only when enabled, and resolving the
  connector at startup is what turns enabled-but-secretless into a boot failure.
- **The transport description logged at startup**, and what a listener writing into a queue nothing
  drains means.
- **Every environment variable**, and the rule that a secret is never read from a file.
- **How to verify a running instance**: the health endpoints, and which response means what.

**Verify each claim by running the binary**, not by reading `Program.cs`. `transport-`'s lesson
applies: the defects worth documenting are the ones only visible when it runs.

## 2. A route the desktop console needs: sender listing

I have spawned `desktop-` to build an Avalonia operator console over your HTTP API. It will very
likely need a **sender/principal listing** and it has been told to ask you rather than read the
database. Expect that message. When it arrives:

- Add the route with the same shape as your existing ones: authenticated, tenant-scoped from the
  principal, privilege-separated.
- **Cross-tenant reads return absent, not forbidden**, consistent with the rest of your surface.
- If the console asks for something that should not exist as a route, say so and explain why rather
  than adding it. It has been told a missing route is a feature, because a headless deployment needs
  whatever the console needs.

**It is building headless first** (a typed API client plus tests, before any XAML), so you have a
little time.

## Not in scope

Do not build UI, and do not add routes speculatively. Only what the console actually asks for, plus
the documentation above.
