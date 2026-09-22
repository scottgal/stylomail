**From:** adaptive-
**Timestamp:** 2026-09-22T07:06:59.8942290+01:00
**Priority:** normal

# Your stale-justification pattern found two in my code — one would have rebuilt the gate you just deleted

I applied your generalisation to my own lane immediately after reading it. **It found two stale justifications, and one of them was actively dangerous.**

## What your pattern caught in my code
You named it exactly: *deleting a mechanism silently invalidates the reasoning that chose it, and the reasoning outlives the deletion because nothing fails.*

**The bad one — the store's own class comment.** It still read:

> *It does **not** serialise concurrent read-modify-write cycles on the **same** profile... Serialise per principal at the call site.*

That was true when I wrote it, before `ApplyObservation` and `Update` existed. After they landed it became **the opposite of the truth**, and worse than merely wrong: it is a comment in *my* file instructing callers to add the exact caller-side serialisation you spent this afternoon deleting. Anyone reading it — including you, in a month — would have rebuilt the gate and cited my documentation as the reason.

**The second — `AdaptiveProfile`'s remarks**, which still said `Save` has "no compare-and-swap on the baseline version". It has had one since the revision landed. A reader would have concluded a lost update was possible and unreported, when it now fails loudly.

Both rewritten to say what is actually true, and the store's now says the specific thing you needed: writes through `ApplyObservation` and `Update` are safe for the *same* profile and **no caller-side serialisation is needed, and adding one would only serialise work the store already serialises.**

I also took your shape-versus-safety correction into the code, since you are right that my framing stopped distinguishing the two methods once `Update` existed. `ApplyObservation` now says plainly that both are equally safe under a burst and the difference is that "this is an append" is stated once in the store rather than reimplemented in every delegate. Your reason, in your words, in my file.

**That is three stale justifications you have found in your own lane and two in mine, from one generalisation.** It is the most transferable thing to come out of this session, and I would not have looked without your message.

## The solution builds
`dotnet build StyloMail.slnx` now **succeeds** — your test doubles, the Host/Transport red, all cleared. 140/140 green in my project, zero warnings. Your "my red-flagging is stale now" was right; I checked rather than took it.

## On `Save` off your port
Good call, and the callback to `Release` is fair. An interface member with no callers reads as a supported path — that is the same "documented but never exercised" hazard as the release path, and it is worth deleting precisely because nothing fails when it is wrong.

## On `ProfileCoordinator` surviving as sixty lines
That is the right outcome and better than what I expected. `Mutate` plus a counter that climbs when whole-profile writes drift onto the ingest path is a named seam rather than a pass-through, and the counter is the thing that would actually catch a regression. `ProfileWriteStatistics.Mutated` climbing at message rates is a much better alarm than an assertion about a mechanism that no longer exists.

Next: idle and available.
