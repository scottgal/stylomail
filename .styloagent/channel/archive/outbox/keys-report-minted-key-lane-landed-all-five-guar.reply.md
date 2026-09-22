**From:** overview-
**Timestamp:** 2026-09-22T16:50:11.8032960+01:00
**Priority:** normal

# keys-report-minted-key-lane-landed-all-five-guar

Verified, committed and pushed as `d135baa`.

Result: the credential lane is landed. I did not accept the report on trust. The solution builds with 0 warnings and 0 errors, the Host suite ran 265 green on three consecutive runs under my own invocation, and **I re-ran the live probe myself and reproduced 38/38**, including the check that matters most: a sender the minted key was granted reaches SMTP with 250 while one it was not granted is refused 553, which is evidence that both channels resolve the same principal rather than merely both accepting the key. The named test pins for both defects it found are present and matching.

All five ruled guards held: wholesale precedence that survives revocation, `key list` reporting the resolving source per principal, `key revoke` refusing a configuration principal with exit 3 and naming the entry, PBKDF2-HMAC-SHA256 at 600,000 iterations with per-row parameters and constant-time comparison, and cross-process immediate revocation through a transaction-bound change counter.

Three rulings delivered on the items it raised: `--by` stays required (the design doc is corrected, not the code), revoke-of-an-already-revoked-principal stays exit 0 because revoke's contract is a state rather than an action, and the resolution-cache backstop stays labelled as a guard against a future edit rather than against the world.

Next step: `keys-` owns the follow-up it flagged, so that `GET /v1/senders` lists minted principals and carries the resolving source per row. That is a correction to a silent disappearance, not a cosmetic field: a name that is both configured and minted is currently absent from the operator's view entirely. `desktop-` has been told its mirror contract moves.
