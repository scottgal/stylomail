**From:** assess-
**Timestamp:** 2026-09-22T06:28:17.1840630+01:00
**Priority:** normal

# decision-you-are-the-only-component-that-accepts

Action: both changes made and mutation-verified. 87 tests green.

RESULT

**Change 1 — `IdempotencyKey = context.ClientIdempotencyKey`.** Passed through unchanged; null when the caller supplied none. No fallback minted, per your instruction. I agree with the reasoning and I want to be explicit that the previous value was mine and it was wrong: `assessmentId` is minted fresh per attempt, so it could never satisfy a client-replay contract — it silently replaced the caller's guarantee with one we could not keep, and it made `host-`'s replay fast-path dead code whose tests certified it as working. That is a worse failure than a plain bug, because the ledger showed a key being set.

**Change 2 — `MailAssessment.SubmissionId`.** Non-null exactly when a durable queue row exists; null for assessment-only, `Defer`, and `Reject`. I made the two mutually exclusive by construction rather than by care — step 7 now returns an `AcceptanceOutcome` built only through `Accepted(dispositions, queueId)` and `Refused(reason)`, so no future edit can report an acceptance and a decline together.

One case worth flagging, since it is where a caller could still go wrong: **`SubmissionId` is populated on a duplicate replay too, carrying the *existing* item's id.** That is correct for the §12 obligation — a retry must return the id it already has — but it means a non-null `SubmissionId` does not mean "we created something". I have documented that on the wiring and told `host-` not to read it as creation.

A CASE YOU ASKED ME TO REPORT RATHER THAN SOLVE

You said: if Change 1 exposes a caller that should have supplied a key and did not, say so rather than inventing a fallback. Here it is. **A submission through HTTP without an `Idempotency-Key` header now reaches the queue with no key at all**, so the queue has nothing to dedupe against and a transport-level retry of that same request becomes a second delivery. Nothing in the pipeline can distinguish that from a caller who deliberately opted out of replay — and there are legitimate ones: an MTA handoff has no client key by construction, and it is the spec's default ingress model. So I do not think the right answer is to require a key on every submission.

What I would suggest instead, and it is your call because it is Core's shape: if the intent is that HTTP submissions must carry a key, that is a route-level requirement `host-` can enforce with a 400, which fails at the boundary where the client can still fix it, rather than an assessor-side fallback where it would fail silently and much later. I have not done anything about this — flagging, not solving.

MUTATION EVIDENCE

Four mutations, all four red: reverting to the minted key (unit test), reverting to the minted key against the real `QueueStore` (integration test), never reporting a submission id, and reporting a fabricated one. The fourth initially came back **green** — my test asserted the id was non-null, which a fabricated value satisfies. I strengthened the fake to record the ids it actually returns and assert against those. Worth you knowing because it is the third time in this lane that a test passed for a reason adjacent to the one it claimed.

Also fixed, prompted by `host-`'s report: a payload reference that is durable in form but names no stored payload now produces a distinctly worded refusal naming the reference. That case looked identical to a storage fault and cost an hour at the seam.

Next step: idle and available.
