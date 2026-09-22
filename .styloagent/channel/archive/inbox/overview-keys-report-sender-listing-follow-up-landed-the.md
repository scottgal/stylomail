**From:** keys-
**Timestamp:** 2026-09-22T16:55:20.6696610+01:00
**Priority:** normal

# keys- report: sender listing follow-up landed, the pinned case mutation-verified, 270 green, 41/41 probe

Listing follow-up done. Nothing staged or committed; in the tree for you to verify and commit.

## What changed

- `PrincipalDirectory.ForTenant` is gone, replaced by `SendersForTenant(tenantId)` returning `IReadOnlyList<PrincipalInventoryEntry>`: both sources, tenant-filtered, **only rows that can authenticate**, ordered by principal id. "Who counts as a sender" now lives beside the precedence rule that decides it rather than being re-derived by the route.
- `SenderResponse.Source` on every row of `GET /v1/senders`: `store` or `environment`. `SenderListingResponse.From` now takes inventory entries, so the projection has no key and no digest to leak in the first place.
- `PrincipalInventoryEntry` gained `SourceName`, `StatusName` and `CanAuthenticate` as the one spelling of those values. The CLI table, `key list --json` and the listing all read them now. Side effect worth flagging: `key list --json` status went from `readonly` to `read-only`, matching the table. Three spellings of one value is how a console learns a word the CLI never says.
- No dead code left: `ForTenant` had exactly one caller and it is now the new method.

## The case you asked me to pin

`A_sender_that_is_both_configured_and_minted_appears_once_as_a_store_sender` asserts all three things, not just presence: the name is **present**, exactly **once**, sourced from the **store**. It also asserts the configuration key really is dead (401), so "listed" cannot be standing in for "the old route still resolves it".

**Mutation-verified.** Making `SendersForTenant` skip store rows reproduces the original defect exactly, and turns **four** tests red, including that one. Fix restored, checked by grep.

Sibling cases covered: a minted-only sender is listed as `store`; a configured one as `environment`; a revoked minted sender is **not** listed, asserted both ways (present before the revoke, absent after) so it cannot pass against a listing that never had it. That test also pauses the sender first and asserts it stays listed, because dropping a paused sender would make a pause look like a deletion.

## Verification

**270 Host tests green, three consecutive runs** (265 before, 5 new), tree signals checked clean before the loop, solution builds 0 errors and 0 warnings.

**Live probe is 41/41** on a real host, with three new checks beyond the 38: the listing carries a minted principal with `source=store`, carries a configured one with `source=environment`, and a name that is both is listed exactly once as `store`. So the pinned case is proven over the wire as well as in the suite. Sample row from the probe:

`{"principalId": "env-reviewer", "source": "store", "control": {"paused": false, ...}, "label": null, "companyId": null}`

## Two documents

`docs/console-management-design.md` you had already corrected with the `source` row. **`docs/running.md` had gone stale and I corrected it**: it claimed `GET /v1/senders` "is built from the configuration that holds every principal's API key", which this change made false, and its route line now mentions that each row carries its source.

## On your ruling 3

The resolution-cache doc now says the counter is the mechanism and the lifetime is a guard against a future edit rather than a runtime condition, and says in as many words not to weaken, skip or batch the counter on the strength of it. No mutation built for it.

## Not mine, and untouched

`desktop-` mirrors the new `source` field. I did not touch `src/StyloMail.Desktop` or its tests, confirmed by `git status`.

One consequence you may want to pass to `desktop-` as well: because the listing now includes store rows, a minted principal appears as a sender even though `PUT /v1/senders/{id}/settings` and the pause control are the only things the console can do with it. The `source` field is what lets the console say that it cannot revoke it from there.
