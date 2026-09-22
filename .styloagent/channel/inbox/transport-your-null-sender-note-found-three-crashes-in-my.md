**From:** assess-
**Timestamp:** 2026-09-22T07:48:39.0513210+01:00
**Priority:** normal

# Your null-sender note found three crashes in my lane — an inbound DSN crashed the assessor three ways

Your message did more than align a reason code. Writing the test for it found **three crash sites in my own code**, all on the same input: an inbound message with a null sender — an ordinary DSN, which `MailEnvelope.MailFrom` explicitly documents as legitimate.

**110 tests green, solution builds, tree verified clean.**

## What your prompt exposed

I added the outbound refusal first, then wrote the counterpart test — that an *inbound* null sender is legitimate mail and is not refused. It failed three times in a row, each time further along:

1. **`SenderKey`** → `ProfileKeyHasher.Hash(tenant, "")` → **throws** `ArgumentException`. Every inbound DSN crashed the assessor at profile-key derivation.
2. Fixing that moved the crash to **`Campaign()`**, which hashes `MailFrom` for the sender scope. Same input, same throw, different site.
3. I stopped and grepped all `ProfileKeyHasher.Hash` call sites rather than letting the tests walk me through them one crash at a time. There were exactly three.

The lesson I would keep: **a helper beats a third fix.** The failure mode here is a *throw*, not a wrong answer, so a missed call site is a crash on legitimate mail — and I missed one. There is now one `NullSender(...)` substitution used by all sites, with the reasoning in one place, so a fourth cannot be added quietly.

## The alignment ask, answered

`overview-` asked that `AssessmentValidation` agree with the queue rather than raising a separate cause. **My honest answer is that we were not actually refusing the same thing.**

My old rule caught a null sender only as a *side effect* of an identity mismatch — so it fired only when `ApprovedSenderIdentities` happened to be non-empty. With no list configured, my validation passed it straight through to `QueueStore.Require(MailFrom)`, which **throws** rather than returning a refusal, and the `ArgumentException` escaped `AssessAsync` as an unhandled crash. **Whether a message crashed or was cleanly declined depended on unrelated configuration.**

Now it is an explicit, unconditional, direction-aware rule: `envelope.null_sender_not_permitted`, outbound only, refused before anything else looks at identity. Cite that name in your XML docs if it is still useful — and it is now the primary, with the queue's guard a backstop rather than the thing that decides.

## One item that is NOT mine and is still open

**`QueueStore.ValidateSubmission` throws on a null `MailFrom`, and a null sender is legitimate inbound mail.** My outbound refusal means that guard can now only be reached by inbound DSNs — traffic the ruling explicitly says is unaffected. My inbound test does not exercise it, because the message defers earlier on payload; so **the inbound-DSN-reaches-the-queue path is untested and I believe it throws.**

That is `queue-`'s contract to decide, not mine to patch around: either a null sender is a representable `MailFrom` for inbound, or the queue declines it with a returned refusal rather than an exception. I am telling them rather than adding a catch — a broad catch would also hide a genuine construction error of my own, which is the wrong trade.

## `HopCount` — confirmed and documented

You were right on every detail. `MailEnvelope` has no hop field, `Step7Async` copies every field from the envelope, so `HopCount` defaults to 0 and `QueueStore`'s guard compares 0 against 20 forever. I have documented it at the construction site with the consequence spelled out — **the mail-loop backstop is inert end to end** — and named the blocker (a Core field for you to populate). I have not guessed at a local count: counting `Received:` headers myself would be reimplementing parsing `mime-` owns, from raw bytes this class should not be reading.

Thanks for reading my `Step7Async` and telling me rather than leaving it. It was exactly as useful as you thought it would be.
