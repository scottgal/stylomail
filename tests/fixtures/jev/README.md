# The recorded Jev corpus

Live responses from the semantic provider, recorded over a fixed set of sample messages and committed
so integration tests exercise the provider's **real response shape** without a live call.

Nothing here is recorded yet. The messages are the input half, and they are ready.

## Every message here is synthetic

Written for this corpus. None of them is a real message, and none is derived from one. The phishing
cases are deliberately plausible, which is the point, and that is also why this file says so: a
committed sample that looks like real mail should state that it is not.

## The cases, and why each exists

| Message | What it asks the classifier to distinguish |
| --- | --- |
| `credential-request.eml` | A credential request dressed as an IT notice, with a link. The classic inbound case. |
| `payment-redirection.eml` | New bank details and a request to update records. Payment redirection without urgency, so the two are separable. |
| `urgency-secrecy.eml` | Pressure and a request for confidentiality from a known name, with no payment or credential ask. |
| `unsolicited-solicitation.eml` | Commercial solicitation with a link: legitimate noise rather than an attack. |
| `benign-transactional.eml` | An ordinary shipping notice with a link, which should read as ordinary. |
| `reply-in-thread.eml` | A reply carrying quoted history and `References`, so conversational continuity is **asked** rather than reported `NotApplicable`. |

The last one matters more than it looks: `conversational_continuity` is the dimension the provider
does not ask when there is no conversation, so a corpus without a threaded case never exercises it.

## What a recording must carry

A response with no provenance cannot be re-recorded or trusted later. Each recording states:

- The **model id requested and the one reported**, because an alias that silently resolves elsewhere
  is the discrepancy this project already found once.
- The **question schema version**, so a change to the questions is visible in the corpus.
- The date, and the fact that it came from a live call rather than a fixture.

Store the response **as received**, not reformatted. A normalised capture discards exactly the
differences a replay test exists to catch.

## Recording

Needs a credential, supplied as `TYPESAFE_API_KEY` in the environment or as `jevkey.pvt` at the
repository root. **No credential is present in this environment at the time of writing**, so nothing
has been recorded.

**The key is never printed, logged, written into a fixture, or committed.** `.pvt` files are
gitignored and that must not be relaxed. If a recording tool cannot find a credential it fails loudly
naming both places, rather than writing an empty corpus that looks like a result.

The response is recorded through the adapter in `src/StyloMail.Jev`, not a new HTTP client, so the
capture is what the adapter actually receives.

## How the fixtures are used

The replay tests drive the adapter over each recorded response and assert it parses into the
dimensions, availability and values the fixture carries, including any dimension that came back
`NotApplicable` or `Unavailable`. Those two states are the point: a corpus that only carries confident
answers tests the easy path and none of the interesting ones.

**A fixture nothing asserts on is documentation that rots.** If a recording is added without a test
that fails when the parsing changes, it is not part of the suite.
