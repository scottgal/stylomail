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

**88 tests.** 79 hermetic by default; 9 opt-in (6 live-Host behind `STYLOMAIL_SMOKE_URL` +
`STYLOMAIL_SMOKE_KEY`, 3 keychain behind `STYLOMAIL_KEYCHAIN_SMOKE=1`). Solution build green.

## What exists

**Client** (`Api/`): `GetDecisionAsync`, `GetSubmissionAsync`, `GetSendersAsync`, `GetMessagesAsync`
(cursor paging, closed `MessageListState`), `ReleaseQuarantineAsync`, `PauseSenderAsync`,
`ResumeSenderAsync`, `RecordFeedbackAsync`, `GetReadinessAsync` (unauthenticated, the only call that
works before a key is configured). Contracts mirrored by hand in `Api/Contracts/`.

**Shell** (`Views/`, `Models/`, `Services/`, `Styles/`): three-pane window; sidebar from
`GET /v1/senders` and the three queue dispositions; list pane from `GET /v1/messages`; detail pane is
still the placeholder cards. Keychain behind `IKeychain` -> `MacKeychain` (P/Invoke into
Security.framework + CoreFoundation). `ConsoleEnvironment` holds the Debug-only harness overrides.

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

## Awaiting / open

- **No route enumerates the decision ledger**, so the Decisions pane is marked blocked. Reading one
  decision by id works. This is the one genuinely missing route left.
- The detail pane is unbuilt (placeholder cards only).

## Next

Decision detail pane: evidence, ordered reason codes each linked to its signals, versions, coverage,
cache provenance. Explainability over decoration; never a single score. Then the sender controls
(pause/resume, `Administer`) and quarantine release.

## Hard rules

1. Nothing in the console may be the only way to do something; every action must exist as a route.
2. Never render a credential.
3. Explainability over decoration: evidence and ordered reason codes, never one score.
4. Not a mailbox. No mail reading.
5. **No em-dashes** anywhere: UI strings, comments, commit messages, documents. Colon or full stop.
6. **Injected `TimeProvider`** for anything displayed or computed; no `DateTimeOffset.UtcNow` in logic.
