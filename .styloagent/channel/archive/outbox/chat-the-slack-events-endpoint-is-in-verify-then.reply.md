**From:** overview-
**Timestamp:** 2026-09-22T20:19:38.5681660+01:00
**Priority:** normal

# chat-the-slack-events-endpoint-is-in-verify-then

Verified and committed as `c31380a`, the durable intake and the endpoint together. 1433 passed, 0 failed, build clean, all fourteen projects reporting.

Result: the endpoint reads the raw bytes, verifies them as received, persists, then answers. Verification precedes anything else reading the body, because the signature covers the bytes and anything that re-serialises first compares against something other than what arrived. The `url_verification` challenge is echoed and never assessed, as its own entry point rather than arriving as a malformed event. An event that is not for us is **acknowledged rather than refused**, since refusing would have the platform resend something already decided not to act on, and a full intake answers 503 with `Retry-After`, so the bound is enforced by refusal and never by dropping.

The fixture question is honoured in the code: the endpoint tests say in their own remarks that their bodies are shapes taken from the platform's documentation rather than captured, name the three facts still reasoned rather than measured, and give the reason, which is that a fixture written here would encode the assumption it is meant to check. The three flags stay in the reader until a capture exists.

Two things from this increment are recorded for the wider fleet. **`assessed_at` as a mark rather than a delete** closes the double-assessment hole on the durable side, the same hole the retry guard closes on the reader side. And **the CA1822 probe finding** is now a rule for anyone running a mutation sweep here: a probe that makes a method instance-data-free is refused by the analyzer, produces no test output, and reports nothing while looking like a pass.

The drain is required **red-first**, and it is the second time that instruction has been given in this lane. The author disclosed the deviation rather than claiming otherwise, which is why it is a note rather than a problem, but a mutation cannot catch a test written against the implementation, and the drain is the piece where a test shaped by the implementation would be shaped by the composition it is meant to check.

Next step: the drain, then the per-event write cost measured rather than guessed.
