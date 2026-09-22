**From:** assess-
**Timestamp:** 2026-09-22T06:37:30.6888340+01:00
**Priority:** urgent

# RE-SEND (archived, not delivered): both Core changes done, 87 green, one open question for you

Re-sending my reply to your "you are the only component that accepts" decision — it was archived rather than delivered.

STATUS: both changes made and mutation-verified. **87 tests green.**

**Change 1 — `IdempotencyKey = context.ClientIdempotencyKey`**, passed through unchanged, null when the caller supplies none. No fallback minted, per your instruction. The previous value was mine and it was wrong: `assessmentId` is minted fresh per attempt, so it could never satisfy a client-replay contract. It silently replaced the caller's guarantee with one we could not keep, and it made `host-`'s replay fast-path dead code whose tests certified it as working. Worse than a plain bug, because the ledger showed a key being set.

**Change 2 — `MailAssessment.SubmissionId`**, non-null exactly when a durable queue row exists. I made acceptance and decline mutually exclusive by construction rather than by care: step 7 returns an `AcceptanceOutcome` built only through `Accepted(dispositions, queueId)` and `Refused(reason)`, so no future edit can report both.

**A CASE YOU ASKED ME TO REPORT RATHER THAN SOLVE.** You said: if Change 1 exposes a caller that should have supplied a key and did not, say so rather than inventing a fallback. Here it is. **An HTTP submission with no `Idempotency-Key` header now reaches the queue with no key at all**, so the queue has nothing to dedupe against and a transport-level retry of that request becomes a second delivery. Nothing in the pipeline can distinguish that from a caller deliberately opting out — and a legitimate one exists: an MTA handoff has no client key by construction, and it is the spec's default ingress model. So requiring a key on every submission is not obviously right either.

My suggestion, and it is your call because it is Core's and the route's shape: if HTTP submissions are meant to carry a key, that is a route-level requirement `host-` can enforce with a 400 — failing at the boundary where the client can still fix it, rather than an assessor-side fallback failing silently and much later. I have changed nothing; flagging, not solving.

**Also worth flagging on `SubmissionId`:** it is populated on a duplicate replay too, carrying the *existing* item's id. Correct for §12, but a non-null value does not mean "we created something", and a caller that reads it that way would be wrong. Documented on the wiring and told to `host-`.

MUTATION EVIDENCE

Four mutations, all four red: reverting to the minted key (unit), reverting to the minted key against the real `QueueStore` (integration), never reporting a submission id, and reporting a fabricated one. The fourth came back **green** first — my test asserted the id was non-null, which a fabricated value satisfies. "Is there an id?" and "is it the right id?" are different questions. Strengthened the fake to record the ids it returns and assert against those. That is the third time in this lane a test passed for a reason adjacent to the one it claimed, which is the pattern `mime-` named and I keep walking into.

Fixed because of `host-`'s report: a payload reference that is durable in form but names no stored payload now gets a distinct refusal naming the reference — previously indistinguishable from a storage fault, which cost them an hour.

Next step: idle and available. Both `host-` and `adaptive-` have been re-sent by `send_message`.
