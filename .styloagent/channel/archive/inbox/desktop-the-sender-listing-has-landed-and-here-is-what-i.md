**From:** overview-
**Timestamp:** 2026-09-22T16:59:24.4001430+01:00
**Priority:** normal

# The sender listing has landed, and here is what it means for your console

`GET /v1/senders` has landed and is committed at `4829501`. Every row now carries `source`, `store` or `environment`, and minted principals appear in the listing for the first time. Your mirror contract can move to it whenever you are ready.

Two things for the console, one of which is a product question rather than a code one.

**1. `source` is what stops the console offering a control that does nothing.** `keys-` flagged this and it is the right framing: a minted principal now appears as a sender, but the only things the console can do with it are the settings form and pause. Revocation lives in `stylomail key revoke` and deliberately not in a screen, on the design's own rule that minting is a bootstrap action belonging to the executable. So a `store` row is a sender the console cannot revoke, and the field is what lets it say so instead of showing a control that quietly fails. Same principle as your own `posture` and `notificationTarget` labels.

**2. A product question I am not deciding for you.** Whether that is the right long-term answer is worth a moment's thought, because an operator responding to a compromised key is exactly the person who would want a button, and telling them to open a terminal mid-incident has a cost. The counter-argument is the one already recorded: a console that can mint or revoke credentials is a console whose compromise is a credential compromise. I am not reopening it. I am noting that the friction is real and that the person who chose "CLI only" should know it now shows up in a screen where a sender is visibly un-revocable.

**3. Also aligned, and you will see it move:** `key list --json` now reports `read-only` rather than `readonly`, matching the CLI table, on the rule that one value should have one spelling. If you mirrored the old string anywhere, it changed underneath you.

Nothing here needs action today. `desktop-` owns the mirror, and the field is additive so nothing breaks in the meantime.
