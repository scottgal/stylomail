# Desktop moderator usability audit

## Persona

The primary reader is an email administrator moderating mail between known
vendors and customers. They need to make a careful, reversible-as-possible
business decision quickly; they are not expected to understand a policy engine,
risk arithmetic, or evidence taxonomy.

## The five jobs

1. Find email that needs a decision and tell whether it is still current.
2. Understand, in one sentence, why it was stopped and whether the information
   is complete enough to act on.
3. Read the relevant conversation and recent history before releasing or
   holding an item.
4. Release an email, keep it held, or stop a sender, knowing the consequence
   and who is affected.
5. Recover from an unavailable read or measurement without treating absence as
   a benign value.

## Where the current console fails

- The primary navigation exposes internal queue states (`Awaiting decision`,
  `Held`, `Quarantined`) and HTTP routes. The message list repeats raw state
  values and opaque queue identifiers, so it does not immediately answer
  “what needs me?”
- A held-list row has no plain-language explanation. The available message
  listing contract contains recipients, state and delivery attempts only; it
  does not carry a decision reason or a decision id. The existing detail
  screen is therefore not reliably reachable from that row. A per-message
  “why it was stopped” summary needs a Host join/read route; Desktop must not
  invent one.
- The decision screen begins with risk indexes, trends, evidence, policy
  reasons and dimensions. It has useful data, but the most important answer is
  buried under analyst vocabulary rather than being presented as a one-line
  human explanation with detail on demand.
- Release is a generic button, while sender controls say `Pause` and `Resume`.
  The existing confirmation does state consequences, but the initial controls
  neither say whom they affect nor use the administrator’s language. There is
  no current route for “keep held” or “ask someone else”; those must remain
  named gaps rather than simulated actions.
- Slack decisions can load assessed conversation history. Email has no current
  relationship/history route, and the message listing does not expose a
  vendor/customer relationship. The console cannot truthfully show this
  context for ordinary email today.
- Read failures are generally distinguished from empty results, and missing
  evidence is not rendered as zero, which is good. But several empty-state
  explanations are written for operators (“Host”, “semantic provider”,
  “bounded re-evaluation”) rather than telling the administrator the next
  practical step. Colour is supplemented by text in status and result bars,
  but the primary action labels still need clearer text.

## Contract gaps to hand to ingress-

- A read that joins a listed held/quarantined message to its assessment summary
  (or carries a safe one-line reason and decision id in the listing).
- A safe email conversation/vendor-customer context read, with explicit
  availability semantics.
- Explicit moderated-workflow actions for keeping a message held and assigning
  it to another administrator, if those are intended product capabilities.
