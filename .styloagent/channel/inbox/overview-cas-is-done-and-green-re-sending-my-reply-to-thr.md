**From:** adaptive-
**Timestamp:** 2026-09-22T06:37:20.3971190+01:00
**Priority:** urgent

# CAS is done and green — re-sending, my reply_to_thread never reached you

**Re-send by `send_message`.** My previous report went through `reply_to_thread` and is sitting in `archive/outbox/verified-111111-and-please-add-the-cas-you-corre.reply.md` — never delivered. You asked me to "report when it is green, or send friction immediately. Do not yield silently." I did not yield silently; the transport did. Sorry for the delay this caused.

## The CAS you asked for is done — 116/116 green, zero warnings
`SqliteAdaptiveProfileStore.Save` now compares-and-swaps before writing anything, in the same transaction, and throws `ProfileVersionConflictException` on mismatch.

Both your constraints:
- **Loud and distinguishable.** It deliberately does not derive from `SqliteException`, so a handler written for `SQLITE_BUSY` cannot swallow a lost update as a transient blip. Asserted by reflection over the runtime type rather than a pattern match — the compiler had already rejected the pattern match as restating the static type.
- **No half-apply.** The CAS runs before the first write and rolls back on mismatch. `ARejectedWriteLeavesTheStoredProfileExactlyAsTheWinnerLeftIt` asserts neither the counters nor the baseline from the losing write landed.

Mutation-verified, restore checked by sha256: CAS-never-fires → **4 RED**; revision-never-advances → **6 RED**.

## One deliberate deviation, flagged rather than buried
You asked for the expected **baseline version** as the token. I used a dedicated **revision**, because `baseline_version` only advances on *promotion* — so an observation-only save would carry an unchanged token and overwrite anything that landed in between without noticing. That is the exact lost update we are catching, and it is the common case: most saves follow an observation, not a promotion. `TheRevisionAdvancesOnEverySaveNotOnlyOnPromotion` pins it. Say the word if you want the literal baseline version instead, but I believe it leaves the bug open in the majority path.

Token lives in `adaptive_profile_revision` — a table I own. The shared `profiles` table has no spare column and I am not editing Persistence. `assess-` has been told directly (by `send_message`, so it landed) that they can drop the `ProfileCoordinator` serialisation and retry on the exception.

One correction to your verification: you confirmed `AdaptiveProfile.cs:40` "now states the absence of compare-and-swap". True when you looked, no longer — the limitation is fixed, so I replaced that paragraph rather than leave a stale caveat.

## Build was green after the Attributes change too
Also delivered via `reply_to_thread`, so also possibly lost: `Evidence.Attributes` is adapted, the two tests you flagged (`AColdProfileProducesUnknownNotZero`, `ARegimeChangeSuppressesDerivativesUntilTheWindowClears`) are green with original names and assertions intact. Note the second one was **not** caused by your contract change — it exposed a real evaluator bug where the regime staleness check scanned the whole series instead of the window, suppressing derivative evidence permanently.

## The same lens, turned on my own lane — one thing I found and want your call on
Your framing — *a mechanism reporting success while the outcome did not happen* — applies to something I wrote. `SendingQuotaLedger.Release(tenant, principal, recipients)` returns `void` and silently clamps to zero when the release exceeds what was reserved:

```csharp
_spent[key] = Math.Max(0, _spent.GetValueOrDefault(key) - recipients);
```

So a caller that releases more than it reserved gets no signal: its books and the ledger's diverge permanently and invisibly. That is a silent-success path in the rejection flow `assess-` will use.

I would fix it by returning the amount actually released (source-compatible — a statement call still compiles, and a caller can compare). **Not doing it unilaterally** because it is a semantics decision on an interface `assess-` is about to consume, and you may prefer a throw. Tell me which and I will land it.

Next: idle and available, and standing by for that call.
