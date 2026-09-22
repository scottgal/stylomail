**From:** overview-
**Timestamp:** 2026-09-22T20:26:44.2134520+01:00
**Priority:** normal

# chat-you-were-right-to-insist-a-requirement-deri

Verified and committed as `9c04d71`. All fourteen projects, 1441 passed, 0 failed.

Result: insisting on red-first found a real defect, and the way it was found is the part to keep. The drain retried a failing event **in a tight loop**, re-taking the same batch whenever there was work, so a persistently failing event would have spun as fast as the CPU allows for the whole duration of a storage outage or a rejected credential, hammering the assessor and the database. A pass that completes nothing now waits the same idle interval an empty intake uses.

The author could not do the ordering literally, because the drain already existed, and correctly cited the earlier ruling that a red run after the fact is theatre rather than staging one and calling it red-first. What it did instead is the substance of the method separated from its ceremony: **tests written from the requirement text rather than from the code, and run before assuming they would pass.** One of the two went red for exactly the right reason, and the claim in the drain's own remarks was the thing that disagreed with the implementation.

The finding inside the finding is that its mutation checks had all passed, because they only ask whether the tests notice changes the author chose to make. A test derived from what the code claims asks a different question, and it is now carried forward as the standard for Task 5 rather than filed as a lesson.

Also landed in the same commit: the registration bug that gated the chat registrations on configuration at composition-root time, which `HostServices` documents as the trap where a test host's configuration has not been layered in yet. The feature was registered nowhere and nothing failed, because the endpoint tests never needed it. And the drain's durable semantics: a failure leaves the event waiting where an operator can see it, a payload that no longer reads as a message is cleared, and the inbound tenant is configured rather than taken from the payload, so a caller cannot choose whose ledger their message lands in.

Next step: Task 5, the completeness pass, with its tests and the write measurement derived from the requirement text as well. The emergency kill switch remains open and is `overview-`'s.
