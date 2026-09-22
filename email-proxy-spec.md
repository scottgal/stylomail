# Adaptive Two-Way Email Proxy

Implementation specification · draft 0.1 · 22 September 2026

## 1. Purpose

Build a small, explainable inbound/outbound email security component. Jev converts unstructured message content into typed semantic evidence; a local adaptive system combines that evidence with message metadata, communication relationships and behaviour over time.

The product detects suspicious communication rather than merely suspicious words. It should identify compromised outbound accounts, inbound phishing, impersonation, emerging campaigns and unusual changes in legitimate communication, while measuring and reducing false positives.

Delivery: a transport-independent .NET library, ASP.NET Core HTTP host and standalone executable, with a thin SMTP integration. Local SQLite persistence; no mandatory distributed services. Cloud Jev is initially optional/configured, behind a provider interface that can later accommodate a compatible local classifier. A local Jev release is not assumed.

“Post-Bayesian” describes moving beyond token-frequency evidence, not rejecting Bayesian methods. “Post-LLM” describes structured classification rather than conversational generation; it is not a claim that errors or adversarial inputs disappear.

## 2. Scope and release boundaries

### Weekend demonstrator

1. Evaluate raw MIME through a NuGet-style library API and CLI.
2. Accept inbound/outbound assessment and submission through authenticated HTTP endpoints.
3. Integrate one existing mail boundary: trusted MTA handoff or a restricted SMTP submission listener relaying to one configured upstream. Do not build a public MX service from scratch.
4. Extract deterministic features and approximately twelve semantic dimensions.
5. Maintain sender, recipient and relationship profiles, fast/slow trends, and acceleration evidence.
6. Reuse exact semantic assessments; detect bounded near-duplicate campaigns.
7. Support allow, bounded hold, quarantine and pre-acceptance reject/defer.
8. Provide trusted feedback, an explainable decision ledger, shadow mode and deterministic replay tests.

### Production gates and subsequent work

Full public-MX operation, general-purpose queue/provider adapters, attachment sandboxing, OCR, distributed clustering, HA, rich dashboards and broad provider compatibility are outside the weekend scope. Existing mail infrastructure should continue to own internet-facing protocol complexity where possible.

Production enforcement requires transport conformance, crash recovery, resource limits, privacy review, labelled evaluation and delivery-failure handling. A successful demo is not evidence of production safety.

No mailbox hosting, IMAP client, marketing platform or autonomous incident-response agent. No promise to detect login/account takeover without relevant authentication telemetry. Email-only evidence can indicate suspected misuse, not prove how an account was compromised.

## 3. Architectural principles

- Probabilistic components provide evidence. Deterministic policy authorizes side effects.
- Separate semantic classification, behavioural observation, trusted learning and delivery.
- Evaluate against the pre-event trusted baseline; never normalize a message with its own evidence first.
- Cache assessments, not permissions. Every message receives current contextual and policy checks.
- Normal automation is not inherently abusive. Scheduled newsletters and transactional bursts need explicit traffic classes.
- A familiar sender is useful context, not a permanent bypass.
- Unknown is a distinct state, not a zero score or evidence of innocence.
- Preserve original MIME bytes for transport and signature integrity; normalized content is an analysis view.
- Bounded queues, memory, cardinality and provider spend are core features.

## 4. End-to-end processing

Ingress establishes tenant, direction, authenticated principal, envelope and trusted provenance. Client-supplied From headers cannot establish identity.

The assessment pipeline is:

1. Validate envelope, authorization, size and parsing limits; apply mandatory hard limits.
2. Parse an analysis copy and extract deterministic evidence.
3. Read profile snapshots; reserve/update atomic observed-rate counters for this attempt.
4. Obtain semantic evidence from an exact cache hit, provider, or explicit unavailable state.
5. Compare against profiles and recent campaign windows; compute drift and trend evidence.
6. Run versioned deterministic policy and persist the decision.
7. For submissions, durably accept and schedule delivery/hold/quarantine, or decline responsibility before acceptance.
8. Commit trusted learning only when an authorized outcome or explicitly permitted learning rule exists.

An assessment-only call does not send email or train profiles. Replays use isolated state and an injected clock. Submission owns observation and delivery state changes. Callers wanting assessment without participation in live traffic accounting use the assessment endpoint, not submission.

## 5. Components and contracts

Suggested logical components; these need not each become a project:

| Component | Responsibility |
| --- | --- |
| Core | Envelopes, evidence, profiles, assessment and policy contracts |
| MIME adapter | Bounded parsing, HTML-to-text, URL and attachment metadata |
| Jev adapter | Request mapping, response validation, timeouts and model versioning |
| Adaptive engine | Trusted baselines, observed windows, drift, velocity and acceleration |
| Persistence | SQLite profiles, queue metadata, feedback and decision ledger; spool payloads |
| Host | ASP.NET Core endpoints, CLI commands, worker lifecycle and health |
| Transport adapter | Restricted SMTP/MTA integration and upstream delivery |

Core interface sketch, not a claim about an existing SDK:

```csharp
public interface IMailAssessor
{
    ValueTask<MailAssessment> AssessAsync(
        MailAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken);
}

public interface ISemanticMailClassifier
{
    ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        CancellationToken cancellationToken);
}
```

Public MIME integration should accept a `MimeMessage` analysis input while submission separately retains original bytes. Keep provider-specific DTOs out of Core. Use `TimeProvider`, explicit immutable snapshots and versioned configuration for replayability.

### Required data

**Envelope:** internal message ID, tenant, inbound/outbound direction, trusted principal/connector ID, SMTP MAIL FROM and RCPT TO, receipt timestamp, MIME digest and payload reference. Header Message-ID is untrusted and is not an idempotency key.

**Authentication context:** original connecting IP when trusted, SPF/DKIM/DMARC results with verifier identity, authenticated account, approved sender identities and optional application/session events. Missing provenance is recorded explicitly.

**Evidence:** signal ID, value, availability, origin, source/version, timestamp and observed scope. Semantic probabilities, model uncertainty, sample support and final policy score are distinct fields.

**Assessment:** risk dimensions, anomaly evidence, aggregate risk index, reliability metadata, ordered reason codes, actual action, proposed action in shadow mode, profile versions, policy/model/question versions, cache provenance and analysis coverage.

**Recipient disposition:** recipient-scoped risk, action and delivery state. Never expose one recipient's relationship history or address to another recipient.

## 6. Deterministic and semantic evidence

### Deterministic features

Extract envelope/header identity mismatches; display-name versus address mismatch; Reply-To novelty; trusted authentication results; actual link destinations versus visible labels; IDN/punycode representation; URL host novelty; attachment extension/type mismatch; message and recipient counts; template similarity; attachment hashes; message size; HTML/text disagreement; padding/obfuscation indicators; new correspondence relationships; thread-header consistency; and authenticated sending-context changes where available.

DKIM/SPF success is not a benign verdict. Compromised authorized accounts may authenticate correctly. Foreign-language content, uncommon names and new contacts are not suspicious by themselves. Thread headers are claims, not proof of a relationship.

Do not fetch links, load remote images, execute attachments or resolve redirects in the MVP. Any later fetching requires a separately sandboxed, SSRF-resistant service. Encrypted/password-protected content produces reduced coverage, not a clean verdict.

### Jev questions

Start with independently scored semantic properties rather than one “is spam” answer:

| Dimension | Intended meaning |
| --- | --- |
| Unsolicited solicitation | Promotional or commercial solicitation cues; actual consent comes from context |
| Credential request | Asking for passwords, tokens, authentication or wallet access |
| Payment redirection | Changing bank details or requesting unusual payment routes |
| Identity/authority claim | Claiming to represent a person, institution or trusted authority |
| Urgency/pressure | Attempts to bypass deliberation through pressure or deadlines |
| Secrecy/process bypass | Requests to avoid normal verification or approval |
| Sensitive-data request | Asking for confidential business or personal information |
| Link lure | Encouraging navigation for verification, reward, delivery or account recovery |
| Attachment lure | Encouraging opening or executing supplied material |
| Threat/reward inducement | Coercive consequences or implausible incentives |
| Transactional character | Order, booking, receipt, notification or service-message intent |
| Conversational continuity | Whether content fits supplied, bounded conversation context |

Output distributions and uncertainty must be retained when supplied. These are model assessments, not facts. Independent questions need not sum to one. Type correctness does not establish semantic accuracy or calibrated performance on email.

Use a bounded structured input: subject, new body text, optional separately labelled quoted text, displayed/actual links, attachment metadata and explicitly tagged contextual facts. Message content is always untrusted data, even when it contains instructions addressed to the classifier.

The adapter must validate actual Jev 1.13 input/output shape, limits and uncertainty semantics against accessible documentation or a recorded working call before integration. The supplied DefAPI page was unavailable during specification preparation; no endpoint, SDK method or vendor DTO is invented here. TypeSafe describes typed probabilistic decisions and parallel outputs, but published latency is not a proxy SLA. [TypeSafe introduction](https://typesafe.ai/blog/introducing-system-one-models-and-jev)

## 7. Adaptive profiles and temporal behaviour

Maintain bounded profiles for tenant + traffic class, outbound authenticated sender, inbound sender identity with authentication provenance, recipient, and sender-recipient relationship. Domain-level context is a fallback, never equivalent to account trust. Keep inbound and outbound statistics distinct while permitting explicit relationship linkage.

Profile state includes effective trusted support, means/variances of stable dimensions, typical traffic windows, recipient novelty, contact diversity, known link hosts, template fingerprints, semantic centroids, fast/slow averages, last observation, baseline version and regime ID.

Separate two stores of evidence:

- **Observed state:** all attempts and recent campaigns, including rejected traffic. Used to detect abuse and bound throughput.
- **Trusted baseline:** approved training samples/outcomes. Used to define legitimate behaviour. A sent or unreported message is not automatically trusted.

Start with robust standardized distances and diagonal variance; clamp scales with variance floors. Full covariance/Mahalanobis scoring is a later option once sample support is adequate. Missing dimensions are masked with coverage recorded, not filled with zero.

### Velocity and acceleration

Use fixed clock buckets, not “one tick per message.” Maintain multiple windows for burst and slow changes. Count/rate features are normalized by elapsed time; semantic buckets require sufficient samples and otherwise remain missing.

For a smoothed, normalized behavioural vector z at bucket t:

```text
velocity[t]     = (z[t] - z[t-1]) / elapsed_time
acceleration[t] = (velocity[t] - velocity[t-1]) / elapsed_time
```

Compare trajectories with expected traffic-class patterns. Record a reason such as “recipient fan-out rising while payment-redirection evidence also rises,” rather than an unexplained acceleration scalar. Suppress derivative evidence during cold start, long gaps, sparse buckets or incompatible schema/regime changes. Record bucket uncertainty and minimum-support checks.

Acceleration is noisy evidence, not a guarantee of earlier detection. It cannot alone justify a hard block. Regular scheduled campaigns and seasonal changes must be represented in evaluation.

### Learning controls

Use time-aware EWMA, for example alpha = 1 - exp(-elapsed/tau), with bounded per-event/per-principal contribution. Cap how quickly trusted baselines move. Freeze baseline promotion during suspected compromise; continue observed traffic accounting.

Trusted labels come from authenticated operator/recipient feedback or configured authoritative application outcomes. Delivery, reply or absence of complaints alone does not establish safety. Feedback is scoped: “wanted promotion” changes recipient preference, not global phishing truth.

Keep label provenance, corrections and checkpoint/rebuild support. New regimes first run as candidates; promotion needs enough trusted support and a stability check. Baseline rollback must not restore depleted sending quotas or erase incident history.

## 8. Adaptive memoisation and campaign windows

Exact semantic cache keys include tenant, classifier/model version, question schema version, preprocessing version and a digest of the complete canonical classifier input. If relationship context was included, it is part of that input and therefore the key.

Retain the evidence distribution, timestamp, coverage and provenance. Apply configurable expiry, model/schema invalidation, bounded LRU/LFU eviction and sampled reclassification. Deduplicate concurrent identical provider requests with single-flight behaviour.

Exact semantic reuse does not skip current authentication, URL changes, behavioural counters, profile comparison or policy. Never memoise “allow this sender.”

Near-duplicate matching initially supplies campaign evidence only. Later approximate reuse needs a separately evaluated similarity threshold and exact preservation of security-bearing details: link destinations, payment identifiers, sender context and attachment hashes. Semantically similar wording with a changed bank account must miss the reuse gate.

Maintain bounded rolling campaign groups by template fingerprint, link-host set, attachment hash and semantic similarity. Detect coordinated fan-out and multi-account reuse within a tenant. Cap group cardinality and candidate comparisons. Cross-tenant sharing is off by default.

## 9. Policy and interventions

| Action | Meaning |
| --- | --- |
| Allow | Eligible for the configured next hop, subject to delivery quotas |
| Hold | Durably retained until a bounded re-evaluation deadline |
| Quarantine | Durably retained for authenticated review; not delivered |
| Defer | Decline responsibility temporarily before acceptance; caller may retry |
| Reject | Decline responsibility permanently before acceptance under explicit policy |

Shadow is a mode: record proposed intervention while forwarding under mandatory relay authorization, size limits, resource controls and operator emergency limits.

Policy precedence: authorization/resource controls; emergency kill switch and hard quotas; explicit verified security rules; behavioural/semantic risk; recipient preference. Allowlists have scope and expiry and do not bypass emergency ceilings.

The MVP aggregate score is a documented risk index, not a calibrated probability. Do not multiply correlated semantic outputs as independent likelihoods. Keep phishing/security risk separate from unwanted-marketing preference. Low support/uncertainty favours bounded hold or review rather than irreversible rejection. Known hard violations do not require model confidence.

Holds are observation windows: re-evaluate when new campaign evidence arrives or the deadline expires. A deadline never silently extends forever. Default suspected outbound compromise remains quarantined if unresolved; ordinary inbound unknowns follow an explicit tenant policy. Do not issue unsolicited email challenges, which could become backscatter. Reauthentication is an optional trusted control-plane integration.

Atomic outbound quotas are keyed by authenticated principal and tenant and count recipients, not just messages. Reserve budget before workers dispatch. Use a limited concurrency/in-flight budget so a late detection has a known maximum escape volume.

## 10. Mail transport correctness

The default deployment is behind an established MTA/application connector. Enforce outbound authentication and authorized sender identities; inbound traffic is only accepted for configured recipient domains. No unauthenticated third-party relay. Require TLS where credentials or trusted internal handoff are used.

An SMTP 250 after DATA transfers delivery responsibility. Either persist the original payload, routing and queue metadata durably before acceptance, or defer without accepting. Disk full and unavailable durable storage cannot yield successful acceptance. HTTP 202 submission likewise requires a durable queue ID. Assessment success has no delivery implication. [SMTP responsibilities](https://www.rfc-editor.org/rfc/rfc5321)

Queue states: Queued, Held, Quarantined, Delivering, Delivered, RetryScheduled and TerminalFailure. Record per-recipient completion; worker leases must be recoverable after a crash. Expired holds are policy decisions, not delivery acknowledgements.

Use tenant-scoped client idempotency keys for HTTP submission. Retries with the same key and payload return the existing submission; a different payload conflicts. SMTP delivery is not exactly once: if upstream accepted a message but the acknowledgement was lost, retry may duplicate it. Preserve attempt records and surface this ambiguity; do not pretend to eliminate it with Message-ID deduplication.

Separate abuse attempt counters from accepted/delivered counters. Re-delivery retries do not become new trusted samples. Retry temporary upstream failures with bounded backoff and a configured expiry; handle permanent failures through the trusted MTA's DSN policy. Never send bespoke warnings/bounces to an unverified spoofed From address. Enforce loop detection and hop limits.

For multi-recipient messages, persist individual dispositions and retry only pending recipients. Do not reject an entire SMTP transaction while silently keeping a subset. Split downstream deliveries without leaking Bcc addresses; SMTP-facing rejection policy must be transaction-consistent, or use a handoff supporting per-recipient results.

Preserve signed content. Do not rewrite subject, body or links by default. Keep analysis annotations in the ledger; outbound signing belongs after any intentional modification. Accept Authentication-Results only from configured trusted boundary verifiers; do not trust a header supplied by the message author. SPF needs the original sending connection context, not the proxy's own address. [Authentication-Results trust boundaries](https://www.rfc-editor.org/rfc/rfc8601)

## 11. Storage, privacy and failure modes

SQLite stores profile snapshots, versioned decisions, feedback, queue metadata, recipient states and cache metadata. Payloads use an access-controlled encrypted spool. Implement atomic payload/metadata commit ordering and orphan recovery. Queue state is durable before acceptance; lower-criticality profile snapshots may use write-behind with a journal/checkpoint recovery strategy.

Bound memory by tenant: active profiles, relationships, rolling windows, cache entries, queue count/bytes and semantic concurrency. Profile eviction dehydrates state without granting a fresh quota. Use transactional counter updates and optimistic profile versions to avoid concurrent-send races.

Email contains personal and confidential data: this is not a zero-PII service. Pseudonymize profile identifiers using tenant-scoped keyed hashes, minimize raw content retention and separate payload access from aggregate operations. Key rotation/deletion and backup retention need documented procedures.

Cloud semantic analysis requires explicit configuration and an operator decision about permitted content. Redact credentials, reset tokens and unnecessary personal fields before external transmission, mark reduced coverage, and do not log request bodies. Redaction does not guarantee anonymity. Tenants prohibiting external content processing use the local evidence path with a semantic-unavailable state.

Starting retention proposals: delivered payloads removed after 24 hours; quarantined payloads retained 7 days; derived decision/feedback records 30 days; inactive profiles expire after 90 days. These are configurable engineering defaults, not legal guidance. Reviewers can export/delete scoped data subject to documented backup expiry.

| Failure | Required behaviour |
| --- | --- |
| Jev timeout, invalid response or budget exhausted | Circuit breaker; mark semantic unavailable; local policy chooses allow/hold/quarantine, never auto-clean |
| Durable storage unavailable/full | Stop new acceptance; return temporary failure; alert |
| Upstream unavailable | Retain accepted messages and retry; apply queue-pressure admission control |
| MIME/parser limit exceeded | Explicit unsupported/oversize disposition; do not analyse an arbitrary fragment as a complete message |
| Profile store cold/unavailable | Conservative cold-start policy; preserve hard ceilings |
| Missing application auth telemetry | Mark takeover evidence incomplete; do not fabricate session history |

## 12. Host API and operator surface

Proposed routes, authenticated and tenant-scoped:

- `POST /v1/assessments`: MIME plus envelope context; returns an assessment without delivery or learning.
- `POST /v1/submissions`: idempotent durable intake with configured route; returns queue ID and assessment/status.
- `GET /v1/submissions/{id}`: recipient-level disposition and delivery progress.
- `GET /v1/decisions/{id}`: evidence, reasons, versions and coverage.
- `POST /v1/feedback`: authorized label/correction referencing a decision and scope.
- `POST /v1/quarantine/{id}/release`: audited, idempotent release; recheck current routing/security ceilings.
- `POST /v1/controls/senders/{id}/pause`: pause the authenticated principal's outbound delivery.
- `/health/live`, `/health/ready`, `/metrics`: no sensitive content.

Separate privileges for assessment, sending, review, feedback and administration. Protect browser-based controls against CSRF. Never derive tenant authority from a request body field alone.

CLI: `assess message.eml`, `replay fixtures/`, `serve`, `quarantine list`, `quarantine release`, `profiles inspect`. CLI assessment must not silently transmit private content externally; provider usage follows explicit configuration.

MVP operator UX is a small read-only decision/quarantine list plus authenticated actions, or CLI only. No dashboard framework project is required.

## 13. Verification and acceptance

Use deterministic clocks, replayable state, fake upstream SMTP, fixture MIME and recorded/fake provider responses. Live Jev evaluation is separate and never defines unit-test expected answers. Chronological train/test separation prevents future labels and duplicate templates leaking into evaluation.

| Scenario | Acceptance condition |
| --- | --- |
| Routine receipts and conversations | Stable replay decisions; no false intervention in the fixed benign smoke set |
| Legitimate scheduled burst | Traffic-class context distinguishes it from novel fan-out without disabling quotas |
| Compromised authenticated sender | Correct authentication cannot bypass recipient caps; suspicious outbound traffic is held/quarantined |
| Near-identical lure, changed URL/bank details | Semantic reuse gate misses; current evidence is evaluated |
| Slow baseline poisoning | Unlabelled/suspicious events affect observations but do not silently become trusted history |
| Cold start and sparse windows | Coverage/support reported; no false acceleration from missing buckets |
| Malicious classifier instructions in email | Fixed output contract and policy authority remain intact; adversarial accuracy measured separately |
| Provider outage | Deterministic configured fallback; no unbounded pending calls |
| Crash after acceptance | Every accepted recipient has recoverable durable state |
| Lost upstream acknowledgement | Ambiguous delivery is recorded and retry semantics are documented |
| Spoofed tenant/authentication headers | No privilege, trust or cross-tenant cache/profile access is gained |
| Malformed MIME or decompression bomb | Bounded resources and explicit disposition; no execution/fetching |
| False-positive correction | Scoped label is recorded; baseline rebuild/correction does not grant a universal bypass |

Report separate metrics for spam preference, phishing and suspected account misuse: precision/recall, false interventions per 1,000 legitimate messages, time/messages/recipients before containment, hold duration, release rate, coverage, provider calls per 1,000 messages, cache hit rate and stale-reuse errors. If exposing probabilities, assess calibration on held-out labelled mail.

Provisional benchmark goals, not claims: local cached assessment p95 below 20 ms for messages up to 100 KiB on a documented development machine; configured semantic deadline initially 1 second; default demonstration hold window 5 seconds with a hard 30-second re-evaluation deadline. Benchmark durability and provider time separately. Demonstrate a one-hour bounded-resource mixed-load run with provider/upstream failures and restart injection.

Containment tests must report escape bounds explicitly: configured recipient budget plus already reserved in-flight recipients. Passing a tiny clean fixture set is a smoke test, not a production false-positive rate.

## 14. Implementation order

1. Core contracts, MIME fixtures, bounded feature extraction and deterministic assessment CLI.
2. Jev adapter integration spike: verify contract, versions, coverage, latency and privacy behaviour; add exact semantic cache.
3. Profiles and observed windows; cold start, trusted feedback, EWMA, normalized trends and acceleration tests.
4. Policy/reasons, shadow mode, campaign matching, quotas and bounded hold worker.
5. SQLite/spool durability, authenticated HTTP host and one restricted mail handoff.
6. Crash/retry/security tests, replay report and a short working demonstration.

Use the existing bones wherever they fit; inspect the actual repository before assigning files or claiming reuse. No existing code was inspected for this specification.

The demo should show a legitimate account sending normal mail, then changing recipient fan-out and message intent while authentication remains valid. Show the evidence evolving, the proxy holding a batch, subsequent events strengthening the decision, and quarantine before the configured escape bound. Follow with a legitimate newsletter case and a false-positive correction.

## 15. Decisions still requiring evidence

- Exact Jev wire contract, version identification, uncertainty semantics and limits.
- Which existing SMTP/MTA integration is already present in the codebase.
- Permitted cloud content and actual provider data-handling terms.
- Baseline half-lives, hold windows and thresholds from representative replay data.
- Production sending ceilings, failure posture and retention chosen by the operator.
- Measured benefit of acceleration versus simpler rates/drift; remove it if it adds noise without predictive value.

The essential product boundary: **classify the message, understand the changing relationship and flow, then intervene minimally through explicit policy.**
