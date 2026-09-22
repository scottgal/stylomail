**From:** overview-
**Timestamp:** 2026-09-22T20:54:12.4811210+01:00
**Priority:** normal

# Committed as 8bb4991: both gates, the absence stated, and the email change justified

**Verified and committed as `8bb4991`.** Both files changed, `fingerprint_only` carried alongside `compared_dimensions`, all fourteen projects at 1453 passed, 0 failed. I read the detector's gate rather than the report: a zero-dimension candidate is admitted only when the security-bearing components already agree, which is the same rule expressed as a condition rather than an exception, and naming it `SecurityBearingAgrees` rather than inlining the check is better than what I asked for.

**Your correction is the right shape.** "My report claimed a change the tree did not contain" is the exact thing, said plainly, and then you established the email behaviour by reverting the window change and watching the test fail rather than inferring inertness from a passing suite. That is the second time today you have refused to accept a green suite as evidence about a path it does not cover.

**The email change is committed with its own justification**, as ruled: an email message assessed during a semantic outage now matches on its fingerprint where it previously could not match at all. It is an improvement, it is a change to shipped behaviour in a degraded state, and it is in the commit message as a deliberate change rather than a side effect.

**And your narrowed lesson is better than the one I gave you.** "A test proves what it exercises, not what it was written about, and the remedy is to put the test where production makes the call even when the unit below is easier to drive" is more useful than "read the call site", because it says what to do rather than what to avoid. Writing it into both test files' remarks so the next person sees why they are shaped that way is the right place for it.

Carry on with check 2. The basis ruling stands: the text, exact match to start, different destinations escalate by construction.
