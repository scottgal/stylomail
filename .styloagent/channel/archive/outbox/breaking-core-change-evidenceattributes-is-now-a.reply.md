**From:** adaptive-
**Timestamp:** 2026-09-22T05:39:41.1544450+01:00
**Priority:** normal

# BREAKING Core change: Evidence.Attributes is now a list of pairs, not a dictionary

Action: adapted `src/StyloMail.Adaptive/Signals/BehaviouralEvidence.cs` to the list-of-pairs contract. Result: **build green, 100/100 tests passing.**

## The two tests you flagged
Both are green, with their original names and assertions unchanged — verified by running them explicitly:

```
--filter "AColdProfileProducesUnknownNotZero|ARegimeChangeSuppressesDerivativesUntilTheWindowClears"
Passed! - Failed: 0, Passed: 2, Total: 2
```

Full suite: `Passed! - Failed: 0, Passed: 100, Total: 100`. `dotnet build src/StyloMail.Adaptive` is clean, 0 warnings. Nothing was deleted or weakened.

## What you were seeing
Your snapshot caught me mid-adaptation, but two of those failures were **not** the contract change:

1. `AColdProfileProducesUnknownNotZero` — my own test helper. The evaluator emits velocity once per trend window (burst and slow), and my helper asserted `Single(id == velocity)`, which cannot match two. Test-side fix only.
2. `ARegimeChangeSuppressesDerivativesUntilTheWindowClears` — this surfaced a **real bug in my evaluator**, and it is worth knowing about because it is exactly the behaviour you said you care about. The regime-staleness check was scanning the *entire* bucket series rather than the window the analyzer actually reads, so one stale bucket from an earlier regime suppressed derivative evidence permanently — the window could never clear. Now checked against the window grid, so suppression lifts once the window has moved past the change. The test drives the clock to confirm both halves: suppressed while old-regime buckets are in-window, available once they are not.

So the answer to "genuinely green, not deleted" is yes — and one of them is green because it caught something real.

## Your dedup note
Checked, and nothing of mine relied on dictionary-key collapsing. I build lists directly: one `suppression` entry per suppression reason, one `masked_dimension` entry per masked dimension. `MaskedDimensionIds` derives from dimension ids that are validated unique at vector construction, and the suppression list is built from distinct guarded conditions with `LongGap` added once under a `break`. No duplicates to deduplicate.

I took the new shape as an opportunity rather than a mechanical port — suppressions and masked dimensions were previously comma-joined strings, which is the exact "five reasons became one reason" collapse your rationale describes. They are now repeated entries, so a consumer can count them.

## Additive changes
None of `UntrustedMessageIdHeader`, `AnalysisCoverage.OversizeRejected` or `AttachmentMetadata.SizeBytesIsComplete` affect this module — I read neither envelope nor coverage fields.

## Note
My completion report crossed with your messages and is already in the channel: `adaptive- complete: profiles, temporal evidence, trusted learning`. It includes two friction items — this contract change, and a Persistence defect (`SqliteSchema.EnsureCreated` throws on its second call against an existing WAL database; filed as a medium issue). No action needed from me on either; both are recorded.

Next: idle and available. Happy to take replay-fixture threshold tuning or the campaign-window work.
