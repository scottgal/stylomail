**From:** desktop-
**Timestamp:** 2026-09-22T14:56:36.8615090+01:00
**Priority:** normal

# desktop-: sidebar and message list on real data, and a defect only a picture found

desktop- reporting. Query routes now wired; both screens show real data from a running Host.

Commit 5fdc1cf. 88 tests (79 hermetic, 9 opt-in: 6 live-Host, 3 keychain). Solution build green, and I am now running that as well as my own suite before claiming a milestone.

Screenshot, at .styloagent/shots/desktop-connected.png, against a local throwaway Host on loopback:
- sidebar: Host (Connected), then Queues (Awaiting decision, Held, Quarantined), then Senders showing one real principal read from GET /v1/senders, then Review (Decisions).
- "Awaiting decision" is selected, so the list pane header reads "Awaiting decision" with the route behind it as its subtitle, and the empty state explains what the pane is and that mail already in normal delivery is not enumerable there.
- status bar: green dot, "Connected", "The Host is running and can accept mail.", and http://127.0.0.1:5199/ right-aligned.

Three decisions worth your attention.

1. The listing state is a closed type rather than a string, mirroring a route decision rather than a style preference. The queue enumerates only what is waiting for attention and refuses state=queued by name, because filtering a page after it had been cut would return short pages with a wrong hasMore and a console paging through it would watch mail disappear. A client that can only name the three states cannot express the request. I also corrected the sidebar: it previously offered "Queued" and "Delivered", which would have answered 400 to a click, and a destination that cannot be requested reads to an operator as a fault rather than as a filter that does not exist. A live test asserts the Host agrees that state=queued is a refusal, sent through a raw HttpClient precisely because the typed client cannot express it.

2. "Decisions" is now marked blocked rather than merely empty, and it is the only such entry, with a test asserting exactly that. Reading one decision by id works, but nothing enumerates the ledger, so the pane has no rows and no amount of client work would give it any. This is now the one genuinely missing route on my side. A marker that also appeared on entries whose routes do exist would stop meaning anything, which is why the negative assertion is there.

3. Rows show a queue id, state, attempt count and recipients, because that is what a queued message carries. No subject and no sender column. Inventing them would mean rendering empty ones, and this is not a mailbox.

The defect worth your time, because it is the second time a picture has beaten my tests:

**The senders section rendered three rows for one principal.** The API returned exactly one; curl confirmed it. The cause was not the model, which was correct throughout, but the window: two loads ran concurrently, because a caller with no synchronization context resumes on the thread pool, and both then mutated an ObservableCollection that is not thread-safe. No assertion on the model alone would have caught it. The window now marshals every model update to the UI thread and owns its own initial load, so callers observe it rather than repeating it. I added a test that applying a listing twice does not accumulate, which guards the property that made the symptom confusing, though the concurrency itself is fixed in the view.

That is four defects the renderer found and no test did: a stale pane header, a blank status-bar field, the first-run message saying "the Host refused the request" when the truth was "no API key set", and this one. I would suggest the fleet treat a rendered screenshot as a verification step in its own right for anything with a surface, rather than as documentation. I can hand over the harness pattern if it is useful: it is Debug-only, writes a PNG with no display and no stolen focus, and it is about eighty lines.

Two hazards for the fleet, both out of my lane and reported to ingress-:
- On this machine 127.0.0.1:5000 is macOS AirPlay Receiver (ControlCenter), answering 403 with an HTML body. docs/running.md uses localhost:5000 as its example URL, so anyone following it on a Mac is talking to ControlCenter.
- Queue paging had a real cursor defect, found and fixed by queue- within minutes of ingress- flagging it. I had not built against it yet, so nothing of mine needs unpicking.

Next: the decision detail pane, per the order you set. Evidence, ordered reason codes each linked to the signal ids that produced them, versions, coverage, cache provenance. No single score. Then the sender controls and quarantine release, which need Administer and are the first actions rather than reads.
