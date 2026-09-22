**From:** keys-
**Timestamp:** 2026-09-22T16:46:44.6524140+01:00
**Priority:** normal

# keys- report: minted-key lane landed, all five guards held, 265 Host tests green, 38/38 live probe

keys- lane complete. Nothing committed, nothing staged: the work is in the tree for you to verify and commit, per the mission.

## Files

Created (5):
- src/StyloMail.Host/Auth/HostPrincipal.cs  (resolved principal, PrincipalSource, PrincipalInventoryEntry)
- src/StyloMail.Host/Auth/MintedApiKey.cs   (value format, per-key salt, PBKDF2 digest, constant-time compare)
- src/StyloMail.Host/Auth/MintedPrincipalStore.cs (SQLite store: digests only, revocation stamps, change counter)
- src/StyloMail.Host/Cli/KeyCommands.cs     (the three verbs)
- tests/StyloMail.Host.Tests/MintedKeyTests.cs

Modified (9): Auth/PrincipalDirectory.cs (rewritten), Auth/HostAuthOptions.cs, Auth/HostAuthentication.cs,
Auth/ApiKeyAuthenticationHandler.cs, Cli/CliApplication.cs, Cli/CliCommands.cs,
Endpoints/SessionEndpoints.cs, Hosting/PrincipalSubmissionAuthenticator.cs (doc only),
Storage/HostDatabase.cs (DDL), plus docs/running.md (the CLI list and exit-code table would have gone stale).

NOT touched, deliberately: ListingEndpoints.cs and ListingResponses.cs. See the open item at the end.

## Tests

Host suite: 265 green, three consecutive runs, tree signals checked clean before each. Baseline was 213,
so 52 new. dotnet build StyloMail.slnx: 0 errors, 0 warnings.

## The live probe, /tmp/keys-work/probe_keys.py, 38/38

Real `dotnet StyloMail.Host.dll serve`, real Kestrel, real SMTP submission listener over STARTTLS,
and the same binary driven as the CLI. No credential value is printed by the script.

What came back, condensed:
- key create exits 0; the minted value is on stdout line 1; "will not be shown again" printed; stderr empty.
- key list names both principals, reports `store`/`active` and `environment`/`read-only`, and contains neither the value nor any digest.
- The database file AND its -wal file contain no copy of the minted value or of its secret half.
- HTTP: minted key to GET /v1/senders -> 200, scoped to the minted principal's tenant. Configured key -> 200. Unrecognised minted-shaped key -> 401. No key -> 401.
- SMTP: STARTTLS then AUTH PLAIN with the same minted key -> 235. Configured key -> 235. Unrecognised key -> 535.
- The approved-sender grant from the store row reaches the SMTP path: MAIL FROM the granted identity -> 250, an ungranted one -> 553 5.7.1. That is the strongest evidence both channels use the SAME resolved principal rather than merely both accepting the key.
- key revoke, no restart: HTTP -> 401 and SMTP -> 535 immediately, in a different process from the one serving. Another principal unaffected.
- key revoke on a configuration principal -> exit 3, names `StyloMail:Auth:Principals:0`, and the configured key still works afterwards.
- Precedence: after minting a key for a name the environment also configures, the configured key stops authenticating on BOTH channels (401 and 535) while the store key works, and key list shows the entry as `shadowed by store`.
- Second live key for one name -> exit 2. Unknown privilege -> exit 2.

## The guards

(a) Wholesale, never merged: an environment entry whose name the store claims resolves nothing, with any key, and it stays claimed after the minted key is revoked. Privileges are never unioned.
(b) Visible: key list reports source and status per principal, including `shadowed by store`, with a printed legend when such a row exists. Environment principals are read-only everywhere. No deprecation scheduled.
(c) key revoke refuses a configuration principal, exits 3, and names the exact entry plus why a restart is needed (the section is bound once at startup).
(d) PBKDF2-HMAC-SHA256, per-key random salt, 600,000 iterations (measured 71 ms), constant-time compare. The parameters are recorded per row so a future cost change is a re-mint, not a break. No cheaper digest of the secret is persisted anywhere.
(e) Revocation is immediate across processes: the store carries a change counter that every mint and revocation bumps in the same transaction, and the resolution cache validates against it. Statement: StyloMail:Auth:ResolutionCacheLifetime, default 30 s, a backstop for a write that somehow misses the bump; 0 disables caching.

## THREE THINGS YOU SHOULD SEE

1. A real defect I found by measuring, not reading. My first key-id encoding was base64url, whose alphabet contains the `_` that separates id from secret. ~30% of minted keys therefore carried the separator, were parsed short, and never resolved. It presented as 4 to 6 of 51 tests failing with a DIFFERENT set every run, which is exactly what a flaky host looks like. Fixed by minting hex ids; `Every_minted_value_names_its_own_row` pins the alphabet, mutation-verified red by restoring base64url.

2. A second defect found by reading after the probe was green. `key revoke` selected its row by principal id alone, and a principal accumulates one row per mint, so it found the OLDEST: after a re-mint every replacement key would report "already revoked" and be permanently un-revocable. Fixed to prefer the live row; pinned by `A_replacement_key_can_itself_be_revoked`, mutation-verified red. Neither defect was visible from the composition root.

3. A deviation from the agreed design sketch. docs/console-management-design.md writes the verbs without `--by`. I made `--by` required on create and revoke, on the rule this CLI already established for `quarantine release --by`: a mutating command with no identity to sign with must not invent one, and minting is how access is granted. Say the word and I will drop it.

Also worth your ruling: `key revoke` of an already-revoked principal exits 0 and says so. Contrast `quarantine release`, which exits non-zero when there is nothing to release. Reasoning: there, nothing to release means the message was not where the caller thought, a state mismatch; here the end state asked for already holds.

## What I could not verify

- The SMTP probe uses a self-signed certificate generated by the probe script, with client-side verification disabled. That is deliberate (loopback, throwaway cert, no CA to trust) but it means the probe does not verify the server certificate chain.
- `ResolutionCacheLifetime` as a *backstop* is not proven to matter in practice: the change counter is the working mechanism in every test and every probe check. I did not construct a mutation that writes without bumping the counter.
- A store row naming an unknown KDF algorithm, or non-positive iterations, is refused by code and by a log line, but I did not seed such a row to watch it happen.

## OPEN, and it needs a listing owner

GET /v1/senders does not list minted principals. `SenderListingResponse.From` takes `IReadOnlyList<HostPrincipalOptions>`, which is the configuration's view, and a minted principal is not a configuration entry, so ForTenant cannot honestly return one. I did not edit the listings, per the mission's lane boundary. The fix is small: project `PrincipalDirectory.Inventory()` rows with source `store` into the listing and carry the source per row so a console can show a sender is minted rather than configured. Inventory() already produces exactly that and is what `key list` renders. Flagging rather than doing, as instructed.

One consequence you may want to weigh in on: because ForTenant now excludes claimed configuration entries (required by wholesale precedence, and consistent with its existing rule that a principal which cannot authenticate is not listed), a name that is configured AND minted disappears from GET /v1/senders entirely until the listing change above lands.
