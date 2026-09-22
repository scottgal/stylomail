**From:** chat-
**Timestamp:** 2026-09-22T20:18:31.6132270+01:00
**Priority:** normal

# chat- the Slack events endpoint is in: verify-then-persist-then-answer, with the loop guard pinned

The endpoint is in and frozen. Eight tests, and the hand-off order is the ruling rather than my earlier proposal.

## The order the request takes

**Read the raw bytes, verify them as received, then persist, then answer.** Verification happens before anything else reads the body, because the signature covers the bytes and anything that re-serialises first compares against something other than what arrived. An unsigned request and a body swapped underneath a valid signature are both refused with nothing stored.

**The challenge handshake is echoed and never assessed.** It carries no author, no channel and no text, so I put it in the reader as its own entry point rather than letting it come back as "not a plain message", which would describe the endpoint's own handshake as a malformed event.

**An event that is not for us is acknowledged, not refused.** Our own app's post, an edit, a reaction: refusing would have the platform resend something we have already decided not to act on, and a retry is traffic we would see again on every attempt.

**And the durable admission happens before the answer**, which is your ruling rather than my earlier bounded-buffer proposal. A full intake returns 503 with a `Retry-After` so the platform retries, so the bound is enforced by refusal and never by dropping.

## Verified, with the standard caveat about how

**I wrote the implementation before the tests again and did not see them red, so I substituted mutation checks rather than claiming red-first.** Breaking `IsOurOwnPost` fails exactly `A_message_the_deployment_posted_itself_is_acknowledged_and_not_assessed`, out of eight. That is the loop this endpoint exists to prevent, and it is now pinned rather than commented.

**A mistake in the probe worth recording:** my first two mutations made the method instance-data-free and the build refused them with **CA1822**, "can be marked as static". So the mutation produced no test output at all and reported nothing, which is the failure mode where a probe looks like a pass. Analyzers-as-errors means a mutation probe has to still touch instance data, and I only caught it because the absence of output was itself suspicious.

## The fixture question, honoured

`SlackEventsEndpointTests` says in its own remarks that its bodies are **shapes taken from the platform's documentation rather than captured from a workspace**, names the three facts that are still reasoned rather than measured, and says why a fixture cannot settle them: it would encode the assumption it is meant to check. The three flags stay in the reader until a capture exists.

## Measured

Build 0 warnings, 0 errors. Whole solution **1433 passed, 0 failed, 23 skipped** (eight new). Sweep signals clean, no em-dashes in anything added.

## What remains in Task 4

**The drain**: a hosted service taking waiting events, reading them, assessing through `IChatAssessor`, recording to the ledger, marking complete and pruning. That is what turns a stored event into an assessed one, and it is what makes the intake more than a very careful way of losing messages. After it, the per-event write cost you asked me to bring numbers for if it is disproportionate, measured rather than guessed.
