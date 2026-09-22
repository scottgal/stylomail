# `access-` — saved context

**Scope:** `src/StyloMail.AccessProxy/` and `tests/StyloMail.AccessProxy.Tests/`. Client access proxy —
IMAP/POP3/SMTP session termination with pluggable backend credentials. Design of record: `spec.md` §9.
Adjacency: `transport-` owns the MTA/Cloudflare path (different subsystem entirely — spec §9.1 says do
not model this as a transport adapter). `host-` will need to wire the listener and a durable store.

## State

- **Repo:** `/Users/scottgalloway/RiderProjects/stylomail`, branch `main`, **no commits yet** (whole repo
  is untracked; I did not run `git add`/`git commit` per mission instruction).
- **Build:** `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`.
  SDK 10.0.201, `net10.0`. Analyzers are **errors** — `CA*` fails the build (hit CA1823, CA1869, CA1869).
- **Tests:** 61/61 green, clean rebuild from scratch verified.
  `dotnet test tests/StyloMail.AccessProxy.Tests/StyloMail.AccessProxy.Tests.csproj`
- **In `StyloMail.slnx`** — both entries added, alphabetical (I added them; `overview-`'s note that they
  were "already there" was observing my own edit crossing with their message). Verified both DLLs are
  produced by `dotnet build StyloMail.slnx`, so my code genuinely compiles in the graph.
- **Solution build: GREEN** as of 07:0x (`assess-` landed the two fakes, `ingress-` the Host→Transport
  reference). 0 errors, 0 warnings.
- **`StyloMail.Queue.Tests`: DETERMINISTIC AND GREEN — my earlier report was WRONG.** Re-verified
  8/8 clean runs (queue- ran 15 more; 23 consecutive). **Root cause was NOT the suite**: `queue-`'s
  mutation harness (`.styloagent/tools/mutate.py`) rewrites `src/StyloMail.Queue/*.cs` **in place** in
  the shared tree during a sweep, so concurrent `dotnet test` ran against mutated source. My SQLite
  hypothesis was wrong (each fixture has its own temp dir/file). Retracted to `assess-` and `queue-`.
  **Check before believing any unexplained failure in this repo:**
  `ls .styloagent/tools/.mutation-sweep.lock` + `find src -name '*.bak'` — both absent ⇒ tree clean,
  the failure is real. Filed in the shared issues list.
- **`StyloMail.Host.Tests`: my flakiness claim was WRONG — the sweep reaches Host via Host→Queue.**
  **RETRACTED TWICE.** `dotnet test tests/StyloMail.Host.Tests/...` **12/12 clean with both tree
  signals verified before every run** (`assess-` ran 8 more): 20 clean runs, 0 failures.
  **Do NOT let this become "Host is clean" in the record** — 20 clean runs *bounds* the rate, it does
  not *zero* it. `ingress-` has the two signals and is to escalate rather than shrug if they ever see
  a failure with both clean. `assess-` and I both hold this caveat explicitly.
  `StyloMail.Host` references `StyloMail.Queue`, so a live mutation breaks Host tests on whatever the
  current mutation is — the varying-victim signature I misread as intrinsic contention.
  **My "2/15 on a quiet tree" was unsound:** I verified the signals before an *earlier* gate run and
  ran the 15 samples a minute later without re-checking. **A cleanliness check is valid only for the
  window it was taken in — re-check per run.**
  `ingress-` was sent a phantom and has been urgently retracted. *Still a real latent bug, separately:*
  `DeliveryWorkerHostingTests.ClosedPort()` probe-then-release TOCTOU — not this, fixable on its merits.

## Method note (the thing I got wrong twice this session)

**Re-measure before attributing a cause — including when the convenient answer is "it has cleared".**
"Flaky" and "deterministic" are both claims about a distribution; one run, or three runs inside a single
edit window, cannot distinguish them.

I got this wrong **three times in one session**, which is why it is written down:
1. Inferred "flaky" from 4 samples — undersold a suite failing 9 runs in 10.
2. Claimed Host "cleared" from **a single run**, immediately after correcting `assess-` for inferring
   determinism from three. My one run was *weaker* evidence than the claim I criticised. A correction is
   as much a claim about a distribution as the original finding, and the pull to accept the first sample
   that agrees with you is strongest right after you have been right about something else.
3. `assess-` inferred "deterministic" from 3 runs inside one active-edit window — stability of a *tree
   state*, not of a defect.

Also: **verify a check's exit code, not just its printed output** — my slnx duplicate-check printed
`DUPLICATES FOUND` on a clean file because `uniq -d` exits 0 when it prints nothing.

**The general form:** the recurring failure this session is a claim that reads as assurance while
measuring less than it appears to. Mine were "solution green" (over code not in the solution), "no
duplicates" (reported as duplicates), and "cleared" (from one sample).
4. **Worst one: I attributed the Queue failures to Queue.** I offered two explanations — "their suite is
   flaky" or "a real defect" — and **both put the cause inside the lane I was measuring.** The actual
   cause was a neighbouring lane's tool rewriting the shared tree. I had *just* reasoned to `assess-`
   that "stability measured in an active edit window is stability of a tree state, not a defect" — and
   then failed to carry that insight across the lane boundary.

5. **Host: I "reproduced 2/15 on a quiet tree" — I had not.** I verified the signals before an
   *earlier* gate run and ran the 15 samples a minute later without re-checking. Then presented that as
   counter-evidence to `assess-`, who was right. **A cleanliness check is valid only for the window it
   was taken in. Re-check per run.** Worst kind of error: it *looked* rigorous because I had checked
   the signals — just not when it mattered.

**Clause to hold onto: enumerate causes OUTSIDE the lane before attributing one INSIDE it.** Causes
outside the observed lane are systematically under-weighted, and the harder I have reasoned about a
hazard elsewhere, the more likely I am to miss that it applies here too.

**Four mis-attributions today, all mine, all the same shape: the cause was outside the lane I was
measuring and I put it inside.** Queue (a neighbour's tooling), Host (two lanes away via a dependency
edge), "Host has cleared" (one sample), and "no duplicates" (a false positive from my own shell).
Before publishing any cross-lane diagnosis: (a) name ≥3 candidate causes, at least one OUTSIDE the
lane; (b) re-verify the instrument's preconditions in the same window as the measurement.
- **Nothing references this project yet** except my own test project — no listener wired. `overview-`
  verified 61/61 and closed my lane; told me to stand by.
- Sent `ingress-` an `info` note on the wiring seam (they own Host listener construction + composition).
  **Explicitly non-blocking.**

## Before believing ANY red in this repo (verified in PROTOCOL.md `## Completion gate`)

**UPDATE 07:41 — the sweep is now ISOLATED and this is largely historical.** `queue-` moved sweeps
onto a private `mkdtemp` copy (`make_isolated_copy()` + `copytree`, tests run with `cwd=RUN_ROOT`), so
the shared tree is no longer mutated. I verified that by reading `mutate.py`, same as I verified the
original bug. The two signals below are retained as the detector for a sweep *violating its own
isolation* (which is how SIGKILL still matters).

```
ls .styloagent/tools/.mutation-sweep.lock   # a sweep is running RIGHT NOW
find src -name '*.bak'                      # a sweep was killed / isolation violated
```
**Residual I flagged, `low`, for `queue-`:** `RUN_ROOT = SOURCE_ROOT` is still the module *default*,
reassigned only inside `main()`. Current usage is safe, but any non-`main()` entry point silently
mutates the shared tree — the same bug re-entering via the initialiser.

**My original filing was actioned rather than mitigated**, on the fleet-level argument: the sweep made
the *completion gate itself* lie, at the exact moment every lane uses it.

## Fleet rule (from `overview-`, 2026-09-22)

**Build and test YOUR project to avoid being blocked by another lane's transient red; build
`dotnet build StyloMail.slnx` before declaring done.** Your project green ≠ "nothing I broke".
`StyloMail.Core` is referenced by everything — a Core change is everyone's change. Break a lane, tell
its owner directly.

I first left my projects out of the slnx on ownership-boundary reasoning. That was **wrong**: it makes
any "solution green" claim vacuous, because the solution never compiled my code. Add your projects.

## The credential seam (the decision the mission called most important)

`IBackendCredentialProvider` (`Credentials/IBackendCredentialProvider.cs`) — one impl per credential
kind, selected by the `Discriminator` string on the stored record (never an enum: adding a kind must
not require editing this assembly).

`IBackendAuthenticator` (`Credentials/BackendAuthenticator.cs`) is **the seam itself**: a source of
opaque tokens plus a `BackendAuthStyle` describing wire framing. Drivers relay bytes and never learn
what a token means. **No caller, driver or session mentions password/token/OAuth.**

- `app-password` → SASL `PLAIN` (IMAP/SMTP); POP3 → legacy `USER`/`PASS` (uneven SASL support there).
- `oauth-refresh-token` → SASL `XOAUTH2`; access token fetched per-connection via `IOAuthTokenEndpoint`
  (fake in tests, no network). **Not cached** — deliberate, bounds bearer-token lifetime.

**Why it survives the OAuth migration:** the only branch anywhere is on `BackendAuthStyle`, a statement
about the wire, not the credential. Two kinds sharing a style are indistinguishable from the driver.
Swapping is a data change (rewrite the discriminator), asserted directly by
`ImapSessionTests.OAuthBackedSession_BehavesIdenticallyToAnAppPasswordSession`.

`BackendCredentialRecord` carries **no password-shaped field** — only `Discriminator` + opaque
`ProtectedSecret`. AES-256-GCM, AAD = tenant|account|discriminator, so re-labelling a refresh token as
an app password is a **decryption failure**, not a silent misread.

## Bug found and fixed (worth knowing)

`AesGcmSecretProtector` was `ZeroMemory`-ing the key it **borrowed** from the key ring — blanking the
ring's only copy. First credential worked; everything after silently used zeros. Regression test:
`CredentialSeamTests.KeyRing_KeySurvivesRepeatedUse`. **Key material ownership stays with the ring.**

## Deliberately not done

1. **SMTP submission.** Operator chose retrieval-first (spec §9.5 "Still open" + blast radius). Seam,
   connectors and bounds are all shaped for it; it is a third session class away.
2. **Durable stores.** Only in-memory impls + interfaces. A real SQLite credential/account store belongs
   with the host/persistence layer — writing a second schema in another agent's lane would be worse.
   **Restart loses all credentials → every session fails closed.**
3. **Production key store.** `InMemorySecretKeyRing` is not per-tenant isolated; spec §9.4 item 2's
   isolation/rotation/breach-path half is a deployment concern and is unmet.
4. **`IOAuthTokenEndpoint` implementation** — no real HTTP client written.
5. **No listener/TLS.** Sessions take an `IDuplexChannel`; binding a socket is `host-`'s.

## Hard-won gotchas

- **`CancellationTokenSource.CancelAfter(TimeSpan, TimeProvider)` does not exist.** Use the constructor
  via `TimeoutScope` (`Sessions/TimeoutScope.cs`). Never `Task.Delay` — the injected clock is the point.
- **Don't infer a timeout from `token.IsCancellationRequested`** — a deliberate cancel sets it too. Use
  `TimeoutScope.DeadlineElapsed`.
- **`private protected`** on the base classes: internal helper types (`BoundedLineReader`,
  `ProtocolLineWriter`, `ClientCredentials`) appear in their signatures.
- **Never clear a key/secret you do not own** (see bug above).
- **No literal control bytes in source** — makes the file binary to tooling, and bare `grep` is a
  `ugrep -I` wrapper that **silently skips** such files and reports clean. Use `/usr/bin/grep`.
- **`reply_to_thread` archives without delivering.** Use `send_message` for anything a peer must act on.

## Verification I rely on

- 61 tests in 6 classes; **no network anywhere** (asserted: zero `HttpClient`/`TcpClient`/`Socket` in
  hand-written source; the only transport is `FakeBackendTransport`).
- Byte-verbatim relay asserted with awkward payloads (NUL, bare LF, high bytes) and across many relay
  buffers. Message content is never parsed after auth — that is what makes constraint 4 structural.
