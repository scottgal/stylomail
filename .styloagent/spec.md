# StyloMail, Spec

Status: draft for operator sign-off · 2026-09-22
**Specification of record for implementation detail:** `email-proxy-spec.md` (repo root, draft 0.1,
22 September 2026). This document is the project's identity and shape; that one is the build-ready
detail. Where they disagree, this document states the intent and that one governs the mechanics.

---

## 1. Purpose

StyloMail is a small, explainable inbound/outbound **email security component**. It detects suspicious
*communication* rather than suspicious words: compromised outbound accounts, inbound phishing,
impersonation, emerging campaigns, and unusual changes in otherwise legitimate communication, while
measuring and reducing false positives.

Two layers do this:

- **Jev** converts unstructured message content into *typed semantic evidence*, bounded,
  independently-scored properties (credential request, payment redirection, urgency, link lure …),
  not a single "is spam" verdict.
- **A local adaptive system** combines that evidence with message metadata, communication
  relationships, and behaviour over time.

Jev is a decision layer, not a parser, evidence generator, or renderer. The caller owns every possible
answer; Jev cannot return arbitrary strings and has a real abstention path.

Terminology is deliberate: **"post-Bayesian"** means moving beyond token-frequency evidence, not
rejecting Bayesian methods. **"Post-LLM"** means structured classification rather than conversational
generation, it is *not* a claim that errors or adversarial inputs disappear.

## 2. Users

| User | What they get |
| --- | --- |
| **Operator / tenant admin** | Configures routes, traffic classes, quotas, retention, and the cloud-content decision. Consumes health and metrics. |
| **Authenticated sending principal** | Outbound mail evaluated under their identity; can be paused (`/v1/controls/senders/{id}/pause`). |
| **Reviewer** | Reads the explainable decision ledger; releases quarantine under audit. Separately privileged from senders. |
| **Recipient** | Receives scoped trust preference (e.g. "wanted promotion") that changes *their* preference, not global truth. |
| **Integrating system (MTA / application connector)** | Calls the library API or HTTP host to assess, or submits for durable delivery. |

Multi-tenant throughout: every profile, cache key, quota, and idempotency key is tenant-scoped, and
one recipient's relationship history or address is never exposed to another recipient.

## 3. Core capabilities

**Weekend demonstrator (the scope that must work):**

1. Evaluate raw MIME through a NuGet-style library API and a CLI.
2. Accept inbound/outbound assessment *and* submission through authenticated HTTP endpoints.
3. Integrate **one** existing mail boundary, a trusted MTA handoff *or* a restricted SMTP submission
   listener relaying to one configured upstream. **Do not build a public MX service from scratch.**
4. Extract deterministic features plus ~12 semantic dimensions.
5. Maintain sender, recipient, and relationship profiles; fast/slow trends; acceleration evidence.
6. Reuse exact semantic assessments; detect bounded near-duplicate campaigns.
7. Support allow, bounded hold, quarantine, and pre-acceptance reject/defer.
8. Provide trusted feedback, an explainable decision ledger, shadow mode, and deterministic replay tests.

**Deliberately out of scope:** full public-MX operation, general-purpose queue/provider adapters,
attachment sandboxing, OCR, distributed clustering, HA, rich dashboards, broad provider compatibility,
mailbox hosting, IMAP client, marketing platform, autonomous incident-response agent. Existing mail
infrastructure should keep owning internet-facing protocol complexity where possible.

## 4. Key constraints

**Architectural principles (non-negotiable):**

- Probabilistic components provide **evidence**; deterministic policy authorizes **side effects**.
- Evaluate against the **pre-event trusted baseline**, never normalize a message with its own evidence first.
- **Cache assessments, not permissions.** Every message receives current contextual and policy checks.
  Never memoise "allow this sender."
- Preserve original MIME bytes for transport and signature integrity; normalized content is an *analysis view*.
- **Unknown is a distinct state**, not a zero score, not evidence of innocence.
- A familiar sender is context, not a permanent bypass. Normal automation is not inherently abusive.
- Bounded queues, memory, cardinality, and provider spend are core features, not tuning.

**Delivery shape:** transport-independent .NET library + ASP.NET Core HTTP host + standalone executable,
with a thin SMTP integration. Local SQLite persistence; no mandatory distributed services. Cloud Jev is
optional/configured behind a provider interface that can later take a compatible local classifier.
**A local Jev release is not assumed.**

**MVP safety limits:** do not fetch links, load remote images, execute attachments, or resolve redirects.
Any later fetching requires a separately sandboxed, SSRF-resistant service.

**Byte preservation, precise scope (clarified 2026-09-22).** "Preserve original MIME bytes" exists
for *signature integrity*, not byte-identity for its own sake. Concretely: the body and every existing
header are preserved byte-for-byte and in order, and **exactly one `Received:` line is prepended** by
our hop. Prepending does not break DKIM, a signature covers only the headers named in its `h=` tag,
and `Received` is not among them, whereas rewriting, refolding or reordering signed headers does.
`transport-` asked whether to add it and correctly declined to decide alone. **Decision: add it.**
The decisive reason is not convention but the loop guard: we check incoming `by` clauses for one
naming us, and since we previously never wrote one, **our own hop was invisible to that check**, leaving
loops to the hop-limit backstop alone. RFC 5321 also requires a relay to add a `Received` line. The
transport test asserts "prepend exactly one Received line; everything else byte-for-byte" so the
property cannot widen unnoticed.

**The `Received` line carries no `for <recipient>` clause, deliberate, keep it that way.** A
`Received` header is stored, forwarded to *every* recipient, and commonly archived by third parties,
so naming one recipient in it discloses that recipient to all the others: **a `Bcc` leak into the
message itself**, created by a diagnostic convenience. The clause is optional in the grammar, so
omitting it costs nothing operationally. Found by `transport-`; asserted by a test that a
multi-recipient message's `Received` line contains no recipient address, because this is exactly the
kind of property a later "make the header more conventional" change would quietly undo.

**Trust rules:** DKIM/SPF success is not a benign verdict, compromised authorized accounts authenticate
correctly. Client-supplied `From` headers cannot establish identity. Accept `Authentication-Results` only
from configured trusted boundary verifiers. Foreign-language content, uncommon names, and new contacts
are not suspicious by themselves. Thread headers are claims, not proof.

**One spool, and one set of coupled bounds, both wiring requirements, not conventions.**

1. **Exactly one `SpoolStore` instance exists.** The transport has no spool by design
   (`transport-` verified this and corrected an earlier claim of its own), so the acceptance sink
   must write through **the same instance** the composition root gives `AssessmentPipeline.Create`.
   A second spool root would make the delete-after-accept path, the queue's orphan sweeper and the
   assessor's read-back reason about different directories, each correct in isolation, together
   broken, and silent.
2. **`SmtpIngressOptions.MaxMessageBytes <= QueueOptions.MaxPayloadBytes` must hold, and is asserted
   at construction.** They are deliberately equal today (64 MB). If they drift, the ingress accepts a
   message, the sink spools it, and the queue refuses it, so the caller sees a **capacity deferral
   that looks like spool pressure** when the cause is a size-policy mismatch two components away. The
   tighter bound must win, and a reader of either component alone cannot see the coupling, which is
   why it is asserted rather than documented.

**Deployment secrets, decided 2026-09-22.** Both come from **environment variables**; no files, no
config keys, no secret store.

| Secret | Variable |
| --- | --- |
| Jev / TypeSafe API key | `TYPESAFE_API_KEY`, already `JevOptions.ApiKeyEnvironmentVariable` |
| Profile keyed-hash master key | `STYLOMAIL_PROFILE_KEY`, a keyed-hash secret, 32+ bytes of high entropy |

The `ProfileKeyHasher` requirement was an unrecorded hole in this spec until `host-` refused to invent
a source for it, correctly, because *"unlike a schema guess it fails open, by silently not
assessing."* Rules:

1. **One deployment-level master key, with the tenant id mixed into the hashed input**, not one key
   per tenant. This gives the property that matters (the same address hashes differently in two
   tenants, so a profile key from one tenant is meaningless in another) without per-tenant key
   management. Changing the construction is a **migration**, not a config change: every stored
   profile key becomes unreadable.
2. **Startup fails loudly if either secret is absent or the profile key is too short.** A missing key
   that silently degrades to a constant or empty string would work perfectly in tests and quietly
   collapse tenant isolation in production, the exact failure shape this project keeps finding.
3. `jevkey.pvt` at the repo root is **not a source**. It exists only for the overview's live
   verification runs; a deployment reads the environment. Never referenced from code.
4. Neither secret is ever logged, placed in an exception message, or written into an assessment or
   decision record, the same rule the Jev key already follows.

**Privacy:** this is not a zero-PII service. Pseudonymize profile identifiers with tenant-scoped keyed
hashes, minimize raw content retention, separate payload access from aggregate operations, and never log
request bodies. Tenants prohibiting external content processing use the local evidence path with an
explicit *semantic-unavailable* state.

**Escape is bounded, not eliminated:** SMTP delivery is not exactly-once. Preserve attempt records and
surface ambiguity rather than pretending `Message-ID` deduplication solves it.

## 5. Shape of the problem

**Assessment pipeline:**

1. Validate envelope, authorization, size, parsing limits; apply mandatory hard limits.
2. Parse an analysis copy; extract deterministic evidence.
3. Read profile snapshots; reserve/update atomic observed-rate counters for this attempt.
4. Obtain semantic evidence, exact cache hit, provider, or explicit *unavailable*.
5. Compare against profiles and recent campaign windows; compute drift and trend evidence.
6. Run versioned deterministic policy; persist the decision.
7. For submissions: durably accept and schedule delivery/hold/quarantine, **or decline responsibility
   before acceptance**.
8. Commit trusted learning only when an authorized outcome or explicitly permitted rule exists.

Assessment-only calls do not send mail or train profiles. Submission owns observation and delivery state.

**Components** (logical; need not each be a project): Core (contracts) · MIME adapter · Jev adapter ·
Adaptive engine · Persistence · Host · Transport adapter.

**Two evidence stores that must never be conflated:**
- **Observed state**, all attempts and recent campaigns, including rejected traffic. Detects abuse, bounds throughput.
- **Trusted baseline**, approved training samples/outcomes. Defines legitimate behaviour. *A sent or unreported message is not automatically trusted.*

**Temporal model:** fixed clock buckets (not "one tick per message"), multiple windows for burst and slow
change, rate features normalized by elapsed time. Velocity and acceleration are derived from a smoothed
normalized behavioural vector; derivative evidence is suppressed during cold start, long gaps, sparse
buckets, or regime changes. **Acceleration is noisy evidence, it can never alone justify a hard block.**

**Interventions:** Allow · Hold (durably retained to a bounded re-evaluation deadline) · Quarantine ·
Defer (decline responsibility temporarily) · Reject (decline permanently, before acceptance). Shadow is a
*mode* that records the proposed intervention while forwarding. Policy precedence: authorization/resource
controls → emergency kill switch and hard quotas → verified security rules → behavioural/semantic risk →
recipient preference.

**Durability boundary:** an SMTP `250` after `DATA` transfers delivery responsibility. Either persist the
original payload, routing, and queue metadata durably **before** acceptance, or defer without accepting.
Disk full or unavailable storage must never yield successful acceptance.

## 6. Jev integration, verified contract

**Verified against the live TypeSafe documentation on 2026-09-22.** This replaces the earlier
"unverified" placeholder: the contract below is read from the published reference, not inferred.

| Fact | Value |
| --- | --- |
| Endpoint | `POST https://api.typesafe.ai/v1/systemone` |
| Auth | `Authorization: Bearer <API_KEY>` · `Content-Type: application/json` |
| Request | `{ state, model, questions }`, where `questions` is `map<questionId, Question>` |
| Question id | Author-chosen key. **Not sent to the model, not used in inference**, the complete judgment must live in `instructions`. |
| Types | `noul` (yes/no → probability) · `choice` (one of a set + distribution) · `score` (position along ordered levels) |
| Noul answer | `{ type, noul }`, probability 0…1. **No `confidence` field.** |
| Choice answer | `{ type, choice, probabilities, confidence }`, max **255** options |
| Score answer | `{ type, score, legend, probabilities, confidence }`, **2…10** levels |
| Response | `{ model, answers, usage }`; `usage = { input_tokens, output_tokens }`; `answers` keyed by our question ids |
| Model | `jev-1.13.0`; aliases `jev-latest` (stable) and `jev-preview`. Response `model` reports the **resolved versioned id**. |
| Listing | `GET /v1/models` |
| Context | 64k per request, of which **32k for `state` plus the longest question** |
| Rate limits | 250k tokens/sec, 1,200 requests/min, documented as adjusting dynamically |
| Pricing | $42/Btok input; output tokens free |
| Errors | `401` bad key · `422` validation (names the offending field) · `429` rate limit · `529` overloaded, back off and retry on the latter two |
| Modality | **Text only**, no image, audio, or video. English primary; other languages accepted with lower accuracy. |

Design consequences this settles:

- **The 12 semantic dimensions are Noul questions.** They are independent properties, several of which
  may hold at once, and the source spec already requires that "independent questions need not sum to
  one", precisely Noul semantics. A Choice would force one mutually-exclusive label and would be wrong.
- **All 12 dimensions go in a single request.** Questions share one `state`, are evaluated independently,
  and cannot see each other's answers. Adding questions "barely changes the response time", so one
  fan-out call is the correct shape.
- **The `state` object is the bounded structured input**, subject, new body text, separately labelled
  quoted text, displayed vs actual links, attachment metadata, explicitly tagged contextual facts, with
  nested values referenced from `instructions` by backticked path (e.g. `` `email.links[0].actual` ``).
- **No .NET SDK exists.** TypeSafe publishes Python and JavaScript SDKs only, so the Jev adapter is an
  HTTP client we write. That is now a known, scoped piece of work rather than an assumption.
- **There is no local or on-premise Jev**, only the hosted endpoint. "A local Jev release is not
  assumed" is now verified fact. Tenants prohibiting external content processing take the
  *semantic-unavailable* path.

### 6.0 Measured against the live API, verified 2026-09-22

The adapter in `src/StyloMail.Jev` was run against the real endpoint with a synthetic message and
the operator's key. These are measurements, not assumptions:

| Observation | Measured |
| --- | --- |
| Requested `jev-1.13.0`, response reported | `jev-1.13.0`, **the pin holds**; the alias was not used |
| Warm in-process latency (5 calls, 1 fan-out request of 11 Nouls) | **238 / 250 / 253 / 267 / 276 ms**, median **253 ms** |
| Tokens per message | ~1,276 in, ~236 out |
| Cost per message at $42/Btok input | ≈ **$0.000054**, about **$0.054 per 1,000 messages** |
| Dimensions returned | 11 `Available`, 1 `NotApplicable` (conversational continuity, correctly not asked), 0 `Unavailable` |
| `Confidence` on every Noul | **null on all 11**, discrepancy #2 below is now *proven*, not inferred |

Scoring was semantically sensible on the synthetic phishing sample: `credential_request` 0.99,
`urgency_pressure` 0.99, `link_lure` 0.99, `sensitive_data_request` 0.98, `threat_reward_inducement`
0.98, `identity_authority_claim` 0.76; `secrecy_bypass` 0.23, `transactional_character` 0.11,
`payment_redirection` 0.09, `unsolicited_solicitation` 0.07, `attachment_lure` 0.03.

**The 1-second deadline is correct warm and too tight cold.** Warm median 253 ms leaves ample room.
But the *first* call in a cold process also pays DNS resolution, TLS handshake and HttpClient
warm-up, and was observed exceeding 1 s and tripping our own client timeout, producing an
`Unavailable` result for that message. Left unaddressed this means **the first message after every
restart silently loses its semantic evidence**, precisely when the system is least proven. Mitigate
by warming the connection at startup, or by allowing a longer deadline for a process's first call.

<small>Measurement caveat: wall-clock runs of the whole process showed 2.9–6.3 s. Those include
`dotnet run` startup, JIT and restore checks, and are **not** the provider cost, do not quote them
as latency.</small>

### 6.1 Discrepancies against the source specification

Places where `email-proxy-spec.md` assumes something the API does not provide. Each needs a decision
before implementation:

1. **No provider request id exists.** The source spec's audit record wants a "provider request ID"; the
   API documents none, no id field, no id header. The ledger must carry **our own** correlation id plus
   the resolved `model` string. Do not plan on a vendor-supplied one.
2. **Noul answers carry no confidence.** The source spec treats semantic probabilities, model
   uncertainty, and sample support as distinct fields. For the 12 dimensions there is **only a
   probability**, no uncertainty field at all. Either derive our own dispersion measure, or use a Score
   variant for dimensions where confidence is genuinely wanted. Do not assume a field that isn't sent.
3. **No timeout is documented.** The proposed 1-second semantic deadline is **ours**, enforced
   client-side. It is a client policy, not a provider SLA.
4. **`jev-latest` moves without notice.** The docs explicitly recommend pinning the versioned id when
   confidence thresholds are tuned, which they are here. **Pin `jev-1.13.0` in config**, record the
   resolved `model` in every cached assessment, and treat an alias move as cache-invalidating.
5. **Composite scoring does not cover correlation.** The rule "do not multiply correlated semantic
   outputs as independent likelihoods" is sound but is **not** supported by TypeSafe's composite-scoring
   page, which covers weighted sums and is silent on correlated outputs and uncertainty propagation.
   That rule remains our own policy reasoning and should be documented as such.
6. **Confidence bands are ours to set.** Documented guidance is high → act, medium → caution, low → do
   not act, with "a confidence threshold is not one number", gate by consequence, stricter for
   irreversible actions. Thresholds require tuning on representative replay data, still outstanding.

## 7. Evidence gaps, still open

1. **Existing SMTP/MTA integration in the codebase, there is none.** `stylomail` is an empty repo: not a
   git repository, no commits, no source. The source spec's instruction to "use the existing bones
   wherever they fit" has nothing to bind to here. The only sibling .NET reference in this workspace is
   `lucidRESUME`, which is a **different repo with its own overview agent**, reuse from it requires
   coordination, not assumption.
2. **Permitted cloud content and provider data-handling terms.** Pricing, rate limits and context size
   are now documented; data-handling and retention terms are not. Sending message content to a hosted API
   is an operator decision with privacy consequences.
3. **Baseline half-lives, hold windows, and thresholds**, must come from representative replay data.
4. **Production sending ceilings, failure posture, and retention**, an operator decision.
5. **Measured benefit of acceleration** versus simpler rates/drift. Remove it if it adds noise without
   predictive value.

## 8. Provider integrations and the credential model

**Added 2026-09-22** at the operator's direction: StyloMail must slot into existing systems and
integrate with popular mail providers. This section records the design; it is **not yet built**.

### 8.1 Providers are not interchangeable

The named providers do materially different things, and treating them as one category would produce
a connector layer with the wrong abstraction:

| Provider | What it actually is | Integration surface | Direction |
| --- | --- | --- | --- |
| **Cloudflare Email Routing** | MX-level inbound routing | Worker binding receives the raw message; no mailbox-read API | Ingress |
| **Gmail / Google Workspace** | Mailbox + transport | Gmail API (OAuth), Pub/Sub push, SMTP relay | Both |
| **Outlook / Microsoft 365** | Mailbox + transport | Microsoft Graph (OAuth), change notifications, SMTP AUTH | Both |
| **SendGrid** | Transactional ESP | Inbound Parse (webhook), Event Webhook, v3 Mail Send | Both |
| **Mailchimp** | **Marketing** platform, not a transport | Marketing API; transactional is a *separate* product (Mandrill) | Egress, marketing only |

### 8.2 Two credential models, and the choice is forced by direction

**Pass-through.** The client presents credentials to StyloMail, which validates and relays them
without storing anything. This is the canonical mail-proxy pattern (nginx mail `auth_http`,
Perdition, Dovecot Director): read the account from credentials the client already holds, replay
auth, bridge the session.

**Stored.** StyloMail holds provider credentials, OAuth refresh tokens or API keys.

Pass-through is preferable wherever it works, because it keeps StyloMail out of the credential
business entirely: a breach of StyloMail is then not a breach of every connected mailbox. But
**pass-through is only possible for interactive, session-oriented flows.** A Gmail Pub/Sub push or a
Graph change notification arrives with no client attached to present anything, so webhook and
push-based ingestion *require* stored credentials.

The split therefore falls out of direction, not preference:

- **Outbound submission (SMTP)**, pass-through is viable and preferred. Nothing stored.
- **Inbound via MTA handoff**, no provider credentials at all (Cloudflare Email Routing fits here).
- **Inbound via provider API/push**, stored OAuth tokens, per tenant, explicitly opted into.

### 8.3 Design rules

1. **Credential mode is a property of the connector, never of Core.** Each connector declares
   whether it is pass-through or stored, so a tenant's stored-secret count is auditable at a glance.
2. **Stored connectors are opt-in per tenant** and reference a secret store by opaque reference,    never an inline value, and never a value in this repository.
3. **A connector never widens the assessment path.** It supplies a `MailAnalysisInput` and
   provenance; it does not get to choose an action or bypass policy precedence.
4. **SendGrid's Event Webhook is a first-class input to the trusted baseline**, bounces and
   complaints are authoritative delivery outcomes, which is exactly the label quality the adaptive
   engine needs. Delivery alone still does not establish safety.
5. **Default remains the MTA-handoff model** (spec §4). Provider adapters are additive; a deployment
   with none of them must still work.

### 8.4 Operator decision, settled 2026-09-22

> **Cloudflare Email Routing. Inbound-only. No other provider connector.**

Consequences, recorded because they are decisions rather than omissions:

1. **The source spec's exclusions stand unchanged.** "No mailbox hosting, IMAP client" and "no
   marketing platform" (§2) are **not** lifted. Direct Gmail/Outlook integration and Mailchimp/
   Mandrill are therefore **out of scope**, not deferred, the §8.1 table describes options that
   were considered and declined, not a roadmap. Do not build toward them implicitly.
2. **Cloudflare Email Routing is the only provider connector.** Inbound-only: a Worker binding
   receives the raw message and hands it to StyloMail. No OAuth, no stored credentials, no mailbox
   read. **The default deployment continues to hold zero provider secrets**, which was the whole
   argument for choosing it first.
3. **SendGrid was not selected, so there is no delivery-event feedback path.** This has a real cost
   worth stating plainly: bounce and complaint signals are the highest-quality labels the adaptive
   engine could receive for its trusted baseline, and without them the baseline learns only from
   operator and recipient feedback. §7's learning controls still work; they are simply narrower.
   Revisit only if the baseline proves too slow to converge in practice.
4. **`transport-` is unblocked** and scoped to: the SMTP/MTA handoff (ingress and egress), the
   delivery port implementation for the queue's worker, and this one connector. Nothing else.

## 9. Client access proxy, IMAP / POP3 / SMTP through Gmail

**Added 2026-09-22 at the operator's direction, marked HIGH PRIORITY. Not yet built.**

Users connect ordinary mail clients to StyloMail, which authenticates them with **StyloMail-issued
credentials** and then proxies the session to their real provider, initially Gmail.

### 9.1 This is a second subsystem, not an extension of the first

This is the **session access proxy** shape declined at the original scoping question, and it is
architecturally distinct from everything in §5:

| | Store-and-forward path (§5) | Access proxy (§9) |
| --- | --- | --- |
| Unit of work | a message | a connection |
| Lifetime | seconds, then spooled | minutes to hours, held open |
| State | durable queue | live session |
| Failure mode | retry later | client sees the session drop |
| Auth | per-message provenance | per-connection, to a backend |

It shares Core contracts, the assessment pipeline and the credential model, but nothing about the
queue, spool or delivery worker applies. **Do not model it as a transport adapter of the MTA path.**

### 9.2 Gmail will not accept a stored password, this is settled by Google, not by us

The operator's credential design is sound and is adopted: **users authenticate to StyloMail with a
StyloMail-issued username and password, and StyloMail holds the backend credential.** The client
never receives the real provider credential, which is a genuine security improvement over
pass-through.

**But the backend credential for Gmail cannot be a password.** Google removed username/password
authentication for IMAP, SMTP, POP, CalDAV and CardDAV, the protocols named here specifically.
Enforcement completed **1 May 2025** (originally scheduled 30 September 2024, paused, then resumed).
App passwords survive only as a transitional exception requiring 2-Step Verification, are being
wound down, and are not a foundation to build on.

**Therefore the stored Gmail credential is an OAuth 2.0 refresh token**, obtained once per user via
a Google consent flow, refreshed by StyloMail, and replayed to Gmail as `XOAUTH2`.

### 9.3 The proxy is genuinely useful *because* of that constraint

There is a real product argument here, and it is the strongest one for building this: **Gmail
requires OAuth, but a great deal of client software still cannot do OAuth.** An IMAP client that only
speaks `LOGIN user pass` cannot connect to Gmail any more. StyloMail can absorb that, the client
authenticates with StyloMail-issued basic credentials, and **StyloMail** performs the OAuth exchange
on the backend. The proxy does not merely sit in the path; it restores access that Google's change
removed for legacy clients.

### 9.4 Hard constraints

1. **Google OAuth app verification is a gating dependency, not a formality.** Gmail IMAP/SMTP scopes
   are *restricted* scopes; production use requires Google's verification process, which for
   restricted scopes includes a security assessment. **This has a lead time of weeks and can fail.**
   It must be started before the work is scheduled, not after it is written.
2. **StyloMail becomes a high-value credential store.** Encrypted at rest is necessary and not
   sufficient. Required: per-tenant key isolation, a documented key-rotation and revocation
   procedure, an explicit breach path, and no credential material in logs, exceptions or decision
   records, the same rule the Jev key already follows.
3. **A refresh token is a bearer credential for the user's entire mailbox, including send.** Its blast
   radius is strictly larger than a message. Scope minimisation (read-only where the use case allows
   it) should be preferred per tenant.
4. **Preserve the opaque-credential rule.** The client's StyloMail-side password and the backend
   refresh token are separate secrets with separate rotation. Neither may be derivable from the other.
5. **IMAP must still be enabled per Gmail account**, and this is a per-user configuration step the
   product must surface, because the failure looks like an authentication bug.
6. **This does not lift the "no mailbox hosting" exclusion.** StyloMail proxies a session; it never
   stores a mailbox or becomes the source of truth for mail. If a design starts requiring us to hold
   message state *because* the client is offline, that is mailbox hosting and is out of scope.

### 9.5 Operator decision, settled 2026-09-22

> **App passwords first as a documented transitional path. Pursue Google OAuth verification in
> parallel. Accept the transitional dependency in exchange for working sooner.**

Protocols in scope: **IMAP, POP3 and SMTP submission**, the operator named all three and marked
them high priority.

**The architectural consequence, which is the point of this section:** the backend credential must
be **pluggable from the first commit**. Define a credential-provider seam with two implementations, app password now, OAuth refresh token later, so the OAuth migration is an **implementation swap,
not a redesign**. Concretely: nothing above that seam may branch on which kind of credential it is,
and the credential store must carry a discriminator rather than assuming a password shape. If the
OAuth work later requires changing callers, this decision was implemented wrongly.

**Risks, stated plainly rather than buried:**

1. **App passwords are being wound down by Google.** This is a transitional exception with an
   announced end, not a foundation, building to a seam rather than to the mechanism is what keeps
   that from becoming a rewrite.
2. **2-Step Verification is mandatory per user.** Onboarding must surface this, because a user
   without 2SV cannot generate an app password, and the failure presents as an authentication bug.
3. **An app password is a bearer credential for the whole mailbox, including send.** Its blast
   radius is strictly larger than any single message, which is the same concern §9.4 raises for
   refresh tokens, app passwords do not reduce it, they only defer the OAuth question.
4. **Secondary revocation path.** A user who revokes the app password in Google must be able to
   remove it from StyloMail, and a revoked credential must fail closed and surface as an
   authentication failure rather than a silent retry loop against Google.
5. **Google may disable the path with little notice.** The seam in point 1 is also the incident
   response: if app passwords stop working, the OAuth implementation is the fallback and the work
   is already done.

**Still open:** whether retrieval (IMAP/POP3) and submission (SMTP) ship together or retrieval
first. Retrieval carries a materially smaller blast radius, no send, and delivers most of the
value, so it is the safer first slice unless the operator says otherwise.

## 10. Relationship to this project's scoping Q&A

The operator confirmed by question (2026-09-22): **inbound/outbound mail flow** in both directions;
a **security/edge boundary** (TLS, auth, rate limits, reputation; back-ends not directly reachable);
**store-and-forward** ownership of the queue; **fixed config mapping** to back-end hosts with no external
routing dependency.

This spec is consistent with all four, and materially wider: it adds the semantic-evidence layer,
adaptive behavioural profiling, campaign detection, and the learning/poisoning controls. Where the source
document is more conservative than the Q&A (e.g. "do not build a public MX from scratch", "behind an
established MTA/application connector"), **the source document's constraint wins**, it reflects a
deliberate safety posture, not an oversight.

The essential product boundary:

> **Classify the message, understand the changing relationship and flow, then intervene minimally
> through explicit policy.**
