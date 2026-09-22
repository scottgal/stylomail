**From:** queue-
**Timestamp:** 2026-09-22T07:53:28.3269930+01:00
**Priority:** normal

# HopCount nullable — done both, and I agree with your storage call

Both done. **What you asked, and your storage view, which I agree with and did not merely accept.**

## 1. `QueueSubmission.HopCount` is `int?`

The check is now null-aware, and `null` means **unenforced, not passing**:

```csharp
if (submission.HopCount is { } hops && hops >= _options.MaxHops) { refuse }
// null → the limit cannot be checked. Proceeds, with the absence preserved in the row.
```

Accepting rather than refusing on an unobserved value is the only workable answer — refusing would reject every message until the whole ingress→assess chain populates the field — but it is a deliberate choice, not an oversight, and the property says so. The row is the report.

## 2. `QueueItem.HopCount` is `int?` and `hop_count` is now nullable

**I agree with your reasoning rather than accepting it under protest**, and the sharper version is: an accepted message's hop count is *knowable-but-unobserved*, and `0` at rest would be the row **claiming we looked and found no prior hops**. That's not a missing value, it's a false one. Nullable stores the absence; zero would manufacture an observation.

`EnforceHopLimit` is unaffected and I've documented why: `NULL >= $maxHops` is never true, so an unobserved count cannot trip a limit. That's correct behaviour and now says so in the SQL.

## The one thing that nearly shipped

`ReadItem` still declared `int hopCount` and called `reader.GetInt32(11)`. **That compiles fine** — `int` → `int?` is implicit — and would have thrown at runtime the first time a row had a NULL. Only the nullable *read* path exposes it, so I've added tests that exercise it end to end rather than just the check.

Worth flagging as a class: making a field nullable is a **three-place** change (contract, bind, read), and the compiler only catches two of them.

## Schema is now v4

`hop_count` changed from `NOT NULL DEFAULT 0` to nullable, so an existing v3 database would reject NULL inserts. The version guard throws on mismatch rather than failing later inside an accept — so **any dev database needs deleting**, including `host-`'s if they have one. I've not touched their lane; flagging so it isn't a surprise.

## Verified

**Queue 95 green** (was 93), sweep 24/24 CLAIMED with anchors surviving the change, and — since Core moved — **Host 128, Transport 186, Assessment 113, solution builds.**

Remaining chain is outside my lane: `ingress-` populating the envelope, then `assess-` copying it in `Step7Async`. Until both land, `MaxHops` still reads null on the submission path — but now that is *visible as unenforced* rather than disguised as a zero, which was the whole defect.

Scenario 4 next, once `transport-` is ready.
