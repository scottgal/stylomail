**From:** chat-
**Timestamp:** 2026-09-22T21:28:07.9082210+01:00
**Priority:** normal

# chat- check 3 is in and the record is fixed; the ambiguity was mine and it was a failure direction read as a disposition

Check 3 is in and the record is corrected. Short, and my context is nearly exhausted so this is close to a handover.

## The record, fixed

Your diagnosis is the right one and it is more useful than the ruling: **I wrote a failure direction as though it were a disposition.** "Safer error: escalate" describes which way the check errs when it is wrong; it says nothing about what the check does on a message. Read literally it produces a check that escalates everything carrying a link.

**The check now has its three outcomes stated**, with the asymmetry kept as the reason it has no dismiss rather than as its disposition: a lure escalates, clean links continue to check 4, and it never dismisses because the absence of a lure rules one thing out and nothing more. I put the correction in the record rather than silently replacing the sentence, because the next person reading it should see that the old phrasing was wrong.

**And your tie-breaker is in the record's reasoning**: escalating every link-bearing message would make check 4 nearly unreachable and spend the expensive path on the commonest thing in a channel.

## The check

Reads `ChatEvidenceProducer`'s output rather than recomputing the link analysis, so **triage and the assessment that follows it agree about what a lure is** by construction. Escalates on an available positive `link_display_mismatch` or `link_idn_homograph`; continues otherwise. Three tests, including both directions of the "clean links continue" claim.

**One test failure of mine worth naming**: I asserted that check 4 had *run* on a clean-links message, when check 4 does not exist and correctly reports itself as not run. The assertion was about the wrong thing, and the fix was to assert that check 3 did not settle rather than that the next check happened.

## And the kill switch

I can see from the tree that you built it and wired `ChatAssessor` to read it through its own port rather than stating it false. **That closes the gap I reported on day one**, and reading it through a port rather than through the mail-typed source is the answer I could not find.

## Verified

Build 0 warnings, 0 errors. All fourteen projects reported: **1468 passed, 0 failed**.

## Where this leaves the lane, for whoever picks it up

Checks 1, 2 and 3 are done and tested. **Check 4 (behaviour) and the drain integration with the separated write are not**, and the separated write is the one that makes triage matter: until triage runs in the drain, nothing calls it. The plan's own ordering is in the record, the counts-on-the-operator-surface work is still outstanding, and my checkpoint carries all of it.
