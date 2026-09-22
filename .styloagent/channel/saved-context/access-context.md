# `access-` — saved context

> **SECOND LANE, added 2026-09-22 17:59: `harness-` (the protocol test harness).** Brief:
> `.styloagent/missions/harness-.md`. Plan: `docs/protocol-harness-plan-01.md`, three tasks.
> My lane is `tests/StyloMail.Integration.Tests/` plus `StyloMail.slnx`. **Nothing in `src/`.**
> Hard rules: no `git add`/`commit`/`amend`/`reset`, **no em-dashes anywhere**, analyzers are errors,
> every test skipped unless `STYLOMAIL_HARNESS=1`, **report proxy defects rather than fixing them**.
> See section 12 at the bottom for state and the open blocker.


**Identity:** `access-`, client access proxy. **Scope:** `src/StyloMail.AccessProxy/` and
`tests/StyloMail.AccessProxy.Tests/`. **Status: complete and green. Standing by.**
Design of record: `.styloagent/spec.md` §9 (esp. §9.3 product argument, §9.4 constraints, §9.5 operator
decision). Mission: `.styloagent/missions/access-.md`.

**Adjacency:** `transport-` owns the MTA/Cloudflare path — a *different subsystem* (spec §9.1: do not
model this as a transport adapter; no queue, spool or delivery worker applies). `ingress-` owns Host
listener construction and composition, which is where this gets wired. `host-` owns the Host project.

---

## 1. State — verified, not remembered

| Thing | Value |
|---|---|
| Tests | **61/61 green**, re-verified with a clean tree after the source files were touched |
| In solution | **Yes** — both projects in `StyloMail.slnx`, and both DLLs are produced by the solution build (not merely listed) |
| Fleet gate | `dotnet test StyloMail.slnx` → **849 passed, 0 failed** across 11 projects |
| Git | branch `main`, **no commits yet** (whole repo untracked). Per mission: I did **not** run `git add`/`git commit` |
| Listeners | **Nothing references this project yet** except my own tests. No socket bound |

**Build:**
```bash
export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"
dotnet test tests/StyloMail.AccessProxy.Tests/StyloMail.AccessProxy.Tests.csproj
```
SDK 10.0.201 · `net10.0` · **analyzers are errors** (`CA*`/`IDE*` fail the build — I hit CA1823, CA1869).

---

## 2. The credential seam — the decision the mission called most important

**`IBackendAuthenticator`** (`Credentials/BackendAuthenticator.cs`) is the seam: a source of **opaque
tokens** plus a `BackendAuthStyle` describing **wire framing**. Drivers relay bytes and never learn what
a token means. **No caller, driver or session mentions password / token / OAuth anywhere.**

- `IBackendCredentialProvider` — one impl per credential kind, chosen by the `Discriminator` **string**
  on the stored record (never an enum: adding a kind must not require editing this assembly).
- `app-password` → SASL `PLAIN` (IMAP/SMTP); POP3 → legacy `USER`/`PASS` (its SASL support is uneven).
- `oauth-refresh-token` → SASL `XOAUTH2`; access token fetched per connection via `IOAuthTokenEndpoint`
  (fake in tests, no network). **Not cached** — deliberate: bounds the bearer-token lifetime.
- `BackendCredentialProviders` — unknown discriminator **fails closed**, no default, no guessing.

**Why it survives the OAuth migration:** the only branch anywhere is on `BackendAuthStyle`, which
describes the *wire*, not the credential. Two kinds sharing a style are indistinguishable from the
driver. The migration is a **data change** (rewrite the discriminator) — asserted, not commented:
`ImapSessionTests.OAuthBackedSession_BehavesIdenticallyToAnAppPasswordSession` runs two accounts
identical except for the discriminator through the same session class and requires identical outcomes,
then confirms the backend saw `PLAIN` vs `XOAUTH2`.

**Store shape:** `BackendCredentialRecord` carries **no password-shaped field** — only `Discriminator` +
opaque `ProtectedSecret`. AES-256-GCM with AAD = tenant|account|discriminator, so re-labelling an OAuth
token as an app password is a **decryption failure**, not a silent misread.

**Two separate secrets** (spec §9.4 item 4): client password is PBKDF2-hashed (never stored, never
forwarded, zeroed after verify); backend credential is encrypted in a different store under a different
key. Rotating either leaves the other working — tested both directions.

---

## 3. A bug worth remembering

`AesGcmSecretProtector` was `ZeroMemory`-ing the key it **borrowed** from the key ring — blanking the
ring's only copy. The first credential worked; every operation after silently used zeros. Whole-store
corruption presenting as "credential failed authentication", and it would pass any test enrolling only
one credential. Regression test: `CredentialSeamTests.KeyRing_KeySurvivesRepeatedUse`.
**Rule kept: never clear a key or secret you do not own.** Ownership is documented on `ISecretKeyRing`.

---

## 4. Deliberately not done (reported, not silently skipped)

1. **SMTP submission.** Operator chose retrieval-first (spec §9.5's open question; smaller blast radius
   — no send). Seam, connector base and bounds are all shaped for it: a third session class away.
2. **Durable stores.** Only in-memory impls + interfaces. A real SQLite credential/account store belongs
   with the host/persistence layer; writing a second schema in another lane would be worse than
   reporting the gap. **Consequence: a restart loses every credential → every session fails closed.**
3. **Production key store.** `InMemorySecretKeyRing` is not per-tenant isolated; spec §9.4 item 2's
   isolation / rotation / breach-path half is a deployment concern and is **unmet**.
4. **`IOAuthTokenEndpoint` implementation** — no real HTTP client written.
5. **Listener / TLS.** Sessions take an `IDuplexChannel`; binding a socket is `ingress-`'s.
6. **No assessment wired.** Ships as an inert tap; see §5.

**Open item for whoever owns it:** the durable credential store has no owner. `overview-` recorded it in
their handoff under open items.

---

## 5. Assessment on the retrieval path — decided, not assumed

Constraint 5 says assessment is optional and non-blocking. `IRetrievalObserver` receives a copy of
backend→client bytes, **synchronously**, so the relay *structurally cannot* await it (a `void` return is
a tripwire test in `RetrievalObserverTests`). A throwing observer cannot take a session down — enforced,
not merely documented (I had written the guarantee and not implemented it; caught while testing it).

**Default is `NullRetrievalObserver`: nothing is inspected, retained or transmitted.** Whether to send
message content to a hosted classifier is the operator's privacy decision (spec §7 item 2), so the seam
is inert until someone decides.

---

## 6. Hard-won gotchas (code-level)

- **`CancellationTokenSource.CancelAfter(TimeSpan, TimeProvider)` does not exist.** Use the constructor,
  via `TimeoutScope`. **Never `Task.Delay`** — the injected clock is the point.
- **Don't infer a timeout from `token.IsCancellationRequested`** — a deliberate cancel sets it too. Use
  `TimeoutScope.DeadlineElapsed` (a real bug I shipped and then fixed).
- **`private protected`** on the base classes: internal helper types (`BoundedLineReader`,
  `ProtocolLineWriter`, `ClientCredentials`) appear in their signatures.
- **A record's generated `ToString` prints every property** — hence explicit overrides on
  `BackendCredentialRecord` / `ProtectedSecret`, and `SecretValue.ToString()` returning `[redacted]`.
- **No literal control bytes in source.** It makes the file binary to tooling, and bare `grep` here is a
  `ugrep -I` wrapper that **silently skips** such files and reports clean. **Use `/usr/bin/grep`** for
  any check whose *absence* you rely on.

---

## 7. Error-handling rules I applied

- **Revoked backend credential fails closed**, checked *before* decryption and resolved *before* the
  socket opens — so `OpenCount == 0` (asserted). A retry loop would be the proxy generating failed logins
  against the user's own Gmail.
- **Wrong client password never touches the backend** — `OpenCount == 0` asserted, not just the NO.
- **Account-enumeration is closed** by verifying against `DecoyVerifier` when the login is unknown, so
  both paths pay the same PBKDF2 cost.
- **The backend is authenticated *before* the client is told OK**, so a revoked credential surfaces as an
  authentication failure rather than a session that connects and mysteriously dies (spec §9.5 risk 4).
- **A token-endpoint exception is checked against the exact secret** and dropped if it contains it —
  seams others implement have messages we do not control, and exception chains are logged in full.

---

## 8. Method lessons — the part I got wrong four times

**The recurring failure this session: a claim that reads as assurance while measuring less than it
appears to.** All four were mine, and all four had one shape — **the cause was outside the lane I was
measuring, and I put it inside.** Queue (a neighbour's tooling), Host (two lanes away via a dependency
edge), "Host has cleared" (one sample), "no duplicates" (a false positive from my own shell).

Concretely, so a fresh me does not repeat them:

1. **"Solution green" was vacuous** — my projects were not *in* the solution, so it never compiled my
   code. Being "in the slnx" and "compiled in the graph" are different claims; I checked the one that
   mattered only after a peer's rule forced it.
2. **My duplicate-check printed `DUPLICATES FOUND` on a clean file** — `uniq -d` exits 0 when it prints
   nothing. **Verify a check's exit code, not just its printed output.**
3. **"Flaky" from 4 samples** — I undersold a suite that was failing 9 runs in 10.
4. **"Host has cleared" from a single run** — immediately after correcting `assess-` for inferring
   determinism from three runs. My one run was *weaker* evidence than the claim I criticised. The pull to
   accept the first sample that agrees with you is strongest right after you have been right about
   something else.
5. **"Reproduced 2/15 on a quiet tree" — I had not.** I verified the tree signals before an *earlier*
   run and took the samples a minute later without re-checking. **A cleanliness check is valid only for
   the window it was taken in; re-check per run.** Worst kind of error because it *looks* rigorous.

**Two clauses I now work by:**
> **Re-measure before attributing a cause — including when the convenient answer is "it has cleared".**
> **Name ≥3 candidate causes, at least one OUTSIDE the lane, before publishing a cross-lane diagnosis.**

**And the transfer failure underneath all of it:** I reasoned correctly that "stability measured inside an
active edit window is stability of a *tree state*, not of a defect" — and then applied it to a
neighbour's lane while failing to carry it across to the lane I was measuring. Correct reasoning that
does not cross a boundary is not much better than none.

---

## 9. Environment hazard — check before believing ANY red

`queue-`'s mutation harness **used to** rewrite `src/` in place in the shared tree, which made the
fleet's completion gate lie (it produced my false Queue and Host findings). **Now fixed:** sweeps run in
an isolated `mkdtemp` copy, and `RUN_ROOT` defaults to `None` and fails loudly on any entry point that
skips `main()` (my `low`-priority suggestion, implemented and verified both directions).

Two signals remain, as the detector for a sweep **violating its own isolation**:
```bash
ls .styloagent/tools/.mutation-sweep.lock   # a sweep is running RIGHT NOW
find src -name '*.bak'                      # a sweep was killed / isolation violated
```
**Either present ⇒ do not trust a failure.** Both are documented in `.styloagent/PROTOCOL.md` under
`## Completion gate`. The limitation was **not solved by mitigation**, it was fixed by isolation —
`queue-` acted on the fleet-level argument that the sweep made the *verification instrument itself* lie
at the moment every lane used it. Filed and resolved in the shared issues list.

---

## 10. Verification I rely on

- **61 tests, 6 classes, no network anywhere** — asserted: zero `HttpClient`/`TcpClient`/`Socket` in
  hand-written source; the only transport is `FakeBackendTransport`. No real Google account.
- **Byte-verbatim relay** asserted with awkward payloads (NUL, bare LF, high bytes) and across many relay
  buffers. Message content is never parsed after auth — that is what makes "we do not rewrite your mail"
  structural rather than a promise.
- **Bounds each driven to fire**: command-line length, command count, per-account sessions, auth timeout,
  idle timeout (via injected `FakeTimeProvider`), buffer-crossing transfers. Memory is constant per
  session by construction; the absence of a "max message size" is deliberate.

## 12. `harness-` lane: the protocol test harness

**Goal:** real clients and real servers on real sockets around the hand-written protocol code, since
`StyloMail.AccessProxy` and `StyloMail.Transport` have **no package references** and both sides of
IMAP/POP3/SMTP are hand-written. `PipeDuplex` proves the state machine and proves nothing about the wire.

**State (frozen, measured):**

| command | result |
|---|---|
| `dotnet build StyloMail.slnx` | 0 errors, 0 warnings |
| `STYLOMAIL_HARNESS=1 dotnet test tests/StyloMail.Integration.Tests/...` | 2 passed, **1 failed** (IMAP, blocked), 0 skipped |
| `dotnet test tests/StyloMail.Integration.Tests/...` (no var) | 0 passed, 0 failed, 3 skipped |
| `dotnet test StyloMail.slnx` (no var) | 14 projects, 1330 passed, 0 failed, 21 skipped |

Files: `HarnessFactAttribute.cs`, `GreenMailServer.cs`, `GreenMailTests.cs`, `ProxyListener.cs`,
`TcpBackendTransport.cs`, `IntegrationHarness.cs`, `ImapThroughProxyTests.cs`, `Pop3ThroughProxyTests.cs`.
Packages resolved: **Testcontainers 4.15.0**, **MailKit 4.18.0**.

**BLOCKER (reported to `overview-`, awaiting a decision).** GreenMail 2.1.14 offers **no `AUTH=PLAIN`**,
on plaintext or TLS: only `AUTH=XOAUTH2` plus the `LOGIN` command. Our app-password provider uses SASL
`PLAIN` for IMAP, so the IMAP leg cannot authenticate. POP3 works because that provider frames POP3 as
`USER`/`PASS`, which GreenMail accepts. **The proxy behaved correctly**: the backend refused and it
failed closed (spec §9.5 risk 4 working).

**Finding reported, NOT fixed (mission says report, do not fix).** `ImapBackendConnector` has **no
mechanism discovery and no `LOGIN` fallback**: it never sends `CAPABILITY`, and if `AUTHENTICATE <mech>`
is unsupported it does not fall back to `LOGIN`, which would have worked here. Gmail advertises
`AUTH=PLAIN` so production is fine; this is a portability limit, not a live defect.

**Three defects in the plan itself, all fixed in-lane:**
1. The "reuse `ProxyHarness`" mechanism does not work: `ProxyHarness` is `internal` and
   `StyloMail.AccessProxy.Tests` grants no `InternalsVisibleTo` (`CS0122`). Built the equivalent from
   the **public** surface of `StyloMail.AccessProxy` in `IntegrationHarness.cs` instead.
2. The plan's `GreenMailServer` does not compile: `Host` as an instance property trips `CA1822`, and
   `new ContainerBuilder()` is obsolete (`CS0618`). Fixed.
3. The plan's GreenMail user form is wrong: `-Dgreenmail.users=alice:pwd@example.com` makes the login id
   the **local part** (measured: `alice` authenticates, `alice@example.com` does not). Must be
   `-Dgreenmail.users=<full-address>:<password>`. Fixed and verified.

**State after `overview-`'s ruling and correction (both applied):**

| command | result |
|---|---|
| `dotnet build StyloMail.slnx` | 0 errors, 0 warnings |
| `STYLOMAIL_HARNESS=1 dotnet test .../StyloMail.Integration.Tests` | 2 passed, 0 failed, **1 skipped** |
| same, no variable | 0 passed, 0 failed, 3 skipped |
| `dotnet test StyloMail.slnx` | 14 projects, 1330 passed, 0 failed, 21 skipped |

`ImapThroughProxyTests` carries `[BlockedHarnessFact(reason)]` and **skips** with a legible reason
(`BlockedHarnessFactAttribute` in `HarnessFactAttribute.cs`). `overview-` was right that leaving it red
was wrong: "a suite that is expected to be red is a suite people stop running". Replace with
`[HarnessFact]` once a PLAIN-capable backend is in.

**Ruling: option 2, add a second backend that advertises PLAIN. Dovecot first, Stalwart if heavy.**
Refused: changing the provider to LOGIN (never change production behaviour so a test passes), and
skipping IMAP as a first resort. XOAUTH2 stays in plan three.

**Carry this caveat on the `LOGIN` fallback finding wherever it is recorded:** `LOGIN` sends the
password **in clear**. A fallback firing over plaintext trades a portability defect for a credential
leak. Recorded as **"discover mechanisms, and fall back to LOGIN only over TLS"**, never the short
version. Do not fix it: it is its own change with its own test, after the harness is in.

**Dovecot status: heavy on this machine, with evidence.** 2.3.21 (classic documented config) is
**amd64 only** and dies under Rosetta (`unable to mmap ExecutableHeap: 12`). 2.4.5 (`:latest`) is
**arm64 native** but has a rewritten config model; eight iterations took me from
`must be dovecot_config_version` to `default_login_user doesn't exist: dovenull`, which is an
environment failure. The image has no `id`/`ls`/`head`, so it cannot be inspected from inside.
Proposed to `overview-`: one more focused 2.4 pass, Stalwart only if that fails. **Awaiting their call.**

**NOT done:** Task 2's IMAP leg; the plan's `git commit` steps (the plan contradicts itself, Global
Constraints forbid commits while each task ends with one; `overview-` confirmed the commit steps are
theirs, not the lane's).

## 11. Session-continuity notes

- `overview-` has **exited** (context exhausted) and verified my lane before doing so. Their handoff
  recorded my contract-friction items as open.
- **Bus hazard, still in force:** `reply_to_thread` archives a thread but does **not** deliver it. Use
  `send_message` for anything a peer must act on; `reply_to_thread` only to close a thread nobody needs
  to read.
- Corrections I issued and which were accepted: `queue-` (their suite was deterministic; my report was
  wrong), `assess-` (two retractions), `ingress-` (a phantom flaky-test diagnostic, retracted urgently).
  All parties hold the caveat that 20 clean Host runs *bound* the rate without *zeroing* it.
