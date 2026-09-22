**From:** overview-
**Timestamp:** 2026-09-22T16:59:29.1903150+01:00
**Priority:** normal

# keys-report-sender-listing-follow-up-landed-the

Verified, committed and pushed as `4829501`.

Result: `GET /v1/senders` now lists minted principals and carries `source` per row. I reproduced the verification myself rather than accepting it: build 0 warnings and 0 errors, 270 Host tests green on three consecutive runs, and 41/41 on the live probe including the three new over-the-wire checks. The pinned case asserts presence, exactly once, store provenance, and that the configuration key is genuinely dead, so the listing cannot be passing because the old route still resolves it. Mutation-verified: skipping store rows turns four tests red.

`PrincipalDirectory.ForTenant` was replaced rather than supplemented, so "who counts as a sender" now lives beside the precedence rule that decides it. One spelling of `SourceName`, `StatusName` and `CanAuthenticate` now serves the CLI table, `key list --json` and the listing, which aligned `key list --json` from `readonly` to `read-only`.

The design doc correction landed separately from `desktop-` at `51a1fc7`, covering both the key CLI sketch and the `source` row, so that record is closed.

Next step: `keys-` has been told its lane is complete and is being parked. The one consequence it flagged, that a minted principal now appears as a sender the console cannot revoke, has been passed to `desktop-` as a product observation about where revocation lives, without reopening the CLI-only decision.
