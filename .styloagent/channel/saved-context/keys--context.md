# `keys-`, saved context

**LANE COMPLETE, 2026-09-22. Parked by `overview-`. Do not start anything new.**

Both items are landed, verified and committed; the follow-up my own precedence rule caused is closed.
`d135baa` (the lane) → `b91da65` (assignment of the follow-up) → `4829501` (the follow-up). Nothing of
mine is uncommitted. If the credential path needs to move again, this file is the cold start.

## Identity + scope

`keys-` owns **StyloMail's minted-key credential path**: the Host principal store that holds key
digests, the `stylomail key create|list|revoke` CLI, and the authentication path for the HTTP surface
and the SMTP submission listener (both in `src/StyloMail.Host`).

**Files I created:** `Auth/HostPrincipal.cs`, `Auth/MintedApiKey.cs`, `Auth/MintedPrincipalStore.cs`,
`Cli/KeyCommands.cs`, `tests/StyloMail.Host.Tests/MintedKeyTests.cs`.
**Files I edited:** `Auth/PrincipalDirectory.cs` (rewritten), `Auth/HostAuthOptions.cs`,
`Auth/HostAuthentication.cs`, `Auth/ApiKeyAuthenticationHandler.cs`, `Cli/CliApplication.cs`,
`Cli/CliCommands.cs`, `Endpoints/SessionEndpoints.cs`, `Hosting/PrincipalSubmissionAuthenticator.cs`
(docs only), `Storage/HostDatabase.cs` (DDL), `docs/running.md`.
For the follow-up, with the lane boundary lifted for that change only: `Endpoints/ListingEndpoints.cs`,
`Contracts/ListingResponses.cs`, `tests/…/ListingEndpointsTests.cs`, `tests/…/TestSupport.cs`.
Never touched anything under `src/StyloMail.Desktop`; `desktop-` mirrors the contract from its side.

Repo `stylomail`, branch `main`. Base `507fe5d`; `overview-` committed and pushed each step.

## State, 2026-09-22

**265 Host tests green, three consecutive clean runs.** Baseline before this lane was 213, so 52 new.
`dotnet build StyloMail.slnx`: 0 errors, 0 warnings.

Live probe `/tmp/keys-work/probe_keys.py`: **38/38** against a real `dotnet StyloMail.Host.dll serve`
process, a real Kestrel, a real SMTP submission listener over `STARTTLS`, and the same binary driven
as the CLI. Re-run it rather than trusting the number.

Build/run:
```
export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"
dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj
python3 /tmp/keys-work/probe_keys.py
```

**Measure with the tree signals first** (same rule `ingress-` recorded):
`ls .styloagent/tools/.mutation-sweep.lock` and `find src -name '*.bak'`. Both clean here.

## The design, and the one thing that is load-bearing

A minted value is `smk_<key id>_<secret>`: the id is **hex**, the secret is base64url. The id is what
makes a slow KDF affordable — a presented key names its own row, so verification is **one** PBKDF2-HMAC-SHA256
derivation (per-key salt, 600,000 iterations, measured at 71 ms) rather than one per stored principal,
and no cheap digest of the secret is written down anywhere.

**The id alphabet must stay hex.** The parser finds the boundary at the first `_` after the scheme, and
base64url's alphabet *contains* `_`. Minting ids as base64url — my first version — made ~30% of keys
contain the separator, so the id was read short and those keys never resolved. It presented as
4-6 of 51 tests failing, a **different set every run**, which reads exactly like a flaky host. Found by
measuring (three failing runs) rather than by reading. `Every_minted_value_names_its_own_row` pins it;
mutation-verified by putting base64url back (test goes red).

Resolution: store first, environment as fallback, and where a name is in both the store wins
**wholesale** — never union privileges, never union keys. An environment entry the store has claimed
resolves nothing, and it stays claimed after the key is revoked (revocation that resurrected a
forgotten configuration credential would not be revocation). `StoreClaimsPrincipal` is by *any* row,
live or revoked, for that reason.

Revocation is immediate **across processes**. The store carries a change counter
(`host_principal_store.version`) that every mint and every revocation increments in the same
transaction; `PrincipalDirectory` caches resolutions keyed by a SHA-256 of the presented key (memory
only, never persisted) and validates against that counter. The CLI that revokes is not the server, so
local eviction was never an option. `StyloMail:Auth:ResolutionCacheLifetime` (default 30 s) is a
backstop for a change written *without* bumping the counter.

## Falsified assumptions, worth not re-deriving

- **SMTP `AUTH` requires TLS.** `SmtpIngressSession` refuses `AUTH` with `538` unless `_encrypted`,
  and only advertises it when encrypted. So an in-suite SMTP AUTH test needs a real certificate; the
  suite instead pins the seam through the container's own `ISubmissionAuthenticator`, and the **probe**
  does the wire-level `STARTTLS` + `AUTH PLAIN`.
- **An unauthorised sender is `553 5.7.1`, not `550`.** My probe asserted 550 and the host answered
  553; the listener was right and the probe was wrong. Same lesson `ingress-` recorded: when a probe
  disagrees, suspect the probe.
- **`key revoke` used to pick the wrong row.** A principal accumulates one row per mint, and
  `List()` orders by `(principal_id, created_at)`, so selecting by principal id alone found the
  *oldest* — after a re-mint, the revoked one. Every replacement key would report "already revoked"
  and be permanently un-revocable. Caught by reading after the probe was green, then pinned by
  `A_replacement_key_can_itself_be_revoked` and mutation-verified.
- **`--by` is required on `key create` and `key revoke`.** The agreed design doc writes the verbs
  without it; the established rule in this CLI (`quarantine release --by`) is that a mutating command
  with no identity to sign with must not invent one. Minting is how access is granted, so the rule is
  at least as strong here. Flagged to `overview-` as a deviation from the sketch.
- **`key create` prints the value, a blank line, and the notice. Nothing else.** No summary block:
  `KEY=$(stylomail key create …)` is the obvious script, and anything extra on stdout lands in it.

## Deviations worth knowing

- **`key revoke` of an already-revoked principal exits 0**, printing that it was already revoked.
  Contrast `quarantine release`, which exits non-zero when there is nothing to release. The
  reasoning: there, finding nothing means the message was not where the caller thought it was, which
  is a state mismatch; here the end state the caller asked for already holds.
- **`ForTenant` now excludes configuration entries the store has claimed**, because it already
  excludes principals that cannot authenticate ("it cannot send, and listing it as a sender would
  advertise an account that does not exist"). Required by wholesale precedence; it is my file, and no
  existing test changed behaviour.

## Follow-up landed: the sender listing (boundary lifted for this one change)

`overview-` verified and committed the lane as `d135baa`, then handed me the listing follow-up with
the lane boundary lifted for that change only. Landed:

- **`PrincipalDirectory.ForTenant` is gone**, replaced by `SendersForTenant(tenantId)` returning
  `IReadOnlyList<PrincipalInventoryEntry>`: both sources, tenant-filtered, **only rows that can
  authenticate**, ordered by principal id. "Who counts as a sender" now lives beside the precedence
  rule that decides it instead of being re-derived by the route.
- **`SenderResponse.Source`** (`store` | `environment`) on every row of `GET /v1/senders`.
  `SenderListingResponse.From` takes the inventory entries.
- **`PrincipalInventoryEntry.SourceName` / `StatusName` / `CanAuthenticate`** are the one spelling of
  those values. The CLI table, `key list --json` and the listing all read them, so the JSON status
  went from `readonly` to `read-only` in the same change: three vocabularies for one value is how a
  console learns a word the CLI never says.
- **The pinned case.** A name that is both configured and minted is listed **exactly once**, as a
  `store` sender. Before this change it vanished from the listing entirely, because wholesale
  precedence drops the configuration entry and nothing replaced it. Five tests in
  `ListingEndpointsTests`; mutation-verified by making `SendersForTenant` skip store rows, which turns
  four of them red including that one.
- `docs/running.md` claimed the listing "is built from the configuration that holds every principal's
  API key", which this change made false. Corrected. `docs/console-management-design.md` was already
  updated by `overview-`.

**Suite is now 270 green, three consecutive runs. Live probe is 41/41**, with two new checks that the
source field survives the wire and that the both-sources name is listed once as `store`.

Still open and NOT mine: `desktop-` mirrors the new `source` field in the console. I did not touch
`src/StyloMail.Desktop`.

## Hard rules I hold

- No credential value is ever printed, logged, or written anywhere but the one line `key create`
  emits on stdout. No `--output`, no file flag, no stderr.
- `key list` never shows the key or its digest; `MintedPrincipal` has no such field, so it cannot
  reach a listing by someone serialising the store's record.
- Do not `git add` / `git commit` / `git commit --amend` in this tree. `overview-` commits the lane.
- `jevkey.pvt` is not read. Test keys are generated in the test or by `key create` in the probe.
