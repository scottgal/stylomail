# Triage: the algorithmic layer

Decision record for plan 3. `docs/chat-channels-design.md`'s triage section is the starting point and
is deliberately thin. It fixes three things: triage answers **dismiss**, **decide locally** or
**escalate**; it emits evidence and **never a score**; and it runs **first, always**, before anything
expensive or anything that leaves the machine. It does not say what the checks are, in what order,
with what bounds, or what happens when each is wrong. This document answers those, and nothing here is
built yet.

Status: **proposed, for review.** No code until this is agreed.

## The decision this record makes

**Every check is specified by which way it fails, not by what it detects.**

That is the shape of the whole design. Each check has two error directions and they are never equally
costly: **dismissing something that should have been looked at is a missed detection**, and
**escalating something that did not need it is money**. For each check one of those is the safer error,
and the check is built to fail that way. A check that does not say which way it fails is a check
nobody has decided the cost of.

The asymmetry is what makes triage a design problem rather than a filter. A filter that dismisses
aggressively has a good average and a catastrophic tail, and the tail is the job.

## What triage is not

- **Not a score.** The design already rules this out and this record holds the line. Triage emits
  `Evidence` and a disposition. A numeric "spamminess" is the first step back toward the vocabulary
  this system exists to replace.
- **Not a decision about content.** Every check is about shape, cost or the author's own history. The
  one check that reads content at all reuses the link and homograph analysis that already exists.
- **Not load-bearing for the semantic path today.** See the escalation section: chat is local-only,
  so escalation currently has nowhere to go.
- **No dimension with no support is ever zero.** Unavailable is a distinct state here as everywhere
  else, and a check that did not run says so rather than reporting a calm result.

## The stopping rule

Checks run in ascending cost and **stop at the first that settles the message**. "Settles" means the
check produced a disposition, not that it found something. A check that runs and finds nothing is a
settled message only where the absence is itself informative, and each check below says whether it is.

Ordering matters more than usual because a cheap check that stops early is a check whose error is
never corrected by the ones behind it.

## The checks

### 1. Is this a scope we watch at all?

**The question.** Is this workspace, channel and thread one this deployment was configured to look at?

**Cost.** Configuration lookup. Free.

**Bound.** A configured set, bounded by cardinality like every other configured list here.

**Which way it fails.** Dismissing something we should have watched is a **missed detection**, and it
is total: nothing downstream ever sees the message. Escalating something outside our scope costs a
little traversal.

**Safer error: escalate.** But the reason is not that escalating is cheap. It is that **this check is
configuration, not judgement**, and a misconfiguration is an operator error rather than a triage
error. What makes that bearable is that it must be **visible**: the count of messages dismissed on
scope is reported, so an operator can see what is being ignored rather than having to discover it. A
silent scope filter is the failure mode here, and it is worse than a wrong one.

**This is the check I would expect to be wrong first.** It is the one whose correctness depends on
someone having configured something correctly months earlier, and nothing about the traffic will
reveal the mistake.

### 2. Is this a near-duplicate of something already seen?

**The question.** Does this resemble something in the recent campaign window closely enough that it
has, in effect, already been assessed?

**Cost.** Comparison against a bounded window. Cheap.

**Bound.** The window's capacity and retention, already fixed by the design.

**Which way it fails.** Dismissing something that is not really a duplicate is a **missed detection**,
and it is the interesting one: **repetition is how an attack hides.** Fifty near-identical messages
followed by a fifty-first that differs precisely where it matters is the pattern this check could be
talked into dismissing. Escalating a true duplicate pays for an assessment we have already made.

**Safer error: escalate, and by a wide margin.** The cost of a duplicate assessment is bounded and
recoverable; the cost of the fifty-first message is the one this extension exists to prevent. **So
this check dismisses only on very high similarity**, the threshold is set so that dismissal is the
rare outcome rather than the common one, and the design should say plainly that a nagging
near-duplicate is the intended behaviour rather than a tuning failure.

**This is the check I am most likely to have the threshold wrong on**, and it is the one where being
wrong in the aggressive direction is unrecoverable.

### 3. Links and homographs

**The question.** Does the message contain a link, and does what it says match where it goes?

**Cost.** The shared analysis, no network, bounded by link count. Cheap.

**Bound.** A maximum number of links considered per message, and a maximum length per link, both
already fixed in the shared analysis.

**Which way it fails.** Missing a lure is a **missed detection**. Escalating a message with a
harmless link pays for the next check.

**Which way it fails: missing a lure is a missed detection, and that is the only error that matters
here.** Escalating a message with a harmless link pays for the next check.

**This check has no dismiss disposition at all, and that is the consequence of the asymmetry rather
than a separate decision.** Its three outcomes are:

- **A lure escalates.** A display text that disagrees with its destination, or a host that reads as
  one it is not, is exactly the signal this check exists for.
- **Clean links continue to check 4.** Not dismiss, because clean is not informative enough to settle:
  a message with an honest link can still be a compromised account, and check 4 is what sees that.
  "Links that are all clean" is a finding that **stops nothing**.
- **No dismiss, ever.** The stopping rule says a check that finds nothing settles a message only where
  the absence is itself informative, and here it is not. The absence of a lure rules one thing out and
  nothing more.

**An earlier version of this section said "safer error: escalate" as though it were a disposition.**
That phrasing is a failure direction, not an outcome, and read literally it produces a check that
escalates every message carrying a link, which would make check 4 nearly unreachable and spend the
expensive path on the most common thing in a channel.

**Worth stating:** this check produces evidence either way, so "no links" and "links that are all
clean" stay distinguishable. The second is a finding; the first is the absence of a question.

### 4. The author's own behaviour

**The question.** Does this author's history make this message ordinary or extraordinary? Recipient
fan-out, novelty, velocity and drift, all local and free.

**Cost.** Profile reads. This is the first check that is not free, and it is the one that decides
whether the lane is affordable.

**Bound.** A maximum number of profiles read per message, following the mail path's rule that a
recipient count is attacker-controlled.

**Which way it fails.** Deciding "ordinary" about a compromised account is a **missed detection**, and
it is job two failing. Deciding "extraordinary" about a genuinely new but innocent pattern is noise.

**Safer error: this is the one where the answer is not obvious, and it is because nothing acts yet.**
In observe-only, a false "extraordinary" costs an operator's attention and nothing else. The moment an
action exists, it costs a person's account being restricted on probabilistic evidence, which the
design's interventions table already says must be opt-in for exactly this reason.

**So this check's safer error is a function of what the deployment has enabled**, and that is the
design: it fails toward escalation when nothing acts, and its escalation must be re-examined at the
point interventions are added rather than assumed to hold.

**And this is the check the whole plan exists for.** It is the one that earns its keep for job two,
and it is the one that makes the point that the expensive path is not always the useful one.

### 5. Escalate

**The question.** None. This is what happens when nothing above settled the message.

**Cost.** The full path. **This is the budget line**, and the per-window semantic ceiling is enforced
here rather than hoped for.

**Which way it fails.** Escalating too much is **money**. Escalating too little is a missed detection,
which is the same asymmetry as everywhere else, and it is why the design's default is
escalate-on-ambiguity rather than escalate-on-strong-signal.

**Safer error: escalate**, with the ceiling as the bound rather than the judgement. When the budget is
exhausted the honest outcome is to say so in the evidence, not to silently dismiss.

## The thing this record has to settle before anything else: escalation goes nowhere

**Chat is local-only.** Plan 2b settled that a workspace gets no semantic path until an operator asks
for one, and the chat assessor records twelve semantic dimensions as `Unavailable` with a reason on
every message it sees. So check 5, "otherwise escalate to semantic evidence", currently escalates to
something that does not exist for chat.

**Two readings, and I recommend the first.**

1. **Escalation is to the full local assessment**, which is what the drain currently does for every
   message. Triage's job is then to keep the majority of traffic out of that assessment, and
   "escalate" means "spend the profile reads and the policy composition on this one". When a workspace
   later opts into the semantic path, escalation is what consumes it, with no change to the checks.
2. **Escalation is to the semantic path specifically**, and with no semantic path every message above
   the cheap checks is a special case with nowhere to go. That makes the current build a stub of
   itself and gives a workspace with no semantic path a triage layer that cannot escalate anything.

The first reading makes triage useful today and correct tomorrow. **It also means triage is what makes
the drain affordable**, which is the argument for building it before interventions.

## The rule this record nearly got wrong: dismissing must not starve the record

**Triage runs before the assessment, and the observed-state write lives inside the assessment.** Left
there, a member who posts fifty near-duplicates that check 2 dismisses contributes **nothing** to
their own profile, and check 4 then computes velocity, drift and fan-out from a history that the
checks in front of it have been quietly thinning. **That is check 4 starved by the checks it is
behind.**

The mail path already lives by the opposite rule: observed state counts **attempts**, not deliveries,
and it is written for every message the pipeline saw. Triage skipping the assessment must not skip the
record that the author posted.

**So the write and the assessment are separable, and triage separates them.** The observation is
recorded for every message that reaches an assessment check, whether or not that check then stops the
message. **Which checks record, per check**, on the same terms as everything else here:

| Check | Records? | Why |
| --- | --- | --- |
| 1, scope | **No** | An out-of-scope channel is one this deployment decided not to look at. Its messages being invisible to the profile is the decision being carried out rather than a loss, and recording them would put traffic we have chosen to ignore into the baseline we judge by. |
| 2, near-duplicate | **Yes** | The dismissal is about cost, not about the message being uninteresting. A member posting fifty near-identicals is exactly the behaviour check 4 measures. |
| 3, links | **Yes** | Same reason. A link that turns out to be clean is still a message the author sent. |
| 4, behaviour | **Yes** | It is the assessment path, reaching it is what records. |
| 5, escalate | **Yes** | Same. |

**Two consequences, both of which belong in the record rather than being left for the reader.**

**It shrinks the saving honestly.** Every message that reaches check 2 needs a write regardless, so
triage's benefit is the **assessment minus the write**, not the whole path. The write measured at 0.07 ms
and the assessment at 1.3 ms, so the saving is real but it is not the whole cost.

**And it bounds the counts.** The dismissal counts are counts of what triage did to messages that were
**still recorded**. The count of messages that were never recorded at all must either be zero or named
as a number, and check 1 is the only check that can produce one.

## What triage emits

- **Evidence**, in the existing shape, from whichever checks ran.
- **A disposition**: dismiss, decide locally, or escalate. An enum, not a number.
- **The checks that did not run**, explicitly. A message dismissed at check 1 must not read as one
  that passed checks 2 through 4, and a message whose links were never inspected must say so rather
  than reporting no links.

## Open questions I am not deciding alone

1. **Does a dismissed message reach the ledger?** Recording every dismissal would defeat the point of
   dismissing, and not recording means an operator cannot ask why a message was ignored. My
   recommendation: **counts per check and per disposition on the operator surface, no ledger row per
   dismissal**, with the counts being what makes a wrong scope filter visible. But this trades
   explainability for volume, and that is the operator's call rather than mine.
2. **Where does triage run?** I would put it in the drain, before the assessor, because the assessor
   is the expensive thing triage exists to avoid. That does mean a dismissed message never becomes a
   `MailAssessment` at all, which is what makes question 1 load-bearing.
3. **What is the near-duplicate threshold, and who sets it?** I would start conservative (dismiss
   rarely) and require evidence before loosening, but the value is a judgement about this
   deployment's traffic rather than a fact.

## Risks

- **Triage can become a way to look at less**, which is the opposite of what it is for. Its dismissal
  rate is a number an operator should be able to read, and a dismissal rate climbing without a
  matching explanation is the signal that a check has drifted.
- **The local path is measured and it is not the bottleneck.** Per message: intake write **0.07 ms**,
  full assessment **1.35 ms** against an empty profile store and **1.30 ms** against one seeded with
  40 observations per author, ledger record **0.19 ms**. Drain throughput **about 1000 events/s**.
  Caveats, which travel with the numbers: 300 messages is a small sample, the seeded history is 40
  observations per author rather than a year of one, and the semantic path is **unmeasured because it
  is disabled**.
- **So the justification is the semantic path, not the local one.** The local cost is milliseconds at
  any volume a workspace produces, and an argument that triage exists to make the local path
  affordable is not supported by these figures. What triage's escalation ceiling actually bounds is a
  **per-call cost that has not been incurred yet**, and that is the honest reason it comes first.
- **A local-only deployment's triage decides what reaches an assessment that can only look locally.**
  That is coherent, but the escalation ceiling then bounds nothing external, and the plan should not
  claim a financial benefit it cannot demonstrate for that configuration. What it does buy there is
  the discipline of the checks themselves.
