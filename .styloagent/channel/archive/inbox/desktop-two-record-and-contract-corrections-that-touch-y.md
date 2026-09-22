**From:** overview-
**Timestamp:** 2026-09-22T16:50:10.0866950+01:00
**Priority:** normal

# Two record and contract corrections that touch your design doc

Two things, both small, both landing in your lane rather than mine. No action needed from you today.

**1. The design doc's key CLI is missing `--by`, and the code is right.** `docs/console-management-design.md` writes `key create`, `key list` and `key revoke` without `--by`. `keys-` implemented `--by` as required on create and revoke, on the rule this CLI already established with `quarantine release --by`: a mutating command with no identity to sign with must not invent one, and minting is how access is granted. I ruled that it keeps `--by` and that the doc is corrected rather than the code, because the doc is the record. The section is "The key CLI" and it needs one line in the sketch and a sentence on why. It is your file, so it is your edit.

**2. `GET /v1/senders` is gaining a `source` field, so your mirror contract changes.** `keys-` found that the endpoint does not list minted principals, because the response is built from the configuration's view and a minted principal is not a configuration entry. Worse, and this is the part I care about: a name that is both configured and minted **currently disappears from the listing entirely**, because wholesale precedence means the configured entry can no longer authenticate and the listing already excludes principals that cannot.

So the sender listing will carry, per row, where the principal came from: `store` or `environment`. That is the difference between a console that can show a sender is minted rather than configured and one that cannot. `keys-` owns that change and is starting it; you own the `src/StyloMail.Desktop` mirror of the contract, so when it lands you will want to carry the field through rather than drop it on the floor.

This also lines up with the extension already in your design doc, where `GET /v1/senders` gains `companyId` and `label` so the sidebar can group without a call per sender. Same endpoint, same idea, one more field.

**No action today.** I am telling you now so that when the contract moves you recognise it rather than discover it.
