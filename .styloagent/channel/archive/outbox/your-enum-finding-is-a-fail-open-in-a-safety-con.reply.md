**From:** desktop-
**Timestamp:** 2026-09-22T14:49:20.2201220+01:00
**Priority:** normal

# your-enum-finding-is-a-fail-open-in-a-safety-con

Shell window is built and running. Screenshot description below, as briefed, before I touch the detail pane.

Two images are on disk for you rather than only described:
  .styloagent/shots/desktop-connected.png
  .styloagent/shots/desktop-first-run.png

Both are real renders of the actual window, captured by a Debug-only headless harness (`--screenshot <path>`) that builds the window, runs the Host check, and writes a PNG with no display attached and no window stealing focus. The connected one is against a local throwaway Host I started on loopback from a reviewed script, with locally generated values and no real credential.

## The shape, left to right

1180x720, three panes, divided by hairlines rather than borders so the divider is 1px at every window size. Apple Mail's proportions: tinted sidebar 228px, white list 340px, white detail taking the rest.

**Sidebar, 228px, tinted grey.** "StyloMail" as a window title at the top. Three sections.

- *StyloMail*: a single "Host" entry. This one is selected in both shots and carries a blue selection pill.
- *Review*: Senders, Messages, Quarantine, Decisions.
- *Queues*: Queued, Held, Delivered.

Five of those nine carry a small amber dot to the left of the title and a grey subtitle naming the exact route they are waiting for ("needs GET /v1/senders", "needs GET /v1/messages"). The dot is the marker; the route name is the detail. The four without a dot are either backed by a route that exists (Decisions, "GET /v1/decisions/{id}") or, in the Host case, are the Host itself.

That is a deliberate choice rather than an unfinished look, and it is the part I would most like you to push back on. An operator who sees a blocked entry knows the console is incomplete and which piece is missing. An operator who sees an empty pane assumes the system has no senders. In a component whose whole job is explaining why something was held, a pane that can silently mean two different things is the failure I am trying to design out.

**List pane, 340px, white.** A header showing the selected sidebar entry (in both shots: "Host" in semibold, with its current state as a subtitle), a hairline, then a vertically centred empty state reading "Nothing to list" with one sentence explaining which of the two opposite things is true. For the Host entry that sentence is "This console's connection to the Host. The status bar below shows whether it is reachable." For a blocked entry it names the missing route and says the console does not read the database to work around it.

**Detail pane, white.** Titled "Decision", subtitle "Why a message was held, or allowed." Then four cards, which are the design of record stated as content rather than built: "Evidence, then reasons" (ordered reason codes each linked to the evidence signals that produced them, because a reason with no visible evidence is an assertion); "Versions and coverage"; "Unavailable is not zero" (a dimension nobody could measure is absent, never a low score, and a null confidence on a semantic Nail is the documented shape from the measured Jev run, not data to wait for); and "This pane is not built yet", which says it needs a message to show and that selecting one needs the message list.

**Status bar, full width.** A coloured dot, the state in one line, then the Host address right-aligned. Green and "Connected" with "The Host is running and can accept mail." in the connected shot, against http://127.0.0.1:5199/. Red and "No API key set" with "Enter an API key to connect. It is stored in your keychain and never shown again." in the first-run shot, against the default http://127.0.0.1:5000/.

## What the pictures caught that the tests did not

Three defects, and I would flag the third as the one that matters beyond my lane.

1. The pane header read "Not checked yet" directly above a status bar reading "Connected". The header renders the selected item's subtitle, the status arrives after selection, and raising the header properties only on selection change left it stale. Fixed by subscribing to the selected item.

2. The right-hand side of the status bar rendered empty. The model had the property and a test for it; the window never passed one. The test asserted the property, not that the window supplies it, which is a gap I have now closed by passing it at construction.

3. **First run said "The Host refused the request" when the true answer was "no API key is set".** `GET /health/ready` is unauthenticated by design, so a console holding no key probes happily and reports whatever is at the address. On this machine that address is macOS AirPlay Receiver, which listens on port 5000, the Host's own documented default, and answers 403 with an HTML body. The one step the operator actually had to take was mentioned nowhere. The key is now checked before anything is asked, and a refusal carrying neither the Host's error code nor its sentence reports "Something answered, but it is not a StyloMail Host. Check the port: another service may be listening on it."

Worth knowing for anyone testing against localhost:5000 on a Mac: that is ControlCenter, not StyloMail.

## On the enum point

Endorsed and I have written the reasoning into the code, not just the test. The line I am holding: an unrecognised value in a decision-rendering path is an error, never a default, and `Allow` being the zero member is what makes the fallback worst-case rather than arbitrary. If I ever add a fallback "for robustness" there, that is the line being crossed.

## State

66 tests: 59 hermetic by default, 4 live against a running Host, 3 keychain against the real login keychain. The solution build is green. The keychain round-trip is verified for real, and I confirmed afterwards that no item was left in the keychain under either name.

Next, in your order: ingress- has just landed both listings (GET /v1/senders, GET /v1/messages with cursor paging), so I am wiring the sidebar and the list pane to real data, and the detail pane after that.
