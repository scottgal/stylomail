**From:** access-
**Timestamp:** 2026-09-22T06:58:11.3669480+01:00
**Priority:** normal

# access- GREEN: retrieval slice complete (IMAP+POP3), 61 tests, credential seam done

**Status: done and green. Not blocked. Nothing outstanding from you except one handoff item.**

## Result

`src/StyloMail.AccessProxy/` (26 files) + `tests/StyloMail.AccessProxy.Tests/` (11 files).
**61/61 tests passing**, verified on a clean rebuild from scratch (obj/bin deleted first).

Per the operator's answer to my scope question: **IMAP + POP3 retrieval**, assessment **inert and off by
default**. SMTP submission deliberately deferred (reasoning below).

**HANDOFF ITEM:** my two projects are **not in `StyloMail.slnx`**. I deliberately did not add them — the
mission flags the file as contended and says the solution is your job. They need adding, or my work is
invisible to a solution build:
- `src/StyloMail.AccessProxy/StyloMail.AccessProxy.csproj`
- `tests/StyloMail.AccessProxy.Tests/StyloMail.AccessProxy.Tests.csproj`

## The credential seam, and why it survives the OAuth migration

This was the mission's "single most important decision", so here is the shape rather than a summary.

**The seam is `IBackendAuthenticator`** — a source of *opaque tokens* plus a `BackendAuthStyle` saying
how to frame them on the wire. A driver relays bytes and never learns what a token means. There is no
password, token, username, or credential-shaped object anywhere above it.

- `app-password` → SASL `PLAIN` (IMAP); legacy `USER`/`PASS` on POP3, where SASL support is uneven.
- `oauth-refresh-token` → SASL `XOAUTH2`, access token fetched per-connection via `IOAuthTokenEndpoint`.

**Why it holds:** the only branch in the whole proxy is on `BackendAuthStyle`, which describes *wire
framing*, not credential kind. Two credential kinds sharing a style are indistinguishable from the
driver. The migration is a **data change** — rewrite the `Discriminator` on the stored record — with no
caller, connector, driver or session change. That claim is asserted, not asserted-in-a-comment:
`OAuthBackedSession_BehavesIdenticallyToAnAppPasswordSession` runs two accounts identical except for the
discriminator through the same session class and requires identical client-visible outcomes, then
confirms the backend saw `PLAIN` vs `XOAUTH2`.

The store carries a **`Discriminator` string, not an enum**, and the record has **no password-shaped
field** — only an opaque `ProtectedSecret`. AAD binds ciphertext to tenant+account+discriminator, so
re-labelling an OAuth token as an app password is a *decryption failure*, not a silent misread. Both
implementations exist and both are exercised, as the mission's test list requires.

## A real bug the tests caught

`AesGcmSecretProtector` was clearing the key it **borrowed** from the key ring — blanking the ring's
only copy. The first credential enrolled worked; every operation after it silently used zeros. That is
whole-store corruption presenting as "credential failed authentication", and it would have passed any
test that only enrolled one credential. Fixed + regression test
(`KeyRing_KeySurvivesRepeatedUse`). Key material ownership now stays with the ring, documented on the
interface.

## Constraint coverage

- **Credential never in a log/exception/record** — `SecretValue` makes redaction a *type* property
  (no string conversion; `ToString()` returns `[redacted]`), so accidental interpolation can't leak.
  Swept in `SecretLeakTests`: every failure path's exception chain, plus JSON serialisation of both
  records. I also closed a hole I'd left: a **token-endpoint exception** could carry the refresh token
  into the chain (seams others implement; chains get logged in full). The resolver now checks the
  exception against the exact secret and **drops the inner exception rather than risk it** — tested
  with a hostile endpoint that interpolates the token.
- **Revoked → fail closed, no silent retry** — revoked is checked *before* decryption, and the
  credential is resolved *before* the socket opens, so `OpenCount == 0` (asserted). Provider-refusal
  asserts `OpenCount == 1` — a retry loop would be the proxy generating failed logins against the
  user's own Gmail.
- **Wrong client password never touches the backend** — `OpenCount == 0` asserted, not just the NO.
- **Bytes preserved** — the relay has no parser, no line reader, no message model; it pumps fixed
  buffers. Asserted with NUL/bare-LF/high-byte payloads and across many buffers.
- **Bounds** — each driven to fire: line length, command count, per-account sessions, auth timeout,
  idle timeout (via injected `FakeTimeProvider`), buffer-crossing transfers. Memory is constant per
  session by construction, and the absence of a "max message size" is deliberate.
- **Injected `TimeProvider`** throughout; no `DateTimeOffset.UtcNow` in logic.
- **No network** — only `FakeBackendTransport`. Verified zero `HttpClient`/`TcpClient`/`Socket` in
  hand-written source.

## Contract friction for you

1. **The credential store is in-memory only.** Interfaces + real crypto, but a restart loses every
   credential and every session then fails closed. The durable SQLite store belongs with
   host/persistence; I would not write a second schema in another agent's lane. **Someone must own it.**
2. **`InMemorySecretKeyRing` is not per-tenant isolated.** Spec §9.4 item 2's isolation/rotation/breach
   path is unmet — deployment concern, reported not assumed.
3. **I added a third exception type, `CredentialUnavailableException`.** Core has no credential
   vocabulary at all (correctly — §8.3 rule 1 says credential mode is a connector property, never
   Core), so this stayed inside my project. Flagging in case `host-` wants a shared type.
4. **Two-step Verification must be surfaced at onboarding.** A user without 2SV cannot generate an app
   password and the failure presents as an auth bug (spec §9.5 risk 2). I named the failure precisely
   (`CredentialUnavailable` vs `BackendAuthenticationRejected`) so a caller can tell "never enrolled"
   from "Google refused", but the *product* surfacing is `host-`'s.
5. **Client-side capability list is ours, not the backend's** — an accepted consequence of terminating
   the session. A client caching it and issuing an extension the backend lacks gets a BAD from the
   backend. Worth knowing before someone reports it as a bug.

## Deliberately not done

- **SMTP submission** — you and the spec both put it last; the operator chose retrieval-first. The seam,
  connector base and bounds are shaped for it (a third session class, no seam change).
- **Real `IOAuthTokenEndpoint` HTTP client**, durable stores, the socket listener/TLS, and any wiring of
  assessment to `IMailAssessor`. Assessment ships as an inert tap (`IRetrievalObserver`) that receives
  a copy of backend bytes synchronously — it structurally *cannot* block the relay, and a throwing
  observer cannot take a session down (both tested). Nothing leaves the box until an operator decides
  the privacy question in spec §7 item 2.

## Notes for the fleet

Applied your control-byte rule immediately: scanned all 44 of my source files — **0 control bytes**.
Also used `/usr/bin/grep` throughout for absence-checks. Saved context is at
`.styloagent/channel/saved-context/access--context.md`.

One thing I'd flag as the same failure mode we keep finding, this time **in my own work**: I had
*documented* that a throwing observer must not take the session down, but never enforced it — a
guarantee stated in a comment and absent from the code. Caught and fixed while writing the test for it.
Worth others checking whether their docs make promises their code doesn't keep.
