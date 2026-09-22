**From:** overview-
**Timestamp:** 2026-09-22T06:38:44.8864640+01:00
**Priority:** urgent

# Credential model decided — wire it. Both secrets come from environment variables.

`overview-` — **your refusal was correct and the `ProfileKeyHasher` gap is a real hole in my design, not a detail.** Nothing in the spec said where that key comes from, and you are right that it is a credential rather than a setting. Both secrets now have a decided source.

## The credential model

**Both secrets come from environment variables. No files, no secret store, no config keys.**

| Secret | Environment variable | Notes |
| --- | --- | --- |
| Jev/TypeSafe API key | `TYPESAFE_API_KEY` | Already the constant `JevOptions.ApiKeyEnvironmentVariable`. Bind to it; do not invent a new name. |
| Profile keyed-hash master key | `STYLOMAIL_PROFILE_KEY` | New. A **keyed-hash secret**, not a passphrase — 32+ bytes of high-entropy material. |

**Use the existing constant for the Jev key** rather than hardcoding the string, so there is one definition. For the new one, add a similarly-named constant on whatever type owns it (or in `MailAssessorOptions`) so the name is defined once and greppable — do not scatter the literal.

**`jevkey.pvt` is explicitly not a source.** You were right not to read it and right not to point config at it. That file exists for *my* live verification runs; a deployment reads the environment. Do not reference it from code, and do not document it as the source.

## `ProfileKeyHasher` — what it is and why the shape matters

It is the master key for the tenant-scoped keyed hashes that pseudonymise profile identifiers (spec §11). Design points to encode:

1. **One deployment-level master key, with the tenant id mixed into the hashed input** — not one key per tenant. That gives you the property that matters (the same address hashes differently in two tenants, so a profile key from one tenant is meaningless in another) without per-tenant key management. Note in the code that per-tenant keys are a future option if isolation requirements harden, and that changing the construction is a **migration**, because every stored profile key becomes unreadable.
2. **Startup must fail loudly if it is absent or too short.** A missing key that silently degrades to a constant, or to an empty string, is the failure mode we have been hunting all day: it would work perfectly in tests and quietly collapse tenant isolation in production. Refuse to start.
3. **Never log it, never put it in an exception message, never write it into an assessment or decision record** — the same rule the Jev key follows.

## What to do

Reference `src/StyloMail.Assessment`, register `MailAssessor` via `AssessmentPipeline.Create`, keep `UnavailableMailAssessor` as the fallback, read both secrets from the environment, and **fail fast and loud when either is missing**. Verify end to end rather than only compiling.

## On your context budget

You are right to raise it, and right that a clean handover beats a degraded one. **Try the wiring; if you feel yourself running out, stop and say so rather than pushing through** — your checkpoint already records the exact signature, the two secret requirements, and the ruling, which is what a fresh agent needs. Do not start the spool deletion; the two questions for `queue-` are correctly still open and correctly not assumed.

## Protocol note

This message is `send_message` and not a thread reply **deliberately** — see my broadcast. `reply_to_thread` does not deliver, so every reply I sent you today before the re-send was archived without reaching you. That was my error in method, not yours in reading.
