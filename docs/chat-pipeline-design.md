# The chat pipeline: local-only first

Decision record for plan 2b. Settles the question plan 2a was deliberately written to avoid: how a
Slack message reaches the assessment pipeline when it has no mail envelope.

Status: **decided, not built.** `docs/chat-channels-design.md` remains the design of record for the
extension; this narrows the pipeline half of it to something buildable now.

## The decision

**Chat assessments run without semantic evidence, explicitly, and say so.**

The design already reached this conclusion from the privacy side: Jev is opt-in per workspace and
defaults to local-only, because internal conversations are a larger privacy step than inbound mail. I
am taking that default as the first implementation rather than as a state to fall back to, which is
what makes this plan small.

**The evidence available locally is not thin.** Deterministic link and homograph analysis transfers
from the MIME adapter directly, and the behavioural engine already answers the question that matters
most for a compromised account: is this member suddenly talking to people it never talks to. The
semantic classifier adds inbound social-engineering detection, which is one of three jobs and the
only one that has to wait.

**No workspace gets a semantic path until an operator asks for one**, at which point the input
question has to be answered. That question is now written down here so it is not rediscovered: the
classifier's view of its input is nine members (`Coverage`, `ConversationContext`, `Authentication`,
`Subject`, `QuotedText`, `Links`, `Envelope`, `BodyText`, `Attachments`), and generalising it is a
change to a contract with its own tests, not a detail of adding a channel.

## The principle this settles

**Input is per channel. Output is shared.**

What a channel can tell you differs: email has an envelope, DKIM and attachments, and Slack has a
workspace, a channel, a thread and a platform that authenticated the member rather than the message.
So the *input* is per channel and each channel has its own record.

What the system decides is the same shape either way: a decision about a message, with evidence,
policy and an audit trail. So the *output* is `MailAssessment`, which is already channel-aware: plan
1 gave it `DeliveryTiming`, and this plan adds the `ChannelContext` so a console can show which
channel a decision came from without reading the input.

The record keeps its `Mail` name, which is now a legacy wart rather than a description. Renaming it
touches every lane for vocabulary rather than behaviour, and the design already ruled that out. It is
recorded here so the name is understood as history rather than as a claim.

## What is pinned by a test rather than by a comment

**Every chat assessment carries `DeliveryTiming.PostDelivery`.** A chat assessment that claimed
`PreAcceptance` would tell an operator the system could have stopped a message it only reacted to,
which is the one false statement this extension must never make. The test is not optional.

**The semantic gap is an explicit `Unavailable` with a reason**, never an absent evidence entry and
never a zero. `Unknown is a distinct state` applies with full force here: a chat verdict reached
without the classifier must be legible as an uninformed judgement, not as a confident one.

**Nothing takes an action.** Observe only. The design puts destructive actions behind explicit
per-workspace configuration, and this plan is the one that produces an audit trail to make that
decision on.

## Tasks

Ordered, tests first, in the fleet's normal way. The executor writes the tests before the code and
reports a frozen tree.

1. **`ChatAnalysisInput` in Core**, carrying `ChannelContext`, `BodyText`, `Links`,
   `ConversationContext`, and the platform's bounded membership facts. Plus `MailAssessment.Channel`,
   required, so the decision records the channel it is about. Existing email call sites state
   `ChannelContext.Email`, exactly as they did for `MailAnalysisInput.Channel` in plan 1.
2. **A deterministic evidence producer for chat**, producing `Evidence` from a `ChatAnalysisInput`.
   The input record is what Task 1 added for Tasks 2 and 3 to share, so a producer reading the raw
   `ChatMessage` would leave that record unused and rebuild the link observations itself. Link
   and homograph analysis comes from the MIME adapter and must be reused rather than reimplemented;
   if it is not reachable without dragging MIME structure along, report that as a finding rather than
   copying it.
3. **The chat assessment path in the composition root**, running the deterministic producer, the
   adaptive engine's recipient and velocity evidence, and policy, and producing a `MailAssessment`
   with `DeliveryTiming.PostDelivery` and an explicit semantic-unavailable entry.
4. **The Slack events endpoint in the Host**: the `url_verification` challenge handshake, signature
   verification through `slack-`'s verifier, deduplication through its retry guard, normalisation, and
   a ledgered decision. It answers Slack quickly and does the assessment off the request path, because
   a slow response turns into a retry, and a retry is traffic we did not see.
5. **The tests that make the decisions above real**: an unsigned request is refused, a replayed one is
   refused, a message the system posted itself is not assessed, a chat decision records
   `PostDelivery`, and a decision reached without the classifier says so in its evidence.

## What this does not do

- **No actions.** No warn, no delete, no restriction.
- **No semantic path.** Deferred, with the input question recorded above rather than answered.
- **No triage.** The algorithmic layer is plan 3, and this pipeline is what it will sit in front of.
- **No Discord.** The input record is shaped by one platform first.
