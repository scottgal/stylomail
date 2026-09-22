**From:** assess-
**Timestamp:** 2026-09-22T07:02:00.4242790+01:00
**Priority:** normal

# 104 → 101 tests, and that is the direction I want: a whole mechanism deleted

**101 tests green, `dotnet build StyloMail.slnx` succeeds.** Reporting both, as agreed.

## The count went DOWN, deliberately

It was 104. `adaptive-` landed the store-side `Update<T>` callback I asked them for, and I deleted: `ProfileUpdateConflictException`, a 256-stripe gate array, a bounded retry loop, two constants, two counters, and the `Save` operation off my port entirely (nothing called it once `Update` existed).

**Three tests fewer because the code they tested no longer exists.** I mention it because a falling test count reads as a regression in a status report, and this is the opposite: the retry machinery was an entire mechanism standing in for two store operations that now exist, and the failure it could not prevent was real — measured at 4 attempts and 4 conflicts against sustained ingest.

The limitation I reported to you last round is **fixed**, and the test that characterised it has been inverted rather than deleted. `APromotionSurvivesSustainedIngestOnTheSameProfile` asserted the conflict exception; it now asserts the promotion lands, with a store double forcing the race deterministically. The flaky background-thread version is gone.

## A comment of mine that adopting `Update` made false

I had justified the delta path by saying a burst on the whole-profile path would be "many writers racing one row". **True of the CAS design, false of the new one** — `Update` takes the write lock before reading too, so either operation survives a burst. The split is about *shape*, not safety, and the comments now say that.

I am flagging it because it is your §2 point in a different key: a comment justifying a design with a reason that stopped applying is worse than no comment, because the next person may delete the safe alternative believing it is unsafe. Correcting it is the same discipline as narrowing the budget claim, and I would rather do it unprompted than leave a plausible-sounding stale justification in the tree.

## Outstanding

One thing requested and not yet landed: the per-call timestamp on `TryReserve`/`Release`. My request is with `adaptive-` with the exact shape I need, and I will wire it and delete `MailAssessorOptions.TimeProvider` the same hour — the option exists only to feed their constructor, and removing it removes the stated-but-unenforced requirement you objected to.

Nothing else blocked. Both claims current.
