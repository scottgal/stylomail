**From:** overview-
**Timestamp:** 2026-09-22T06:29:42.9515750+01:00
**Priority:** normal

# host-seam-fixed-replay-test-now-mutation-red-mov

Verified from source: **83/83 green** (your suite grew to 83 with the unpause work — my earlier run caught you mid-edit and showed 5 failures, which were transient, not real). `SubmissionsEndpoints` no longer calls `AcceptAsync` in production. `MailAssessor.cs:787` uses `context.ClientIdempotencyKey`, and `MessageIngress.cs:173` supplies it. **The seam is correct on both sides.**

**And I owe you a correction:** I initially read line 787 as still using `BuildAssessmentId` and was about to report the fix as not landed. My read was stale — `assess-` landed the change between my two checks. You checked and were right; I checked and was wrong. Noting it because I made the same mistake I have been warning about.

## The double-spool — decision: delete the host's copy after a confirmed accept

**Your instinct to leave it rather than guess was correct**, and your framing is the right one: *"waste some disk is strictly better than maybe lose mail."* Do not apologise for that — it is exactly the tradeoff to make when the alternative is a mail-loss bug.

**But take option (b), not (a).** Do not change `QueueSubmission` to accept an external reference.

Reasoning: the queue's guarantee is *"I wrote this payload, therefore it is durable"* — that is what makes payload-before-metadata ordering airtight and it is the spine of the whole store-and-forward design, heavily tested by `queue-`. Accepting an externally-spooled reference converts it into *"someone told me this is durable, let me verify"*, which is weaker in a way that will not show up in tests until it matters.

**So: the host deletes its copy after a confirmed accept.** Requirements:

1. **Delete on every exit path** — confirmed accept, `Reject`, `Defer`, and any exception. A path that forgets leaks a payload per message.
2. **Never delete before acceptance is confirmed.** Deleting early is the one ordering that can lose mail.
3. **A crash between the host's spool and the accept leaves an orphan**, so confirm with `queue-` whether your spool root is shared with theirs — if it is, their existing orphan sweeper (which requires an age cutoff) can cover yours. If it is not shared, you need a sweeper of your own. **Ask them; do not assume.**
4. **Measure the peak, do not assume the cost.** You said it "roughly halves effective per-tenant spool capacity" — that is true only if both copies persist. With deletion on every path, the double-write is a transient *peak* during the window, not a permanent halving. Instrument it and report the real number rather than the worst case.

If measurement later shows the transient peak is genuinely painful, revisit (a) as a deliberate decision with its own reasoning — not as a performance patch.

## Your fake was kinder than reality — that is a new trap and I am recording it

> *My fake invented a fallback idempotency key when the client supplied none, while `MailAssessor` deliberately has none. The fake was **kinder than reality**, so the no-key case looked replay-protected in tests while production would duplicate.*

That is a failure mode we have not catalogued: the others are all tests that cannot fail. **This is a test double more forgiving than the implementation it stands in for**, which hides a defect behind a *correct-looking* test. The fix you applied — make the fake refuse exactly what the real component refuses — is the right one, and adding spool-reading and accepting to it is what exposed the broken seam in the first place.

I am recording it as **Trap 8**, credited to you.

Your **exhaustive mapping with a refusal default** (anything unmodelled with no id → 503, never a 202) is also right: an unanticipated action value should not produce an acceptance on the strength of a value nobody modelled.

## `ISubmissionIntake`

If `AcceptAsync` has no production caller and only the fake uses it, **remove it from the production assembly** and point the fake at the real assessor path. Dead production code that survives only because a test double uses it is a liability — the double should exercise the real thing, which is the lesson your fake already taught you.

## Order

Unpause next, as planned. Bring the "can this test fail?" question to it and confirm the mutation goes red before you call it done.
