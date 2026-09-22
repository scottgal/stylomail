# StyloMail, Architecture

The C4 component view of the system described in `spec.md`. Colour identifies the **owning agent**,
so this diagram doubles as the fleet's ownership map.

```mermaid
C4Component
    title StyloMail, component view (adaptive two-way email security proxy)

    Container_Boundary(stylomail, "StyloMail") {

        Component(core, "Core contracts", ".NET library", "Envelopes, evidence, assessment, profiles, actions. Defines the vocabulary every other component speaks. Owns the invariants: Noul has no confidence, Unavailable is not zero, Message-ID is untrusted, bytes are preserved.")

        Component(mime, "MIME adapter", ".NET library", "Bounded MIME parsing and deterministic evidence: identity mismatches, display-vs-actual links, IDN homographs, attachment type mismatch, HTML/text disagreement. No network access ever.")

        Component(jev, "Jev adapter", "HTTP client", "Turns message content into typed semantic evidence via TypeSafe System One. One fan-out request, 12 Noul dimensions, pinned model id, explicit unavailable state on failure.")

        Component(adaptive, "Adaptive engine", ".NET library", "Bounded sender/recipient/relationship profiles. Observed state vs trusted baseline kept strictly separate. Drift, velocity and acceleration over fixed clock buckets. Campaign windows supply evidence only - never a reuse gate.")

        Component(policy, "Policy engine", ".NET library", "The only place an action is chosen. Orders precedence: resource controls, verified rules, compromise posture, risk, recipient preference. Probabilistic components supply evidence; only this authorises side effects.")

        Component(queue, "Durable queue + spool", ".NET library + SQLite", "Durably accepts before acknowledging, spools payloads atomically with payload-before-metadata ordering, claims by recoverable lease, tracks per-recipient state. Owns the 250-after-DATA durability contract.")

        Component(delivery, "Delivery worker", ".NET worker", "Leases accepted items, dispatches per recipient, schedules bounded retry, drains gracefully on shutdown. Speaks to the outside world only through an injected delivery port, so it never opens SMTP itself.")

        Component(assessment, "Composition root", ".NET library", "Wires MIME, Jev, Adaptive, Policy and Queue into one IMailAssessor. Owns the semantic cache decorator. Enforces that assessment-only traffic never reaches durable acceptance and never commits learning.")

        Component(host, "Host", "ASP.NET Core", "Authenticated, tenant-scoped HTTP API and CLI: assess, submit, decisions, feedback, quarantine release, sender pause. Shadow mode. No sensitive content in health or metrics.")

        Component(transport, "Transport and connectors", "SMTP / provider SDKs", "Ingress and egress at the edge. MTA handoff by default. Provider connectors declare their own credential mode - pass-through or stored - so a tenant's secret count stays auditable.")
    }

    System_Ext(upstream, "Upstream MTA", "Accepts our delivery and owns internet-facing protocol complexity")
    System_Ext(systemone, "TypeSafe System One", "Hosted Jev semantic classifier")
    System_Ext(providers, "Mail providers", "Cloudflare Email Routing, Gmail, Outlook, SendGrid")

    Rel(transport, mime, "Hands over raw MIME for analysis")
    Rel(transport, queue, "Requests durable acceptance before answering 250")
    Rel(transport, upstream, "Delivers accepted mail; DSN policy stays upstream")
    Rel(transport, providers, "Ingests via webhook or handoff")

    Rel(mime, core, "Produces deterministic Evidence")
    Rel(mime, jev, "Supplies the bounded analysis view")

    Rel(jev, systemone, "POST /v1/systemone with bounded state and Noul questions")
    Rel(jev, core, "Returns semantic Evidence and cache provenance")

    Rel(adaptive, core, "Produces behavioural Evidence")
    Rel(mime, adaptive, "Feeds message and relationship observations")

    Rel(policy, core, "Consumes Evidence, returns a MailAction")
    Rel(jev, policy, "Evidence only - never an action")
    Rel(adaptive, policy, "Evidence only - never an action")

    Rel(queue, core, "Persists per-recipient dispositions")

    Rel(host, assessment, "Owns no pipeline itself; calls IMailAssessor")
    Rel(assessment, mime, "Starts the pipeline with deterministic evidence")
    Rel(assessment, jev, "Requests semantic evidence through the cache decorator")
    Rel(assessment, adaptive, "Reads profiles, writes observations")
    Rel(assessment, policy, "Supplies evidence, receives the authorised action")
    Rel(assessment, queue, "Accepts for durable delivery when not assessment-only")
    Rel(delivery, queue, "Leases, reports outcomes, recovers dead leases")

    UpdateElementStyle(core, $bgColor="#A1887F", $fontColor="#000000")
    UpdateElementStyle(jev, $bgColor="#A1887F", $fontColor="#000000")
    UpdateElementStyle(assessment, $bgColor="#A1887F", $fontColor="#000000")
    UpdateElementStyle(mime, $bgColor="#80CBC4", $fontColor="#000000")
    UpdateElementStyle(adaptive, $bgColor="#90A4AE", $fontColor="#000000")
    UpdateElementStyle(queue, $bgColor="#FFB74D", $fontColor="#000000")
    UpdateElementStyle(delivery, $bgColor="#FFB74D", $fontColor="#000000")
    UpdateElementStyle(policy, $bgColor="#FFF176", $fontColor="#000000")
```

## Ownership map

| Owner | Colour | Owns | State |
| --- | --- | --- | --- |
| `overview-` | `#A1887F` | Core contracts, spec, architecture, **Jev adapter**, **Policy engine** | live |
| `mime-` | `#80CBC4` | MIME adapter, deterministic evidence | complete (87 tests) |
| `adaptive-` | `#90A4AE` | Adaptive engine, profiles, temporal evidence, campaign windows | complete (100), extending |
| `queue-` | `#FFB74D` | Durable queue, spool, **delivery worker** | complete (44), worker next |
| `host-` | *(unassigned)* | HTTP host and CLI | live |
| `assess-` | `#A1887F` | Composition root, semantic cache decorator | live |
| `transport-` | *(unassigned)* | Transport adapter and provider connectors | **not yet spawned, blocked on spec §8.4 decision** |
| `policy-` | `#FFF176` | Reserved, Policy is currently held by `overview-` | not yet spawned |
| `jev-` | `#A1887F` | Reserved, Jev adapter is currently held by `overview-` | not yet spawned |

`overview-`, `jev-` and `assess-` resolve to the same roster colour. That is a genuine collision in
the roster, not a rendering artefact, noted here so it is not mistaken for a mistake.

## Adjacency, who to redirect to

- **Read path vs write path of the same surface sit together.** Deterministic evidence (`mime-`) and
  behavioural evidence (`adaptive-`) both feed policy; a question about *why a decision was made*
  belongs with `overview-` until `policy-` has its own owner.
- **Delivery state incidents sit with `queue-`**, lease expiry, retry storms, spool pressure.
- **Pipeline wiring belongs to `assess-`.** If a component is not being called, or is called in the
  wrong order, that is the composition root, not the component.
- **Provider connector questions sit with `transport-`** once spawned; until then, `overview-`.
- **Anything about what the system *is*** (scope, boundaries, invariants) is `overview-`'s, and
  should be `send_message`d rather than patched into another agent's files.

## Structural decisions worth not re-litigating

1. **Evidence and action are different types.** `ISemanticMailClassifier` cannot return a
   `MailAction`. Enforced by the type system, and `adaptive-` asserts it by reflection over its own
   compiled surface rather than by convention.
2. **Core is dependency-free.** Everything references Core; Core references nothing. It holds
   contracts and the 12 semantic dimensions, and no I/O. `IMimeMessageAnalyzer` deliberately stays in
   `StyloMail.Mime` for this reason, moving it would drag MimeKit's shape into the centre.
3. **Queue owns its own durability contract.** The `250`-after-`DATA` ordering is a property of the
   queue, so its schema lives with it rather than in the shared Persistence project.
4. **Payload-before-metadata ordering.** A crash can leave a sweepable orphan payload, never
   metadata pointing at mail that does not exist. Orphan sweeps **require** an age cutoff: between
   the write and the commit an in-flight acceptance is indistinguishable from a true orphan.
5. **Credential mode is a connector property, not a Core property**, so "how many secrets does this
   deployment hold?" is answerable per tenant, and the default answer stays *zero*.
6. **Acceptance is a queue id, not a boolean.** `IsAccepted => QueueId is not null`, so no future
   edit can report success without naming the durable row that success is a claim about.
7. **The delivery worker never opens a socket.** It dispatches through an injected port, so
   `transport-` can implement delivery later without the queue learning SMTP.
8. **Observation and judgement stay in separate layers.** `AuthenticationResult` records what a
   trusted verifier *observed*; comparing a DKIM signing domain against the visible From domain is a
   *judgement*, and belongs with policy.
