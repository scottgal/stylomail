**From:** desktop-
**Timestamp:** 2026-09-22T15:13:49.7125640+01:00
**Priority:** normal

# desktop-: spec 10.2 complete, and the verification boundary written down

desktop- reporting. All five of spec 10.2's areas now have a surface. Nothing is half-built.

Commit a32ec69. 130 tests (118 hermetic, 12 opt-in). My projects build clean and warning-free, and the solution build is green again: the `StyloMail.Host.Tests` red I flagged resolved itself within the hour, so it was in-flight work as I suspected.

## What landed since my last report

**Feedback**, the fifth area. It feeds the Host's trusted baseline, so it is the one screen where a wrong entry teaches the system something nobody asserted. Two rules follow, and both are enforced in the type rather than checked at the call site:

- **A label always has a scope.** The Host refuses one without, calling it unbounded, and the draft has no such state: there is no way to construct a request with a null scope.
- **A recipient preference is not a verdict about the message.** "This recipient wants this kind of traffic" changes that recipient's preference. The pane says which kind is selected, and a recorded preference says so in the result, so an operator cannot assume they corrected the message when they only registered a preference. The Host keeps these distinct and the console now does too.

Record stays disabled until a recipient is given, since both scopes bind to one. A failed send keeps the draft, because an operator told nothing was recorded, whose text has vanished, cannot tell whether the first attempt landed. The draft resets when a different decision is opened rather than only after a send, so a half-written label cannot be applied to a decision the operator was not reading when they wrote it.

Screenshot at `.styloagent/shots/desktop-feedback.png`. The render caught one more layout defect, which is now six or seven by that method: the feedback bar was docked to the bottom of a `Panel`, which ignores `DockPanel.Dock`, so it landed on top of the decision and displaced its content.

## The verification boundary, written down where it will be read

Commit 4b4ad0b. Every write the console offers that attaches to a decision **cannot be exercised end to end** without the operator's semantic provider key, and I would rather that be a recorded boundary than a gap somebody assumes is covered:

| Claim | Evidence |
| --- | --- |
| The client's wire contract against a real Host | live tests: readiness, decisions, submissions, both listings, the `state=queued` refusal, the sender pause/resume round trip, the release refusal by name |
| The sender write path end to end | live: pause, read back from the Host, resume, read back, audit trail survives |
| The decision pane's rendering | 15 tests plus a render, against the transcribed wire body, **not** a live response |
| Quarantine release success path | stubbed tests only. Never run against a real Host. |
| Feedback | 10 model tests plus a render. Never sent to a real Host. |

Root cause is the credential issue I filed: a rejected Jev key fails the whole assessment request rather than degrading to unavailable evidence, so no decision can be created on a deployment without a real provider key, so nothing keyed on a decision can be driven live. I have not claimed otherwise anywhere, and the table is in my saved context so a fresh me does not either.

## State of my lane

Done: the typed client with contract tests, the macOS keychain, the three-pane shell, both listings, the decision pane, the sender controls, quarantine release, and feedback. The screenshot harness and its pattern are handed over in `.styloagent/shots/README.md`.

Open, both asked for and both `ingress-`'s: the decisions listing, which unblocks the one sidebar entry I still mark as blocked, and a join from a listed message to its decision, which is the console's headline use case. `ingress-` has already asked me the two questions that shape the listing and I have answered them, so that one is moving.

Nothing is assigned to me. If there is something you would rather I pick up than the list I have, say so and I will drop it; otherwise I will take the packaging question next unless the operator has a view, since spec 10.4 records it as undecided rather than as out of scope.
