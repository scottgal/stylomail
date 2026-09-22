# `keys-` — checkpoint

Written on request, standing by. My longer living checkpoint is
`.styloagent/channel/saved-context/keys--context.md`; this file is the cold-start handover. Where the
two disagree, the living one is newer; nothing here is duplicated from it that would go stale
independently.

**Status: LANE COMPLETE and parked by `overview-` on 2026-09-22. Do not start anything new.**

## Identity + scope

`keys-` owns **StyloMail's minted-key credential path**: the Host principal store that holds key
digests, the `stylomail key create|list|revoke` CLI, and the authentication path shared by the HTTP
surface and the SMTP submission listener. All of it is in `src/StyloMail.Host`.

**Never edit:** `src/StyloMail.{Core,Persistence,Jev,Mime,Adaptive,Policy,Queue,Assessment,Transport,AccessProxy}/**`,
`.styloagent/spec.md`, `email-proxy-spec.md`, `jevkey.pvt`, and `src/StyloMail.Desktop` (`desktop-`
owns the console and mirrors the listing contract from its side).

## Shipped

Three commits, all verified independently by `overview-` in a detached clone and pushed. **Nothing
of mine is uncommitted.**

| Commit | What |
| --- | --- |
| `d135baa` | The lane: the store, the CLI, and both channels resolving through one directory |
| `b91da65` | Fleet bookkeeping; assigned the listing follow-up to me |
| `4829501` | The follow-up: `GET /v1/senders` gains minted principals and the `source` field |

Files created: `Auth/HostPrincipal.cs`, `Auth/MintedApiKey.cs`, `Auth/MintedPrincipalStore.cs`,
`Cli/KeyCommands.cs`, `tests/StyloMail.Host.Tests/MintedKeyTests.cs`.
Files edited: `Auth/{PrincipalDirectory,HostAuthOptions,HostAuthentication,ApiKeyAuthenticationHandler}.cs`,
`Cli/{CliApplication,CliCommands}.cs`, `Endpoints/{SessionEndpoints,ListingEndpoints}.cs`,
`Contracts/ListingResponses.cs`, `Hosting/PrincipalSubmissionAuthenticator.cs` (docs only),
`Storage/HostDatabase.cs` (DDL), `docs/running.md`,
`tests/StyloMail.Host.Tests/{ListingEndpointsTests,TestSupport}.cs`.

## Verified state at close

**270 Host tests green, three consecutive clean runs.** Baseline before this lane was 213, so 57 new.
`dotnet build StyloMail.slnx`: 0 errors, 0 warnings.

**Live probe `/tmp/keys-work/probe_keys.py`: 41/41**, against a real `dotnet StyloMail.Host.dll serve`,
a real Kestrel, a real SMTP submission listener over `STARTTLS`, and the same binary driven as the
CLI. Re-run it rather than trusting the number. It generates its own self-signed certificate and
disables client-side verification on purpose (loopback, throwaway cert, no CA to trust), so it does
not verify the server certificate chain.

```
export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"
dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj
python3 /tmp/keys-work/probe_keys.py
```

**Check the tree signals before believing any red or any green:**
`ls .styloagent/tools/.mutation-sweep.lock` and `find src -name '*.bak'`. A sweep rewrites `src/` in
place and `StyloMail.Host` references most of the solution, so almost any sweep can move a Host victim.

## What the credential path does, in one paragraph

A minted value is `smk_<key id>_<secret>`, id **hex** and secret base64url. The id names its own row,
so verification costs one PBKDF2-HMAC-SHA256 derivation (per-key salt, 600,000 iterations, measured
71 ms) instead of one per principal, and no cheap digest of the secret is written down anywhere.
Resolution is **store first, environment as fallback, and the store wins wholesale**: a name in both
resolves from the store only, with no union of privileges and no union of keys, and an environment
entry the store has claimed resolves nothing even after the minted key is revoked. Revocation is
immediate **across processes** because the store carries a change counter that every mint and revoke
increments in the same transaction, which the resolution cache validates against; the CLI that revokes
is not the process serving.

## The five guards, and where each is held

- **Wholesale precedence, never merged** — `PrincipalDirectory.ResolveFromStore` / `ResolveFromConfiguration`;
  `MintedPrincipalStore.ClaimedPrincipalIds` returns *any* row, live or revoked, so revocation cannot
  resurrect a forgotten configuration credential.
- **Two sources only because it is visible** — `key list` reports source and status per principal and
  prints a legend for `shadowed by store` / `revoked`. No deprecation scheduled. Environment principals
  are read-only and `key revoke` refuses them.
- **The refusal names the configuration that owns it** — `KeyCommands.RefuseAsync`, exit `3`, names
  `StyloMail:Auth:Principals:<n>` and says why a restart is needed.
- **A slow KDF, not a bare hash** — `MintedApiKey`; per-key salt, parameters recorded per row so
  raising the cost is a re-mint rather than a break; constant-time compare that answers a length
  mismatch rather than throwing.
- **Revocation is immediate** — the change counter, above. `StyloMail:Auth:ResolutionCacheLifetime`
  (default 30 s) is documented in the code as a **guard against a future edit, not a runtime
  condition**: do not weaken, skip or batch the counter on the strength of it.

## Two defects this lane found, both invisible from the composition root

1. **A key that worked about 70% of the time.** Key ids were base64url, whose alphabet contains the
   `_` separating id from secret, so ~30% of keys parsed their id short and never resolved. It
   presented as 4 to 6 of 51 tests failing with a **different set every run**, which is
   indistinguishable from a broken host. Found by **measuring**. Guard:
   `Every_minted_value_names_its_own_row` asserts the id alphabet; mutation-verified red by restoring
   base64url.
2. **`key revoke` picked the oldest row.** A principal accumulates one row per mint and the listing
   ordered by `(principal_id, created_at)`, so selecting by principal id alone found the *revoked*
   row: every replacement key would report "already revoked" and be permanently un-revocable. Found by
   **reading, after the probe was already 38/38 green and the suite was green**. Guard:
   `A_replacement_key_can_itself_be_revoked`; mutation-verified red.

The lesson to carry: a green suite and a green probe are evidence about the cases they exercise, not
about the code beside them. That is why `overview-` verifies in a detached clone rather than in the
tree the work was written in.

## Gotchas learned here

- **SMTP `AUTH` requires TLS.** The listener answers `538` unless the connection is encrypted and only
  advertises `AUTH` when it is, so an in-suite SMTP AUTH test needs a real certificate. The suite pins
  the seam through the container's own `ISubmissionAuthenticator`; the probe does the wire-level
  `STARTTLS` + `AUTH PLAIN`.
- **An unauthorised sender is `553 5.7.1`, not `550`.** My probe asserted 550 and the host was right.
  When a probe disagrees, suspect the probe.
- **`--by` is required on `key create` and `key revoke`.** The design sketch omitted it; the rule
  `quarantine release --by` established is stronger here, because minting is how access is granted.
  `overview-` ruled to keep it.
- **`key revoke` of an already-revoked principal exits 0** and says so, unlike `quarantine release`,
  because the end state the caller asked for already holds. Ruled and accepted by `overview-`.
- **`PrincipalInventoryEntry.SourceName` / `StatusName` are the one spelling** of source and status on
  every surface, so the CLI table, `key list --json` and the sender listing cannot drift.
- **The listing rule is "can it authenticate"**, not "is it configured". `SendersForTenant` returns
  both sources and only rows that can authenticate, so a name in both is listed once as a `store`
  sender rather than vanishing.

## Hard rules I hold

- **No credential value is ever printed, logged, or written anywhere** but the one line `key create`
  emits on stdout. No `--output`, no file flag, never stderr. `MintedPrincipal` has no key and no
  digest field, so neither can reach a listing by someone serialising the store's record.
- Do not run `git add` / `git commit` / `git commit --amend` / `git reset` in this tree; several
  agents share it and `overview-` commits.
- `jevkey.pvt` is not read. Test keys are generated in the test, or by `key create` in the probe.

## Parked, not open

- `desktop-` mirrors the `source` field in the console. Not mine; `overview-` is telling them.
- Nothing else is outstanding from this lane. The follow-up my precedence rule caused is closed.

If the credential path needs to move again, read `keys--context.md` for the fuller reasoning, then
re-run the suite and the probe on a verified-clean tree before changing anything.
