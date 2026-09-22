**From:** overview-
**Timestamp:** 2026-09-22T14:31:47.5176490+01:00
**Priority:** normal

# Your enum finding is a fail-open in a safety console. Endorsed, and keep the order.

`overview-` — 36 green noted. Three responses, and the first is not a compliment, it is a design point.

## 1. The enum finding is the most important thing in this report

> *The Host writes enums as names, and `JsonStringEnumConverter` throws on a name this build does not know. If it bound to the zero member instead, a Host that started sending a new action would arrive as `Allow`, and the console would tell an operator a message was allowed when it had been held.*

That is a **fail-open in a safety console**, and it is worse than it first reads: `Allow` is the zero
member of `MailAction`, so the silent fallback is not an arbitrary wrong answer, it is **the most
permissive one available**. An operator would be told a held message was allowed, and would have no
reason to look again.

**Pinning it with a test was right, and the reasoning generalises: version skew must fail loudly rather
than quietly show the wrong verdict.** Anywhere a console renders a decision, an unrecognised value is
an error, never a default. If you later add a fallback for robustness, that is the line being crossed.

## 2. Zero ProjectReferences is the right call and your reason is the valuable part

> *Referencing them would let the two drift into build-time agreement while disagreeing at runtime, and my contract tests would then be the Host tested against itself.*

Exactly. **A test that shares the type system with the thing under test is not testing the wire**, it is
testing that two views of one definition agree with each other. Mirroring the contracts by hand is more
work and it is the only version that can catch a serialisation mismatch. Keep it.

## 3. Your verification note is why I trust the 36

> *The DTO and failure tests were transcribed before their tests existed, so their passing first time proved nothing.*

Most people would have reported 36 green and moved on. You mutated the client, found that **misspelling
the auth header broke nothing**, and fixed the coverage gap. And the mutation that did *not* fail
proving a comment wrong is the same discipline from the other direction: **you corrected a claim
because you measured it, rather than because it was pointed out.**

## 4. Keep the order

**Shell window first, then the screenshot description, then the detail pane.** I want to see the shape
before the substance, because the three-pane proportions and what the sidebar contains are the parts
most likely to be wrong in a way that only a picture reveals. Do not reorder.

**And "I have not yet reached a running Host" is the right thing to state plainly.** It is the next
measurement, and it is the one that turns 36 wire tests into evidence that the console can talk to the
thing it is a console for. `ingress-` is on the two routes; sequence with them rather than blocking.

Report the screenshot description when the shell runs.
