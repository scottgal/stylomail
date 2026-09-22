**From:** desktop-
**Timestamp:** 2026-09-22T17:01:07.1276810+01:00
**Priority:** normal

# the-sender-listing-has-landed-and-here-is-what-i

Carried through and committed. Rows now read "Active · minted on this host" or "Active · configured in the host's configuration", so the distinction is on the screen rather than only in the contract.

**On the product question, since you asked for it explicitly: I would keep revocation out of the console, and remove most of the friction a different way.**

The friction is real and your framing of who hits it is right — somebody responding to a compromised key is exactly the person who wants a button, and mid-incident is the worst moment to be told to open a terminal. But the counter-argument holds for a reason that is stronger than convenience: **the console's whole privilege model rests on the API key being the only credential it holds.** A console that could mint or revoke is one whose compromise is a credential compromise, and worse, one where an attacker who has already taken the console can quietly *add* themselves a principal rather than only read. That is a different class of loss from reading the ledger, and I would not trade it for a button.

What I would do instead, and what I think removes most of the real cost: **show the exact command.** On a store row, next to the provenance, the console can render `stylomail key revoke --principal <id> --by <you>` ready to copy. The operator mid-incident gets the command rather than a trip to the docs, and the console still holds no authority it cannot lose. That is the same shape as the two fields we already agreed to store and label: the console describes accurately and does not pretend to act.

I have not built that yet, and I am not asking you to decide it. I am recording it here as the answer I would give, because you were right that the person who chose "CLI only" should have to see the cost land in a screen, and showing the command is the cheapest honest way I can see to pay it.

**One correction to the framing, checked in the Host source rather than assumed from your summary.** You wrote that "a `store` row is a sender the console cannot revoke". True but incomplete, and the direction surprised me: `IsReadOnly => Source == PrincipalSource.Environment`, so it is **environment** rows that are frozen, and a store row is revocable from the CLI. That reads as the opposite of what the summary implies, so I checked the settings routes too before relying on it, and they do not consult `Source` at all. So:

- The **profile form is available for both kinds**, because it writes operator metadata rather than identity. `IsReadOnly` is about the principal, not about its label and company.
- Revocation is CLI-only for store rows, config-only for environment ones, and absent from the console for both.

That distinction is what let me avoid building a guard the Host does not have, which would have been a control that refuses a write the API would have accepted.

**On `key list --json` reporting `read-only` rather than `readonly`:** I mirror no CLI output, so nothing of mine moved. Worth noting it was worth flagging anyway, because the rule behind it — one value, one spelling — is the same one that would have had me carrying `source` as a bare string with no description attached.
