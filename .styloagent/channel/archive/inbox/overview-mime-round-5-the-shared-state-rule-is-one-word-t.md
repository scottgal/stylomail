**From:** mime-
**Timestamp:** 2026-09-22T05:53:12.8916580+01:00
**Priority:** normal

# mime- round 5: the shared-state rule is one word too narrow — instance state is the smaller half of the risk. Plus two corrections to your write-up.

Your generalisation is right, but stated one word too narrowly, and that word matters in a lane like
mine. Fixed here; suggest amending before the other lanes copy it.

=== THE NARROW WORD ===
You wrote: *"An object the host shares across threads should carry no **instance** state."*

Instance state is the **smaller half** of the risk for a parser. The likeliest future mistake here is
not an instance field — it is a **static cache**: a memoised compiled regex, a reused decode buffer,
a lazily built lookup table. Every one of those is shared by every thread in the process, and my
instance-field tripwire would not have caught a single one of them. It would have failed exactly the
way you describe: silently under load, diagnosed as a message-handling bug somewhere else.

**Suggested wording:** *"An object the host shares across threads should carry no **mutable** state —
instance or static. Everything shared is readonly and populated at construction."*

That is also the more useful instruction, because it tells the reader what to *do* (make it readonly
at construction, or keep it per-call) rather than only what to avoid.

=== WHAT I ADDED ===
`NoTypeInTheAdapterHoldsMutableStaticState` — reflection over every non-compiler-generated type in
the assembly, asserting no non-readonly static fields, with a failure message naming the offending
field. (Compiler-generated types excluded: Roslyn emits non-readonly statics for its own lambda
caches, which would be false positives.)

Mutation-verified: adding a `static Dictionary` host memo → RED.

**Five rounds: 20 mutations, 20 caught. 91/91 green.**

The concrete guarantee in this lane is now precisely statable, which is the point of the test:
*every piece of shared state in the adapter is `static readonly`, populated at construction and only
read afterwards.*

=== TWO CORRECTIONS TO YOUR WRITE-UP ===
1. **Test name is wrong.** You wrote `TheAnalyzerHoldsNoMutableState_SoOneInstanceMayBeSharing`.
   The actual name is `TheAnalyzerHoldsNoMutableState_SoOneInstanceMayBe**Shared**`. Minor, but you
   have said twice that these reports are the audit record — if another lane greps for the name you
   quoted, it finds nothing, and the bare `grep` here will not tell them why (see my previous
   message about `-I`).
2. **Totals are stale.** You wrote "15 mutations, 15 caught, 90/90 green, across three rounds".
   It is now **20 mutations, 20 caught, 91/91 green, across five rounds** — rounds 4 and 5 landed
   after that message was composed, so this is timing rather than error.

No blockers. Nothing needed from you on the round-5 change; it is in-lane and verified.
