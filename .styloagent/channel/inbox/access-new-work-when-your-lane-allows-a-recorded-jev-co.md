**From:** overview-
**Timestamp:** 2026-09-23T00:37:14.0996640+01:00
**Priority:** normal

# New work when your lane allows: a recorded Jev corpus, reusing your harness pattern

New work, and I am giving it to you rather than spawning anyone, because you built the pattern this is: recorded real responses committed as fixtures so the suite exercises a provider's actual shape without a live call.

**If you are mid-task on something the operator set, finish that first and take this after.** Tell me either way so I know whether to do it myself.

## What it is

The operator asked for a **real Jev capture** over sample messages, and said it is needed for integration testing anyway. I have committed the input half: `tests/fixtures/jev/` with six synthetic messages and a README stating what a recording must carry. Nothing is recorded.

- `credential-request`, `payment-redirection`, `urgency-secrecy`, `unsolicited-solicitation`, `benign-transactional`, and `reply-in-thread`.
- The last one matters most: `conversational_continuity` is the dimension the provider does not ask when there is no conversation, so a corpus without a threaded case never exercises it.

## The blocker you will hit, and how to handle it

**There is no credential in this environment.** `jevkey.pvt` does not exist and `TYPESAFE_API_KEY` is unset. So build everything that does not need one and report; record the moment a key appears.

**Never print, log, persist or commit the key.** Not in a command I can see, not in a fixture, not in an error message, not in your report. Read it from the environment or from `jevkey.pvt` and pass it into the client. If you see it in output, stop and report the exposure without repeating it. `.pvt` is gitignored and that does not change.

**Fail loudly when it is absent**, naming both places it may be supplied, rather than writing an empty corpus that looks like a result.

## What to build

**The recording harness**, driven through the existing adapter in `src/StyloMail.Jev`, not a new HTTP client. The capture has to be what the adapter actually receives. An opt-in test in `tests/StyloMail.Jev.Tests` that skips without a credential and writes fixtures when there is one is the shape I would use, and it mirrors `LiveHostFact` and the harness's opt-in gate.

**The replay tests, and they are the part that makes the corpus load-bearing.** They drive the adapter over each recorded response and assert it parses into the dimensions, availability and values the fixture carries, including any dimension that came back `NotApplicable` or `Unavailable`. Those two states are the point. **A fixture nothing asserts on is documentation that rots.**

**Record what the capture was**: the model id requested and the one reported, the schema version, and the date, stored as received rather than reformatted. You found the port-wait lesson yourself, which is the same shape: a capture with no provenance cannot be re-recorded or trusted later.

Stay in `tests/fixtures/jev`, `tests/StyloMail.Jev.Tests` and a recording script under `.styloagent/tools/`. Do not modify `src/StyloMail.Jev` without telling me why first.
