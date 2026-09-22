**From:** transport-
**Timestamp:** 2026-09-22T07:46:43.7944240+01:00
**Priority:** urgent

# QueueStore is queue-'s file, not mine — and the fix needs QueueSubmission.HopCount to become int? too

`transport-`. The Core field is right and I'll confirm my side is unaffected — but **I'm not going to make that edit, for two reasons, one of which is that the ask as written is incomplete.**

## 1. It's queue-'s file

`QueueStore.cs` is `queue-`'s. My brief is explicit — *"Do not modify any other project. Other agents own them. If a contract must change, `send_message` the owner and copy `overview-`"* — and you've reinforced that boundary twice for `host-`/`ingress-`. The same boundary applies to Queue, so I'd be applying it inconsistently by taking this one. **If you want me to have it, say so explicitly and I'll do it** — but I'd rather ask than assume, because "he was told not to touch other lanes and did anyway, with a message that looked like permission" is not a thing I want to be true.

## 2. It can't be done in `QueueStore` alone

`QueueSubmission.HopCount` is **`int`, non-nullable** (`QueueContracts.cs:130`). So the queue cannot currently *represent* "not observed" — asking the check to treat null as unenforced requires the field to become `int?` first, or `Step7Async` has to map `int?` → `int` and **the ambiguity you just warned about is reintroduced at exactly the handover you're protecting.**

That's the same defect one layer down: `0` meaning both "observed, no prior hops" and "never looked", with `MaxHops` reading as enforced while never firing.

There's also a storage question only `queue-` can answer: `QueueItem.HopCount` is `required int` (`:301`). Once a message is *accepted* without observation, does the row store `0` or does it become nullable too? An accepted message's hop count is knowable-but-unobserved, and that is a persistence decision, not a check.

## 3. My lane has no equivalent problem, and I checked rather than assuming

My ingress does have a `MaxHops` check (`SmtpIngressSession`, against `SmtpIngressOptions.MaxHops`), but it compares `facts.ReceivedCount` from **my own header scan** — always observed, never a default. So there's no null case for me to represent, and nothing analogous to change. I also confirmed **I construct no `MailEnvelope` anywhere**, so adding the Core field cannot break my lane.

**181 green, solution builds.**

I've sent `queue-` the requirement with your reasoning and this analysis, so the exchange is one hop rather than routing back through you. If you'd rather I take the edit, or want it split differently, tell me and I'll do whichever you say.
