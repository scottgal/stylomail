# Security policy

StyloMail is a security component. It holds credentials, it sits in front of mail stores, and a
defect in it can lose mail or leak the contents of a mailbox. Please treat anything below as a
serious report rather than a bug report.

## Reporting a vulnerability

**Do not open a public issue for a security defect.** Report it privately to the maintainer listed
against this repository on GitHub.

Please include:

- what an attacker gains, and what they must already have to try it
- the smallest reproduction you can manage
- which component, and whether the defect is in code, in a contract, or in a documented guarantee
  that the code does not actually provide

**Never include a live credential in a report.** Describe where it lives
(`env:TYPESAFE_API_KEY`, `keychain://item`, a vault path) and never the value. If you have already
sent one, say so without repeating it — that is the useful part and the rest can wait.

## What is treated as a vulnerability

- Any path by which one tenant can observe, address or influence another tenant's data.
- Any path by which message content, a credential or a keyed-hash input reaches a log, an exception
  message, a decision record, or a metric.
- Any means by which a message is delivered twice, accepted without being durably stored, or
  silently dropped.
- Any case where the system reports success while the outcome did not happen — including a guarantee
  that is documented but not enforced.
- Any bound (size, rate, hop, concurrency) that reads as enforced and is not.

Reports of the last two kinds are especially welcome. **A mechanism that reports success while the
outcome did not occur is the failure class this project has spent the most effort eliminating**, and
one is known to remain (below).

## Known limitations, disclosed deliberately

These are not vulnerabilities to report, but they bound what this software can claim. They are
recorded here rather than left for a reader to discover.

| Limitation | Consequence |
| --- | --- |
| **Not production-hardened.** No hardening review, no crash-recovery testing at volume, no live load profile. | Do not deploy against real mail without that work. |
| **The AccessProxy credential store is in-memory.** | A restart loses credentials; every session then fails closed. A durable store is required before real use. |
| **Per-tenant key isolation is unmet** for the credential store. | Key compromise has a wider blast radius than intended. |
| **One mechanism is known-unverified by tests.** `SqliteAdaptiveProfileStore.ApplyObservation`'s `BEGIN IMMEDIATE` is not discriminated by any test; SQLite's refusal to upgrade a stale snapshot is what protects the deferred form. | Documented in code as *"the tests will not tell you."* A refactor around it will not be caught. |
| **Thresholds are unvalidated defaults**, not measured values. They require representative replay data. | Tuning-quality, not correctness — but they are not evidence of tuning. |
| **Cloud content leaves the box** when the hosted classifier is enabled. | An operator decision per tenant, not a default. Tenants that prohibit it use the local evidence path with an explicit *semantic-unavailable* state. |

## Handling of secrets in this repository

- Deployment secrets come **only from environment variables**. No credential is read from a file in
  this tree, and none is written to one.
- `STYLOMAIL_PROFILE_KEY` rotation is a **migration**: every stored profile key becomes unreadable.
  Rotating it requires a re-key, not a restart.
- Credential-shaped types (`SecretValue`) make redaction a **type property** — no string conversion,
  and `ToString()` returns a redaction marker — so accidental interpolation cannot leak a value.
- If a secret is ever committed: **rotate it.** Removing the file, or adding an ignore rule, does not
  un-leak it — the history is the exposure.

## Scope

In scope: everything in this repository.

Out of scope: the upstream MTA's own policies; DNS, TLS provisioning and reputation infrastructure;
and the hosted classifier vendor's data-handling terms, which are an operator decision recorded in
`.styloagent/spec.md`.
