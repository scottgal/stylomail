# Chat channels: Slack first, Discord after

Design for extending StyloMail from an email security component to a communication security
component with two channel families. Agreed with the operator 2026-09-22.

Status: **designed, not built.** Nothing here exists yet. This document is the record of what is
being proposed and the reasoning behind each decision, so that the reasoning can be argued with
before there is code to argue with instead.

## Three jobs, all confirmed by the operator

| Job | What it catches | Which evidence it leans on |
| --- | --- | --- |
| **Protect members from inbound** | External DMs, phishing, social engineering reaching a member | Semantic, plus link analysis |
| **Catch compromised accounts** | A member's own account fanning out to strangers, or drifting from its baseline | Behavioural, local and free |
| **Cut channel noise** | Marketing blasts and low-quality repetition in public channels | Near-duplicate and campaign detection, plus local rates |

The second job is the one the existing codebase already supports almost entirely. `RecipientHistory`
and the recipient bloom filter answer "is this account suddenly talking to people it never talks to"
without a network call, and they landed for email on the same day this design was written.

## The one fact that decides this design

**Email lets you decide before delivery. Slack does not.**

StyloMail's durability boundary ("a `250` after `DATA` transfers delivery responsibility") and its
action set (Allow, Hold, Quarantine, Defer, Reject) all assume the system sits in the delivery path
and can decline responsibility before the recipient sees anything.

A normal Slack app receives a `message` event **after** Slack has delivered the message. There is no
point at which the message is held. So:

- **The honest name for this is an observer with post-hoc interventions**, not a proxy. Saying
  otherwise would be a claim about the system that is not true.
- **`DeliveryTiming` becomes a recorded state**: `PreAcceptance` for email, `PostDelivery` for chat.
  It is recorded on every assessment, never inferred at read time, so nobody can render a post-hoc
  record as a prevention.
- **Anything claiming pre-delivery requires a different product.** If the bot is the only thing that
  can post, you own the client. That is not this.

Two consequences fall out of it and both are load-bearing:

**There is no queue and no delivery worker for chat.** The queue exists to own the durability
contract for something we then deliver. Chat has no delivery responsibility, so the queue's role does
not transfer, and neither does the spool, the lease, the retry schedule, or the orphan sweeper. A
chat pipeline that reached for the queue would be importing a solution to a problem it does not have.

**The intervention set is smaller and every entry is reversible or visible.** That is a real loss of
capability against email, and it is better stated than discovered.

## What transfers, and what does not

Reusable unchanged: `Evidence`, `SemanticDimension`, `BehaviouralProfile`, the policy engine, the
decision ledger, the operator console, the Host's authenticated HTTP surface, the semantic cache and
its campaign windows, and the observed-state versus trusted-baseline split.

Transfers partly: **link and homograph analysis** from the MIME adapter applies directly, because a
link lure is a link lure. MIME structure and DKIM/SPF do not apply at all.

Does not transfer: envelope validation, MIME parsing, byte preservation, the `Received:` rules, and
everything in the durability boundary.

**Slack authenticates the member, not the message.** A compromised member account is authenticated
exactly like a legitimate one, which is the same problem the spec already names for email: "DKIM/SPF
success is not a benign verdict". So membership is context and never a verdict, and no chat
authentication result may be treated as evidence of good intent.

## Components

### Chat connector, new project

Platform-specific and nothing else. Slack first, through SlackNet, which is the mature .NET library
and covers the Web API, the Events API and Socket Mode. Discord later, through Discord.Net or the
newer NetCord; the gateway model there is a friendlier fit for real time and bots may delete far more
freely, but the connector interface should be proven against one platform rather than designed for
two at once.

The connector owns exactly four things: **signature verification** of the inbound request, **event
deduplication**, because Slack retries and a retried event must not be assessed twice, **rate-limit
handling** in both directions, and **normalisation** into the channel-neutral input. It makes no
judgement about content. Anything that decides is somewhere else.

### Triage, new component

This is the algorithmic layer. It runs **first, always**, on every message, before anything expensive
or anything that leaves the machine. It answers one of three things:

- **Dismiss**: clearly fine, stop. The overwhelming majority of channel traffic.
- **Decide locally**: cheap evidence settles it without the semantic path.
- **Escalate**: this warrants the full pipeline.

Checks, in ascending cost, stopping at the first that settles it:

1. Is this a channel, workspace and thread this deployment watches at all?
2. Is it a near-duplicate of something in the recent campaign window?
3. Link and homograph inspection, reusing the MIME adapter's analysis.
4. The author's behavioural profile: recipient fan-out, novelty, velocity and drift. This is local
   and free, and it is the check that earns its keep for job two.
5. Otherwise escalate to semantic evidence.

**Triage emits evidence and a disposition. It never emits a spam score.** This project is deliberately
post-Bayesian and does not produce "is spam" verdicts, and a numeric score invented here would be the
first step toward the vocabulary drifting back to what this system exists to replace. A dimension with
no support is `Unavailable`, never zero.

## Core additions, all additive

No `Mail*` type is renamed. The email path is not touched.

- **`ChannelKind`**: `Email`, `Slack`, `Discord`.
- **`ChannelContext`**: workspace, channel, thread position, and the membership evidence, kept
  separate from message content the same way `TaggedContext` is today.
- **`DeliveryTiming`** on the assessment: `PreAcceptance` or `PostDelivery`.
- **A chat deterministic-evidence producer** as a sibling of the MIME analyzer. `Evidence` is already
  the shared currency, so this is a new producer rather than a change to the existing one.

The seam already exists. What varies between channels is who produces `Evidence` and what the
envelope means, and both are already pluggable in shape if not in name.

## Interventions

Every action is post-hoc. Defaults are chosen so that a deployment which configures nothing takes no
irreversible action.

| Action | Default | Why |
| --- | --- | --- |
| Observe only, record the proposed action | **on** | Shadow mode already exists for email. Phase one is read-only. |
| Warn the recipient | **on** | Non-destructive, and it is the minimal intervention. |
| Flag for review in the console | **on** | Uses the existing ledger and the existing review surface. |
| Delete or redact the message | **off**, opt-in per workspace | Destructive, irreversible, and visible to the whole channel. |
| Restrict the member's account | **off**, opt-in | A side effect on a person, taken on evidence that is probabilistic. |

Two rules hold this together. **No destructive action without explicit per-workspace configuration,
and every action is in the ledger with the evidence that produced it.** And **the console shows
`deliveryTiming` on every chat decision**, so an operator reading the record is never left to infer
whether the system could have stopped something, when it could not.

## Cost and privacy

The economics differ from email by orders of magnitude. A workspace produces far more messages than a
mailbox, and most of it is benign conversation. Two decisions follow.

**Jev is opt-in per workspace, defaulting to local-only with an explicit semantic-unavailable state.**
This is the spec's existing privacy stance, and it matters more here, because these are internal
conversations rather than inbound mail. A workspace whose operator has not agreed to send content to a
hosted classifier gets every local check and an honestly labelled gap where the semantic one would be.

**Bounded spend is a feature, not tuning.** Per-workspace message rate caps, bounded triage
cardinality, and a bounded semantic budget per window, all three enforced rather than hoped for. The
spec already makes this a core feature for email; chat raises the stakes rather than changing the rule.

**Retention defaults shorter than email, and raw content is kept only for what escalated.** Chat
messages are internal and frequently sensitive; retaining every one of them so that a later question
can be answered is a cost the operator did not ask for.

## Persistence

Observed state gains a channel discriminator, and the ledger records chat decisions exactly as it
records email decisions, because the ledger is the explainability surface and it should not have two
shapes. There is no spool and no queue row.

## Testing

The connector is tested against recorded event payloads for signature verification, deduplication of
a retried event, and normalisation. Triage is table-driven, and includes cases that must **not**
escalate, because a triage layer nobody has proven to stay quiet is just a slower pipeline. A test
asserts that every chat assessment carries `PostDelivery`, so the post-hoc rule cannot be quietly
broken by a later edit. The UI harness pattern in `ux-scripts/` extends to chat the same way it
covers the console.

## Deferred, with reasons

- **Discord**, until the connector interface has been proven against Slack. Designing for two
  platforms at once is how an interface ends up being the union of two rather than an abstraction of
  one.
- **Pre-delivery interception**, which is not buildable as a normal Slack app.
- **Cross-workspace correlation**, which is a different design with its own privacy analysis.
- **Attachment and media analysis**, because the spec already forbids fetching content, and it would
  need its own sandboxed service before it is even discussable.

## Open risks

1. **Triage may not cut enough volume** for the semantic path to stay affordable, in which case the
   default flips from escalate-on-ambiguity to escalate-on-strong-signal, and the recall cost is real.
2. **Privacy is a larger step here than for email**, because internal conversations are not the same
   thing as inbound mail. Defaulting to local-only is the mitigation, and the per-workspace sign-off
   should be explicit rather than implied by installing the app.
3. **Scope creep toward an autonomous incident-response agent**, which the spec explicitly excludes.
   The line held here is that the system produces evidence and flags for review, and destructive
   actions are opt-in and audited. That line is worth re-stating every time an action is added.
4. **Slack's app distribution model** affects rate limits and review, and is not yet decided.

## Sequencing

1. **This document and the spec section it implies.** No code.
2. **Core additions**: `ChannelKind`, `ChannelContext`, `DeliveryTiming`. Small and additive.
3. **Slack connector, read-only**: ingest, normalise, triage, assess, record in the ledger, take no
   action. Observe-only is a complete and useful slice, and it proves the vocabulary before anything
   destructive exists.
4. **Triage**: the checks above, cheapest first, all bounded.
5. **Console surface** for chat traffic and its decisions, including `deliveryTiming`.
6. **Interventions**, one at a time, non-destructive first, each behind explicit configuration.
7. **Discord**, once the connector interface has been earned by Slack.
