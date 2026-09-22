**From:** overview-
**Timestamp:** 2026-09-22T21:27:37.1628760+01:00
**Priority:** normal

# StyloMail blog article technical guidance

Technical guidance, and the first thing is the one that matters most for an article: **the line between what is built and what is planned.** This codebase has spent the day finding places where a document claimed something the code did not do, and an article that repeats an aspiration as a fact is the same defect in a more public place.

## What StyloMail is

Three commitments everything follows from, and they are the article's spine:
1. **Probabilistic components produce evidence; only deterministic policy authorises side effects.** The classifier cannot return an action, and that is enforced by the type system rather than by convention.
2. **Unknown is a distinct state, never a zero.** An unavailable signal says so; it is not a calm result.
3. **Intervene minimally.** Thin evidence yields a bounded hold, not an irreversible rejection.

The framing is "detects suspicious *communication* rather than suspicious words", and the deliberate vocabulary is **post-Bayesian** (beyond token frequency) and **post-LLM** (structured classification rather than generation). Say plainly that post-LLM is not a claim that errors or adversarial inputs disappear.

## Jev's role, and the pipeline around it

Jev is a **decision layer, not a parser, evidence generator or renderer**. From `src/StyloMail.Jev`:
- One fan-out request to TypeSafe System One carrying bounded state and the questions to answer.
- **Twelve typed semantic dimensions** (credential request, payment redirection, urgency, link lure, and so on), each independently scored and bounded. See `SemanticDimension` in `src/StyloMail.Core`.
- It **cannot return arbitrary strings** and it has a real **abstention path**.
- A pinned model id, and an explicit `Unavailable` state on failure rather than a zero.
- Results are cached, and the **cache key includes the behavioural profile** so two identical messages from different senders cannot share an assessment.

The surrounding pipeline (`src/StyloMail.Assessment/AssessmentPipeline.cs`, spec §5): validate and bound → parse an analysis copy and extract deterministic evidence → read profile snapshots and reserve rate counters → obtain semantic evidence (cache, provider, or explicit unavailable) → compare against profiles and campaign windows → run versioned deterministic policy → persist the decision.

## What is actually built, and what is not

**Built and tested (1,465 tests, twelve source projects):** the MIME adapter and deterministic evidence; the Jev adapter; the adaptive engine (profiles, drift, velocity, acceleration over fixed clock buckets, recipient history); the policy engine; the durable queue and spool with the 250-after-DATA contract; the delivery worker; the HTTP host and CLI; the operator console; the SMTP/MTA transport and provider connectors; the client access proxy; the live-event hub; a real-socket protocol test harness.

**Built today, and worth an article's honesty:** a **chat channel** beside email (Slack ingress, durable intake, assessment path, triage in progress). It is **built, not proven**: nothing in it has been exercised against a real Slack workspace, so its claims about Slack's payloads are claims about the documentation.

**Not built, and do not imply otherwise:** Discord; the semantic path for chat (disabled by design, local-only by default for privacy); triage actions (nothing acts yet, deliberately); AccessProxy hosting and its durable credential store.

**Worth a sentence, because it is the most interesting thing that happened:** the **emergency kill switch** was specified and read by the policy engine, but wired to nothing, so it was false on every assessment in every deployment and no operator could engage it. It was fixed today. That is a good real example of a control that looks present because a spec names it.

## A concrete example that shows the design

A compromised mailbox does not start sending obvious spam; it sends a plausible invoice request to twelve new recipients in four minutes. No word is suspicious. What the system has that a content filter does not: the sender's own history (this account has never addressed these people), the rate against its baseline, and the semantic shape of the message read as typed evidence. The **fan-out** signal is the one worth the article: distinct recipients, novelty and velocity, all computed locally and free.

## Caveats an article should carry

- **Acceleration is noisy evidence and can never alone justify a hard block.** The engine suppresses derivative evidence during cold start, long gaps, sparse buckets and regime changes.
- **A familiar sender is context, not a permanent bypass.**
- **DKIM/SPF success is not a benign verdict**, because a compromised authorised account authenticates perfectly.
- **The semantic provider is optional.** A tenant that forbids external content processing gets an explicit *semantic-unavailable* state rather than a silent gap.
- **The aggregate risk index is a documented index, not a calibrated probability**, and must not be reported as one.

## Repository, and a real constraint for publishing

Source is `github.com/scottgal/stylomail` and it is **private**, so any link you put in an article will not resolve for a reader. Also **no LICENSE has been chosen**, which matters before publishing anything that points at the code. Raise both with the operator rather than writing around them.

Documents worth reading: `.styloagent/spec.md` (the identity, and §13 for chat), `docs/chat-channels-design.md`, `docs/running.md` (the operator-facing account of the executable, verified against a running process), and `.styloagent/architecture.md` (the component view).

**Send me the draft's factual claims and I will check them against the tree.** That is the review that matters: not the prose, the accuracy.
