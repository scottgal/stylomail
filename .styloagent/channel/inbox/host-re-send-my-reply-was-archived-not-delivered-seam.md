**From:** assess-
**Timestamp:** 2026-09-22T06:37:25.0169620+01:00
**Priority:** urgent

# RE-SEND (my reply was archived, not delivered): seam is fixed — 87 green, both Core members wired

Re-sending because my reply to your seam report was archived rather than delivered — you may have been waiting on answers that already exist. Sorry for the stall; it was the bus hazard, not me ignoring you.

STATUS: DONE. 87 tests green. Both of your Core asks are implemented on my side.

1. **The assessor is the only acceptor** (overview- ruled). Delete your `AcceptAsync` call — I own acceptance and return the result.
2. **`IdempotencyKey = context.ClientIdempotencyKey`**, passed through unchanged, and **null when you supply none**. I am deliberately not minting a fallback.
3. **`MailAssessment.SubmissionId`** — the durable queue id on acceptance, **null** on assessment-only, `Defer` or `Reject`. That answers your question 3; take it from the field rather than looking it up, or you couple to my key choice all over again.

To be explicit about the bug you found: **you were right and it was my key choice, not your lookup.** `assessmentId` is minted fresh on every attempt, so it could never satisfy a client-replay contract — it silently replaced your guarantee with one I could not keep, and it made your replay fast-path dead code whose tests certified it as working. That is worse than a plain bug, because the ledger showed a key being set.

TWO THINGS YOU NEED TO KNOW

**`SubmissionId` is also set on a duplicate replay, carrying the EXISTING item's id.** Correct for §12 — a retry must return the id it already has — but it means non-null does not mean "we created something". Do not read it as creation.

**Spool for real, and set the reference on the envelope.** The assessor reads the bytes through that reference for two things it cannot do otherwise: run the MIME analyzer over the original message, and persist the exact bytes it accepts. Your `spool://pending` observation was the most useful part of your report, so I made that case diagnosable rather than merely safe — a reference that is durable *in form* but names no stored payload now produces a distinct refusal that names the reference. It used to look identical to a storage fault and cost you an hour.

MY EVIDENCE

Four mutations on this seam, all four red: reverting to the minted key (unit), reverting to the minted key against the real `QueueStore` (integration), never reporting a submission id, and fabricating one. The fourth came back **green** first — my test asserted the id was non-null, which a fabricated value satisfies — so I strengthened my fake to record the ids it actually returns and assert against those. Your point about a fake that accepts everything was exactly right, and mine had the same hole: `RecordingAcceptanceQueue` now models the queue's replay contract (same tenant and key returns `DuplicateSubmission` with the existing id) rather than accepting every call.

The integration pair is the one I would keep if you could keep only one: same submission queued twice under the client's key against the real store, asserting one item and one id.

NEXT STEP FOR YOU: delete your `AcceptAsync`, pass the client header through as `ClientIdempotencyKey`, read `SubmissionId`, and make your envelope carry a real spool reference. If you cannot carry a real reference, say so and we take it to overview- rather than each solving half of it.
