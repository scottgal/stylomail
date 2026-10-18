<!-- Draft for review. Not for publication. -->

# The Relationship Is the Unit

## Communication as a test bed for adaptive inference

Most systems that judge email ask a question about a message. Is this a credential request? Is this
urgent? Does this attachment look dangerous? Those questions are useful, and the answers are hard to
get right. They are also the wrong unit for the thing that worries people most.

Three lines that are ordinary in isolation can be a pattern in sequence. A request for confirmation
is routine from a supplier you have paid for years and a change in behaviour from a colleague who has
never asked before. Urgency is appropriate in an incident and coercive in a personal thread. A warm
greeting is ordinary from a friend and a claim of identity from a correspondent you have never met.
The words matter less than the relationship they arrive inside, and the relationship is not a
property of any one message.

This project is usually described as an AI spam filter. The more interesting problem it exposed is
communication as a test bed for adaptive inference over evolving relationships. That is a direction,
not a shipped capability, and the distinction is the first thing this piece has to keep clear.

## What is built

The client access proxy is real and runs inside the Host process. It accepts IMAP retrieval and SMTP
submission from clients, authenticates with a password the proxy holds, and relays to the backend
under its own credential. It is one deployable with two faces: the same process terminates mail
protocols and serves the management API the console uses.

One harness run records both directions in a single place. In
`.styloagent/artifacts/proxy-e2e-20261018T125203Z`, an IMAP client authenticated, listed INBOX and
fetched one 168-byte body; an SMTP client completed STARTTLS, authenticated and submitted one message.
Both sessions carry the same opaque account id. Each direction produced one element point,
`urgency_pressure`, under the `elements/3` vocabulary and the `prototype_knn/teacher-urgency-1`
scorer. The byte counters are null rather than zero. The artifact is a harness run with real clients
and a containerised backend, not production traffic.

Around that data path the system already keeps per-sender and per-recipient profiles with drift,
velocity and acceleration over fixed buckets. It has a thread entity with a bounded window and a
change question. It records element signals for a segment, with per-element availability and a
reason when a value was not produced. It has a console that shows those observations with their
scope, window and availability, and that refuses to present a missing measurement as a zero.

It also has a deterministic policy engine, and that engine is the only place an action is chosen.

## What is not built

There is no group model. There are no transition expectations. No behavioural signal is read by any
policy tier. The adaptive layer observes and displays; it does not decide. A description of this
project that implies otherwise is wrong, including a flattering one.

The distinction is not modesty. It is the architecture that makes the direction researchable. A
relationship model can be wrong in interesting ways only if its wrongness cannot become an action by
accident.

## Three measurements

The honesty is the most persuasive part, so the claims in this piece are tied to artifacts and
numbers rather than adjectives.

The first is the two-way proof above. Retrieval and submission, one account, one measured element
per direction, a real body fetch and a real submission. It is one run, not a production sample, and
the manual records it as such.

The second is the pruning of the element vocabulary, which is a better story than a larger vocabulary
would have been. The layer began with five names. An independent 20-row check by a different model
lineage found two of them labelled on rows where the act was not present: the quoted phrases were
real, but they did not contain the act. `impersonated_greeting` and `attachment_lure` left at
`elements/2`. `credential_request` was kept as provisional and put through a pre-registered 150-row
check. It failed on both limbs: union agreement 0.413, with 44 teacher-only labels against 0 in the
other direction, and a permitted directional difference of 3. That is not a threshold question.
`threat` could not be labelled reproducibly either: a 5-row union in the 60-row check, 11
teacher-only against 2 verifier-only on the same-model pair, and 8 training positives. `elements/3`
contains `urgency_pressure` only.

One caveat keeps that result from reading as stronger than it is. The element layer currently decodes
a bounded copy from the raw wire message, headers and transfer encoding included, and splits only on
LF LF, so a CRLF message can become one header-laden fragment and later body text is not analysed
while segment coverage is still reported. The one measured element describes the analysed segments,
not yet the whole message. Segment correctness is under repair, and the evidence should be read that
way until a new run exists.

One measured element is not a failure of ambition. It is what the evidence supports, and a label that
another lineage cannot reproduce is not evidence. The same rule applies to every relationship
observation this direction proposes.

The third is the reliability soak. In review's controlled run at `b92d228`, eight concurrent clients
ran three ten-second rounds and completed 1649 healthy sessions. File descriptors moved from 97 at
baseline to 99, 100 and 100. Sockets stayed at 5. Session slots peaked at 8 and were 0 at every
settle. Managed memory moved from 1.54 MB to 2.78, 2.85 and 2.71 MB. RSS moved from 84.4 MB to 96.4,
79.5 and 79.8 MB. After disposal there were 96 descriptors, one socket and no managed children; all
four fixture ports rebounded. That is a bounded run with a clean settle, not an indefinite-run
clearance.

At the time of this measurement, six of nine controlled faults escaped without an appropriate named
outcome. A real connection refusal surfaced as `SocketException`; a connect stall and a missing
backend greeting surfaced as timeout or cancellation exceptions; an SMTP DATA reset surfaced as
`IOException`. Half-closes were already named. The named-outcome mapping is being repaired now;
treat those six as a dated finding, not a standing claim. The connect bound was repaired from about
7.8 seconds to about 373 milliseconds because the timeout had started after `OpenAsync` returned
rather than covering establishment. The instrument found what the product did not name, which is the
only reason the repair is credible.

The one percent false-positive budget for the measured element is not met and cannot yet be
resolved. The held-out negatives are below the number needed to resolve a one percent rate, and the
artifact marks the element `fpr_budget_resolvable: false`. Quoting the budget as satisfied would be
the same class of error as quoting a label the independent check rejected.

## Why relationships are a different unit

Everything in this section is proposed. None of it is shipped.

A message-level model asks whether this message resembles abuse. A relationship model asks what
changed between these people, in which direction, over what window, and how far that is from what the
relationship established. Four quantities become first class.

**Trajectory.** The same request repeated with increasing frequency is not the same event as the
first request. Velocity and acceleration over fixed buckets are crude, but they are observable,
bounded and available. The profile layer already records them. It does not yet interpret them as a
transition.

**Reciprocity.** Who answers whom, how quickly, and whether the register changes. A one-sided flow of
requests from an address that used to be a peer is a different observation from a busy but mutual
thread. Reciprocity needs both directions, which is why a proxy that reports only the inbound half is
reporting half a conversation.

**Group convergence.** Several relationships moving toward the same request at the same time is a
different shape from one relationship doing it. The existing system has no group model, and this is
where the largest research gap sits. A group is not a bigger sender profile; it is a pattern across
relationships, with its own baseline and its own failure modes.

**Deviation.** The valuable question is not whether a message matches a known script. It is how far
this relationship has moved from what it established, and in which direction. That makes the
baseline local and historical rather than a global catalogue. It also makes the baseline the most
sensitive object in the system, which is the first hazard below.

A transition is more informative than a state. A request that is ordinary for a supplier is a
transition when it comes from a colleague. Urgency is ordinary in an incident and a transition in a
quiet personal thread. A greeting is ordinary from a friend and a claim of identity from a new
correspondent. None of those statements is a verdict about a person. They are statements about a
relationship's movement, and they are exactly the kind of statement a policy engine could one day
consume as evidence.

## The invariant: evidence in, authority out

Probabilistic components produce evidence. Deterministic policy authorises side effects. The policy
engine is the only place an action is chosen, and no adaptive model acquires authority by being
accurate.

This is the invariant that makes an experimental relationship model safe to run in the same system
as real mail. A model can be wrong without the system acting on its wrongness. A dimension can be
unavailable without becoming a zero. An outage is never a verdict. A missing measurement is recorded
as `not_produced` with a reason, not smoothed into a confident answer.

The same separation appears in the small decisions. A tone score, when one exists, is evidence, not
a mood verdict. An element margin is evidence about a segment, not a judgement about a message. A behavioural
observation is a row with a scope, a window and an availability, not a hidden input to an action. The
policy engine sees the rows it is configured to weigh, and nothing acquires weight by existing.

The relationship direction does not change that rule. It makes the rule more important, because a
relationship model is the kind of system that becomes persuasive before it becomes correct.

## Hazard one: the baseline is the attack surface

An adaptive system can learn abuse as normal, slowly. That is not a hypothetical edge case; it is
the natural failure of any model whose definition of normal comes from the traffic it sees.

Three defences follow. Learning must be gated on a trusted boundary that a conversation cannot move
on its own. A profile that shifts sharply must raise its own uncertainty rather than silently
rebasing on the new behaviour. And feedback must remain evidence, not a training instruction: an
operator label is an observation about a message, not an order to move the detector. Candidate
exemplars are reviewed before promotion, with an actor and a time, because a single mistaken label
or a coordinated set of them can move a prototype set.

Poisoning and slow drift are the same failure at different speeds. The slow version is harder to see
and therefore more dangerous. A system that only tests whether today's model agrees with yesterday's
model will call a successful contamination "stable".

## Hazard two: privacy is linkage, not just content

Content-free is not non-identifying. Relationship graphs, stable pseudonymous identifiers,
embeddings and timing patterns can re-identify people even when no message body is stored. A graph
that says these two addresses talk every Tuesday at 09:00 is sensitive even if it contains no text.

The existing system already reduces some linkage. Login lookups use a tenant-keyed pseudonym rather
than a raw login. Profile keys are keyed hashes, not addresses. The management surface carries opaque
account ids and no content. Tenant boundaries are part of the lookup, not a filter applied afterward.
None of that is a claim of anonymity. The honest claim is bounded linkage: the system should collect
the minimum graph it needs, keep it tenant-scoped, retain it for a stated period, and treat a new
linkage as a decision rather than a free by-product of storage.

For a relationship model, the graph would be the product, so the privacy question is architectural.
A group model that is useful would also be a map of who knows whom. That map needs an owner, a purpose and a
retention rule before it needs a better algorithm.

## No conclusions about intent

A relationship model may say that a pattern is present and that a relationship moved. It may not say
that a person intended it.

For grooming, coercion and coordinated pressure, the distance between "this pattern is present" and
"this person intended it" is the whole safety property. A model that collapses that distance is not
more useful; it is less safe, because it turns a statistical observation into an accusation. The
language in the console and in any report matters. "The pattern is consistent with escalating
pressure" is a statement about evidence. "This person is pressuring" is a statement about a person,
and it is not one the system is entitled to make. A relationship model would be held to the same
limit.

Policy can act on a message without making that leap. The action is about what the system will do
with a message, not about what a person is.

## A modest progression

There is a family resemblance between three pieces of work: Stylo.Bot's behavioural sequences,
adaptive extraction of document expectations, and now expectations about communication. Each treats
a sequence as the unit and asks what changed relative to what came before.

The resemblance is a design instinct, not a proven shared mechanism. The substrates differ: bot
behaviour, document structure, interpersonal communication. What transfers is the discipline of
separating an observation from an action, not a model that can be moved between them unchanged. The
progression is worth naming because it explains why this direction is plausible. It is not evidence
that the direction has been built.

## What would count as progress

The direction becomes research rather than speculation when it can fail in public.

A relationship model needs a pre-registered criterion before the check, the way the credential test
did. It needs independent lineage checks on labels, because a label another lineage cannot reproduce
is not evidence. It needs longitudinal splits by relationship, not random splits by message, or it
will learn the people rather than the pattern. It needs baseline-contamination tests and poisoning
controls, with a stated trusted boundary. It needs group-convergence tests on synthetic data and on
consented real data, with the consent treated as a first-class constraint rather than a footnote. It
needs a false-positive budget with named hard-negative counts, and it needs a message-only baseline
to beat. If a relationship signal does not add anything over the message-level model, the honest
result is that the unit change did not help on this data.

The hard part is data and consent, not the model. Three innocuous lines are easy to generate and hard
to label. A relationship trajectory is easy to draw and hard to validate. The system's existing
discipline, evidence in and authority out, is what makes it safe to collect the evidence while the
validation is still incomplete.

## Closing

The unit change is the contribution: from judging a message to observing a relationship. The safety
separation is what makes the change researchable: probabilistic components produce evidence, and
only deterministic policy authorises an action. The measurements are what keep it honest: one
measured element after the rest failed reproduction, one two-way proxy proof, one bounded soak with
six faults the product could not name at the time of the measurement.

This is a direction, not a product. The interesting part is that the direction can be wrong in
public without the system acting on its wrongness. That is the property worth building on.
