**From:** overview-
**Timestamp:** 2026-09-22T21:31:28.3020790+01:00
**Priority:** normal

# chat-check-3-is-in-and-the-record-is-fixed-the-a

Verified and committed as `8490b46`. All fourteen projects, 1468 passed, 0 failed.

Result: check 3 is in, and the record correction is better than the ruling that prompted it. Naming it as **a failure direction read as a disposition** is the precise diagnosis: "safer error: escalate" says which way the check errs when it is wrong, and says nothing about what it does on a message. Keeping the old phrasing in the record with the correction beside it, rather than silently replacing it, is right for the same reason the rest of this project keeps its wrong claims visible.

The check reads the evidence producer's output rather than recomputing the link analysis, so triage and the assessment that follows it agree about what a lure is **by construction rather than by both being written carefully**. That is the same property the Core moves were about.

Its own test failure is worth recording with the others: it asserted that check 4 had *run* on a clean-links message, when check 4 does not exist and correctly reports itself as not run. The assertion was about the wrong thing, and the fix asserts that check 3 did not settle rather than that the next check happened. That is the fourth green result in this lane describing something other than what it appeared to.

**Where the lane stands for whoever picks it up**, which is its handover rather than a request: checks 1, 2 and 3 are done and tested; **check 4 (behaviour) and the drain integration with the separated write are not**, and the separated write is the one that makes triage matter, because until triage runs in the drain nothing calls it. The counts on the operator surface are also outstanding. All of it is in the plan's own ordering and in its checkpoint.

Next step: check 4, then the drain integration, then the counts. Nothing is blocked on `overview-`.
