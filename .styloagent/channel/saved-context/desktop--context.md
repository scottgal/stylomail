# `desktop-` context

## Identity and scope

`desktop-`, spawned by `overview-`. I own the Avalonia operator console: **`src/StyloMail.Desktop/`
and `tests/StyloMail.Desktop.Tests/`**. No other project.

Mission doc: `.styloagent/missions/desktop-.md`. Spec section 10 is the design of record.

**The decision that constrains everything:** the console talks to the Host HTTP API and nothing else
(spec 10.1). **Zero ProjectReferences**, deliberately. No local database, no credential but its own
API key. If the console cannot do something through the API, the API is missing a route and I ask
`ingress-`, rather than reading the database or referencing a component.

## Repo state

- `/Users/scottgalloway/RiderProjects/stylomail`, branch `main`, shared tree (no worktree).
- `dotnet` not on PATH. Every shell:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- SDK 10.0.201. **Analyzers run as errors** (CA1823 caught me once). No `Directory.Build.props`.
- Judge with `dotnet test tests/StyloMail.Desktop.Tests/StyloMail.Desktop.Tests.csproj`, **and** run
  `dotnet build StyloMail.slnx` before claiming a milestone. `overview-` flagged a solution red from
  my project once; it was already fixed, but the lesson stands: my suite green says nothing about the
  shared solution.

## Landed commits

| SHA | What |
| --- | --- |
| `7a0c41a` | Headless typed API client + contract tests |
| `cffa78e` | Opt-in live-Host tests |
| `366cfe9` | Three-pane shell, macOS keychain interop |
| `b1d23b4` | Keychain verified against the real keychain |
| `5fdc1cf` | Sidebar + message list wired to the two new listings |
| `de7ad59` | Decision detail pane |
| `79405ec` | Sender controls and quarantine release |

**120 tests.** 108 hermetic by default; 12 opt-in (live-Host behind `STYLOMAIL_SMOKE_URL` +
`STYLOMAIL_SMOKE_KEY`, keychain behind `STYLOMAIL_KEYCHAIN_SMOKE=1`). My two projects build clean and
warning-free. **Check the solution build separately**: at the last check `dotnet build StyloMail.slnx`
failed in `tests/StyloMail.Host.Tests` (an inaccessible `CredentialAwareSemanticClassifier`), which is
another agent mid-edit, not mine. Do not assume a solution red is yours, and do not assume one is not.

`.styloagent/shots/README.md` holds the screenshot-harness pattern, written at `overview-`'s request
and adopted fleet-wide. Read it before re-deriving any of it.

## What exists

**Client** (`Api/`): `GetDecisionAsync`, `GetSubmissionAsync`, `GetSendersAsync`, `GetMessagesAsync`
(cursor paging, closed `MessageListState`), `ReleaseQuarantineAsync`, `PauseSenderAsync`,
`ResumeSenderAsync`, `RecordFeedbackAsync`, `GetReadinessAsync` (unauthenticated, the only call that
works before a key is configured). Contracts mirrored by hand in `Api/Contracts/`.

**Shell** (`Views/`, `Models/`, `Services/`, `Styles/`): three-pane window; sidebar from
`GET /v1/senders` and the three queue dispositions; list pane from `GET /v1/messages`; **decision
detail pane built** (`Models/DecisionView.cs`). Keychain behind `IKeychain` -> `MacKeychain`
(P/Invoke into Security.framework + CoreFoundation). `ConsoleEnvironment` holds Debug-only harness
overrides.

**Decision pane rules, all tested** (`DecisionViewTests`, 15 tests): reasons in policy's order, never
resorted; each reason resolves its own evidence inline; a named-but-absent signal is reported by name;
**an unavailable dimension renders "not measured (unavailable)" and has no path to render its 0.0**;
reduced coverage shows the score plus why; a null confidence renders "not reported"; the risk index is
pinned to "an index, not a probability" and never a percentage; shadow shows both actions; coverage
lists only the true flags.

**Screenshot harness** (Debug only): `dotnet run --project src/StyloMail.Desktop -- --screenshot <path>`.
Headless Skia render, no window, no focus stolen. Use it after any UI change; it has now found four
defects no test caught.

## Decisions made, do not re-derive

1. **Zero ProjectReferences; contracts mirrored by hand.** Referencing Core/Host would let the two
   drift into build-time agreement while disagreeing at runtime, and my contract tests would be the
   Host tested against itself. `tests/.../Wire.cs` is the transcription, from `Contracts/` + `HostJson`.
2. **Unknown enum members fail loudly.** `Allow` is the zero member of `MailAction`, so a silent
   fallback would be a **fail-open in a safety console**. Endorsed by `overview-`. An unrecognised
   value in a decision-rendering path is an error, never a default. Held line.
3. **Four distinct failures**: `ApiKeyNotConfigured`, `HostRefused`, `Unreachable`,
   `UnreadableResponse`. A missing or blank key sends **no request at all**.
4. **A 503 from `/health/ready` is an answer, not a failure** (failed checks must survive).
5. **The API key is never formatted into anything.** One read per request, one header.
6. **Principal ids are addresses** -> `Uri.EscapeDataString` on path ids (`%40`).
7. **`MessageListState` is closed** because the Host refuses `state=queued` by name. Sidebar
   destinations match; "Queued"/"Delivered" were removed.
8. **Model updates are marshalled to the UI thread** in `MainWindow.OnUiThreadAsync`, and the window
   owns its initial load (`InitialLoad`) so callers observe rather than repeat it.
9. **The dashboard does not invent columns.** A queued message carries a queue id, state, attempts and
   recipients. No subject, no sender. Not a mailbox.

## Verification method

Mutation checks against the client (backup in `/tmp`, restored; **no `.bak` files left in the tree**).
Found a real gap: no test asserted an authenticated request sends the key, so misspelling the header
broke nothing. Two tests added and the mutation now fails them. One mutation did *not* fail, which
proved a comment of mine wrong; the comment now states what was measured.

Four defects found only by rendering the window, all now covered:
1. Pane header stale against the status bar (selected item's detail raised nothing).
2. Status bar's right-hand address blank (window never passed one).
3. First run said "the Host refused the request" when the truth was "no API key set" (`/health/ready`
   needs no key, so it answered happily; the default port is AirPlay).
4. Senders rendered three rows for one principal (concurrent `ApplySenders` off the UI thread).

## Local smoke Host

Script at `/tmp/desktop-smoke/run.sh` (outside the repo). Starts a throwaway Host on
`127.0.0.1:5199` with locally generated values and **no real credential**; storage and spool under
`/tmp`. The principal key is in `/tmp/desktop-smoke/data/principal.key` (test value, not a secret).
Both listings verified 200 against it.

**Gotcha:** on this machine **port 5000 is macOS AirPlay Receiver** (`ControlCenter`), which answers
403 with an HTML body. `docs/running.md` uses `localhost:5000` as its example URL, so anyone following
it here is talking to ControlCenter, not StyloMail. Reported to `ingress-`.

## THE VERIFICATION BOUNDARY. Read before claiming anything is end-to-end verified.

**Every write that attaches to a decision cannot be exercised end to end without the operator's
semantic provider key.** An assessment needs Jev; a rejected key currently fails the whole request
rather than degrading to unavailable evidence (filed as an issue); therefore no decision can be
created on a deployment without a real provider key, therefore nothing keyed on a decision can be
driven live.

What that leaves verified where:

| Claim | Evidence |
| --- | --- |
| The client's wire contract against a real Host | live tests: readiness, decisions (404 shape), submissions, both listings, `state=queued` refusal, sender pause/resume round trip, release refusal by name |
| The sender write path end to end | live: pause, read back from the Host, resume, read back, audit trail survives |
| The decision pane's rendering | 15 tests plus a render, against the transcribed wire body, **not** a live response |
| Quarantine release success path | stubbed tests only. **Never run against a real Host.** |
| Feedback (`POST /v1/feedback`) | client method + contract test only. No UI. |

Say this plainly when reporting. Do not describe the decision pane as verified against a live Host.

## Awaiting / open

- **No route connects a listed message to its decision.** `assessmentId` exists only on the response
  to `POST /v1/submissions`; not on the listing rows, not on `GET /v1/submissions/{id}`, and
  `QueueItem` carries none. Asked `ingress-` for either an `assessmentId` on the row or a
  `GET /v1/decisions?queueId=` lookup. This is the console's headline use case, so it matters more
  than its size suggests. **Do not** work around it with a client-side map built from own submissions.
- **No route enumerates the decision ledger**, so the Decisions pane is marked blocked. Reading one
  decision by id works.
- Two issues filed against other lanes: a rejected Jev key 500s assessments while readiness says
  ready (medium), and `JevOptions.Endpoint`/`Model` are not configurable (low).

## Screenshot harness input for the decision pane

The pane renders from the real window, but a *real* decision needs a working semantic provider key
(see the filed issue), which this harness must not hold. So:

    STYLOMAIL_SMOKE_DECISION_FILE=/tmp/desktop-smoke/decision-fixture.json

loads a decision body from a file into the model. Debug only. The fixture is
`/tmp/desktop-smoke/decision-fixture.json`, mirroring `tests/.../Wire.Decision` plus an
unavailable dimension and a reason naming an absent signal. **Say so when presenting that
screenshot**; do not imply it came from a live Host.

## Write actions (landed)

Pause/resume a sender (`Administer`) and release a quarantine (`Review`). Asking and doing are
separate: a click opens a confirmation stating the consequence, the reason is required and collected
against it, and `ConfirmedAction` is null until one is given. Controls appear only where they would
do something (`CanPause` / `CanResume` are exclusive). A failure is never rendered as a success: the
`failed` flag is explicit, never inferred from the Host's prose. Proven end-to-end live: pause, read
back from the Host, resume, read back, audit trail survives.

## THE RECURRING BUG. Read this before adding anything to the window.

**Every model mutation must go through `MainWindow`.** Three separate defects in this window were all
the same thing: a model change made from a background continuation. The model updates and raises
`PropertyChanged`, and the visual never changes, because the notification is raised off the UI thread.
Symptoms seen: three sidebar rows for one principal, a queue selection that did not highlight, and a
confirmation bar that was pending in the model and absent from the window. All three passed every
model-level test. Use `OnUiThreadAsync` or one of the `Request*Async` / `SelectAsync` / `ShowDecisionAsync`
/ `Load*Async` methods. Never assign `_model` from a caller's thread.

## Harness gotchas (each cost real time)

1. **A faulted load used to produce a screenshot anyway.** The pump loop only checks `IsCompleted`.
   Now reports and refuses. If a capture looks wrong, check the harness's stderr first.
2. **`IsVisible` changes on a docked child need an explicit `window.InvalidateMeasure()`** before the
   frame is taken. Without it the change is in the model, notified, and absent from the bitmap.
3. **Never touch `window.DataContext` off the UI thread** ("Call from invalid thread"). `window.Model`
   is a plain object and is safe.
4. Reading the live Host's state back (`GET /v1/senders`) is the only way to prove a write landed.

## Next

Nothing assigned. Candidates, in the order I would pick them: wire the decision pane once ingress-
answers (see below); sender `Administer` actions beyond pause, if the Host grows any; feedback
(`POST /v1/feedback`) from the decision pane, which is the last of spec 10.2's five areas with no
surface; and packaging (spec 10.4's distribution question is still open and operator-owned).

## Hard rules

1. Nothing in the console may be the only way to do something; every action must exist as a route.
2. Never render a credential.
3. Explainability over decoration: evidence and ordered reason codes, never one score.
4. Not a mailbox. No mail reading.
5. **No em-dashes** anywhere: UI strings, comments, commit messages, documents. Colon or full stop.
6. **Injected `TimeProvider`** for anything displayed or computed; no `DateTimeOffset.UtcNow` in logic.
