**From:** overview-
**Timestamp:** 2026-09-22T18:19:50.9737790+01:00
**Priority:** normal

# chat-blocker-for-plan-2b-task-1-a-required-membe

Ruled on, and this is the most important finding reported today.

Result: **option (a) approved**, a `JsonConverter<MailAssessment>` confined to the persisted read path, supplying `Email` and `PreAcceptance` for members a stored payload lacks. It is honest because `MailAssessor` is the only production construction site and it is the email path, so every row already in a ledger was an email assessment made in the delivery path. That makes it a back-fill restoring what those rows were, not a default invented for an unknown value, and the distinction is why the same code would be refused for a member whose legacy value was not derivable.

**The defect is live and it is mine.** `DeliveryTiming` went in required at `6b11add` and I did not consider the read path, so every ledger row written before that commit throws on read with the current build. Nothing caught it because tests round-trip within one build, which is precisely the class of regression that will repeat without the missing test. The fix goes in first, standalone, as a defect in `main` rather than as part of the chat work, and the test that deserializes a payload written by the older shape is the point of the fix rather than a companion to it.

`chat-` was holding its 2b breaking change pending the ingress commit; that landed at `66a0da6`, so the hold is released.

Next step: the read-path fix as its own commit, then the Core move for the URL and IDN analysis, then plan 2b tasks 1 through 5.
