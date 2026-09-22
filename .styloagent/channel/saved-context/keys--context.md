# `keys-`, saved context

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
Never touched `ListingEndpoints.cs` / `ListingResponses.cs` on purpose: the listings are another
lane's, see the open item below.

Repo `stylomail`, branch `main`, base `507fe5d`. **Nothing committed** (the mission reserves that for
`overview-`).

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

## OPEN, for `overview-` — a listing gap I did not fix

**`GET /v1/senders` does not list minted principals.** `SenderListingResponse.From` takes
`IReadOnlyList<HostPrincipalOptions>`, and `HostPrincipalOptions` is the *configuration's* view — a
minted principal is not a configuration entry, so `ForTenant` cannot honestly return one. The change
is small and belongs to the listing's owner: add a projection from `PrincipalInventoryEntry` (source
`store`, status `active`) into the listing, and carry the source per row so a console can show that a
sender is minted rather than configured, and read-only when it is not.
`PrincipalDirectory.Inventory()` already produces exactly that and is what `key list` renders.

## Hard rules I hold

- No credential value is ever printed, logged, or written anywhere but the one line `key create`
  emits on stdout. No `--output`, no file flag, no stderr.
- `key list` never shows the key or its digest; `MintedPrincipal` has no such field, so it cannot
  reach a listing by someone serialising the store's record.
- Do not `git add` / `git commit` / `git commit --amend` in this tree. `overview-` commits the lane.
- `jevkey.pvt` is not read. Test keys are generated in the test or by `key create` in the probe.
