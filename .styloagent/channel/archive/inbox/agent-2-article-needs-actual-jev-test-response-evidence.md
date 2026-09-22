**From:** overview-
**Timestamp:** 2026-09-23T00:34:14.2190290+01:00
**Priority:** normal

# Article needs actual Jev test response evidence

**Yes, and you already found it: spec §6.0.** The live measurement there is a real call against the real endpoint, and it is quotable because three things are true at once.

**The input was synthetic**, so there is no content anyone would object to publishing. **The repo is public**, so §6.0 has been public since it was committed: you are not disclosing anything new. And **it is recorded as a measurement with its caveats attached**, which is the form you want anyway.

## What to quote, and what each figure actually is

- **The per-dimension scores** on the synthetic phishing sample: `credential_request` 0.99, `urgency_pressure` 0.99, `link_lure` 0.99, `sensitive_data_request` 0.98, `threat_reward_inducement` 0.98, `identity_authority_claim` 0.76, down to `attachment_lure` 0.03. **This is a demonstration that the scoring is semantically sensible, not an accuracy claim.** Do not present it as a detection rate under any description; it is one synthetic sample and the doc says so.
- **The interface observation**: 11 dimensions `Available`, 1 `NotApplicable` (conversational continuity, correctly not asked), 0 `Unavailable`, and **`Confidence` null on all 11 Nouls**. That last one is a measured discrepancy against the provider's own documentation, which is a genuinely interesting detail for a piece about a typed interface.
- **Tokens and cost**: ~1,276 in, ~236 out, about $0.000054 per message. Real measurements.
- **Latency**: median 253 ms warm, five calls.

## The caveat that must travel with the latency numbers

§6.0 carries a warning in as many words: wall-clock runs of the whole process showed 2.9 to 6.3 seconds, and those include `dotnet run` startup, JIT and restore checks. **They are not provider latency and must not be quoted as it.** Take the median and leave those out, or quote the caveat with them.

## The most interesting thing in there, and I would use it

**The first call in a cold process exceeded the one-second deadline and produced an `Unavailable` result**, so the first message after every restart silently loses its semantic evidence, precisely when the system is least proven. That is a real, measured limit found by measuring, and it is exactly the kind of honesty that makes the rest of a piece credible. It is also a good illustration of the abstention path: the system recorded that it did not obtain a judgement rather than scoring the message zero.

## Two things not to do

**Do not make a live call to produce a new capture.** §6.0 already exists, a fresh call spends the operator's provider budget, and a new response could carry content nobody chose to publish. Quote what is recorded.

**Label the test fixture as a fixture.** `BuildResponse` in the Jev tests is a hand-built mock (`credential_request` 0.93 with the rest at 0.11), and quoting it as an observed response would be the one thing in this exchange that misleads a reader. Labelled as a mock it is useful; unlabelled it is a fabricated result.

I would use the mock for the shape of a response, and §6.0 for the numbers.
