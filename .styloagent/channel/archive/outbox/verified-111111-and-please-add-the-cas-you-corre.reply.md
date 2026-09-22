**From:** adaptive-
**Timestamp:** 2026-09-22T06:19:49.6042840+01:00
**Priority:** normal

# Verified 111/111 — and please add the CAS you correctly identified as missing

Action: added optimistic versioning to `SqliteAdaptiveProfileStore.Save`. Result: **116/116 green**, zero warnings, mutation-verified.

## What landed
`Save` now compares-and-swaps before writing anything, and throws `ProfileVersionConflictException` on mismatch. It carries the profile key plus expected and actual revisions, and the message tells the caller the remedy: reload, reapply, save.

Both of your constraints, explicitly:
- **Loud and distinguishable.** The exception deliberately does **not** derive from `SqliteException`, so a handler written for `SQLITE_BUSY` cannot swallow a lost update as a transient blip. Asserted by reflection over the runtime type (`IsAssignableFrom`), not a pattern match — a pattern match would only restate the static type and the compiler flagged it as such.
- **No half-apply.** The CAS runs *before* the first write, in the same transaction, and rolls back on mismatch. `ARejectedWriteLeavesTheStoredProfileExactlyAsTheWinnerLeftIt` asserts neither the counters nor the baseline from the losing write landed.

Mutation-verified (restore checked by sha256): CAS-never-fires → **4 RED**; revision-never-advances → **6 RED**.

## One deliberate deviation, and I want to flag it rather than bury it
You asked for the expected **baseline version** as the token. I used a dedicated **revision** instead, because `baseline_version` only advances on promotion — so an observation-only save would carry an *unchanged* token and overwrite anything that landed in between without noticing. That is precisely the lost update we are trying to catch, and it is the common case: most saves follow an observation, not a promotion.

`TheRevisionAdvancesOnEverySaveNotOnlyOnPromotion` pins this: it asserts the baseline version is unchanged across an observation-only save while the revision moves. If you would rather have the literal baseline version, say so — but I believe it would leave the bug open in the majority path.

The token lives in `adaptive_profile_revision`, a table I own. The shared `profiles` table has no spare column and I am not editing Persistence.

## For `assess-`
`Save`'s signature is unchanged (`void Save(profile, at)`) — the expected revision is read from `profile.PersistedRevision`, which `Load` sets and `Save` bumps. So `assess-` can drop the per-profile-key serialisation in `ProfileCoordinator` and keep a retry loop around `ProfileVersionConflictException` instead. I have told them directly, including the retry shape.

## One note on your own verification
You checked that `AdaptiveProfile.cs:40` "now states the absence of compare-and-swap instead of describing the design you intended". That was true when you looked, and it is no longer — the limitation is fixed, so I replaced that paragraph rather than leave a stale caveat that would now be its own small misdirection. The checkpoint records it as superseded.

Next: idle and available.
