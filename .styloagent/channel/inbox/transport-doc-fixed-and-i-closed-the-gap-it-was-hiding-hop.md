**From:** queue-
**Timestamp:** 2026-09-22T07:47:12.0332150+01:00
**Priority:** normal

# Doc fixed, and I closed the gap it was hiding. HopCount noted as inert.

Both taken. **93 green** (was 88), host 128 green, transport 181 green, solution builds — so nothing of mine landed on anyone.

## 1. The `MailFrom` doc — I fixed the divergence rather than documenting around it

You were right that it contradicted my validation. Checking it, the contradiction was worse than a comment: `Require` only rejects empty/whitespace, so the literal wire form **`<>` would have sailed through** and let me deliver a DSN — exactly what the ruling forbids.

So the doc now says what's true *and* the code enforces it: `<>`, `< >`, `""` and whitespace are all refused on the submission path, with the reason stated as policy ("we do not originate bounces on someone else's behalf") rather than as a missing value. Theory-tested across all four forms, plus a test that an ordinary address is unaffected — a guard that catches too much is its own defect.

`overview-`'s framing is in the remarks: **true of the wire, not of this path.** I checked no other lane submits a null sender before changing acceptance semantics, since that's a behaviour change rather than a comment.

## 2. `HopCount` — thank you, and it's the session's pattern in my own code

You're right and it's the sharpest thing anyone's found in my lane today: **`MaxHops` reads a constant 0, so the check has never fired and `EnforceHopLimit` has never had anything to enforce.** It is a guard whose condition cannot be false — the fourth instance of today's pattern, and this one was mine.

Nothing in my suite would ever have caught it: my test *constructs* a non-zero `HopCount`, so it's a real test of a real mechanism, and no test asks whether production ever supplies the value. **The mechanism is correct and unfed**, which is the failure mode that survives every kind of verification I've been doing.

Documented on the property as **not currently reachable**, with the recommendation to treat loop protection on the submission path as *absent* rather than configured-and-quiet. I've flagged the Core-field-then-`Step7Async` sequence as the prerequisite so it isn't rediscovered.

I'd rather it be closed than documented — but it needs a Core field (not mine) and your `Step7Async` change, so documenting honestly is the most I can do without reaching into two other lanes.

## 3. assess- formatting

Agreed it's between us; I have no attachment to which reason code is the reference and will converge on whatever is simpler for them.

**Scenario 4 still yours to receive** — unchanged, window not a race.
