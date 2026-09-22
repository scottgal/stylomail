# `desktop-` context

## Identity and scope

`desktop-`, spawned by `overview-`. I own the Avalonia operator console: **`src/StyloMail.Desktop/`
and `tests/StyloMail.Desktop.Tests/`**. I do not modify any other project.

Mission doc: `.styloagent/missions/desktop-.md`. Spec section 10 is the design of record.

**The one decision that constrains everything:** the console talks to the Host HTTP API and nothing
else (spec 10.1). It has **zero ProjectReferences**, deliberately. No local database, no credential
but its own API key. If the console cannot do something through the API, the API is missing a route
and I ask `ingress-` (who owns `src/StyloMail.Host`), rather than reading the database or
referencing a component. A headless deployment needs the same route.

## Repo state

- Repo: `/Users/scottgalloway/RiderProjects/stylomail`, branch `main`, shared tree (no worktree).
- `dotnet` is **not on PATH**. Every shell needs:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- SDK 10.0.201. Judge with `dotnet test tests/StyloMail.Desktop.Tests/StyloMail.Desktop.Tests.csproj`,
  never the solution build. No `Directory.Build.props` and no `.editorconfig` in this repo, so
  "analyzers run as errors" comes from SDK defaults; the build is currently warning-free.

## Landed

**Milestone 1, headless API client plus contract tests. 36 tests green, warning-free build.**

Files created:

- `src/StyloMail.Desktop/StyloMail.Desktop.csproj` (`WinExe`, net10.0, no Avalonia packages yet, no refs)
- `src/StyloMail.Desktop/Program.cs` (placeholder `Main`; becomes the Avalonia builder)
- `src/StyloMail.Desktop/Api/IApiKeyProvider.cs`
- `src/StyloMail.Desktop/Api/StyloMailApiException.cs`
- `src/StyloMail.Desktop/Api/StyloMailApiClient.cs`
- `src/StyloMail.Desktop/Api/Contracts/Enums.cs`
- `src/StyloMail.Desktop/Api/Contracts/DecisionContracts.cs`
- `src/StyloMail.Desktop/Api/Contracts/OperatorContracts.cs`
- `src/StyloMail.Desktop/Api/Contracts/HealthContracts.cs`
- `tests/StyloMail.Desktop.Tests/` (5 files: csproj, `StubHttpMessageHandler`, `TestApiKeyProvider`,
  `Wire`, plus `StyloMailApiClientTests`, `DecisionContractTests`, `ApiFailureTests`)
- `StyloMail.slnx`: both projects registered (additive, two lines each).

Client surface today: `GetDecisionAsync`, `GetSubmissionAsync`, `ReleaseQuarantineAsync`,
`PauseSenderAsync`, `ResumeSenderAsync`, `RecordFeedbackAsync`, `GetReadinessAsync` (the last is
unauthenticated and is the only call that works before a key is configured).

## Decisions I made, and why (do not re-derive)

1. **Zero ProjectReferences; DTOs mirrored by hand.** Referencing `StyloMail.Core` or `StyloMail.Host`
   would let console and Host drift into build-time agreement while disagreeing at runtime, and it is
   the first of many references. `tests/.../Wire.cs` transcribes the Host's wire bodies by hand from
   `src/StyloMail.Host/Contracts/` and `HostJson`, so the contract tests are evidence about the wire
   rather than the client agreeing with itself.
2. **Unknown enum members must fail loudly.** `JsonStringEnumConverter` throws on an unknown name, and
   that is load-bearing: if it bound to the zero member, a Host sending a new action would arrive as
   `Allow` and the console would tell an operator a message was allowed when it was held. Pinned by
   `DecisionContractTests.An_unknown_action_fails_loudly_rather_than_binding_to_a_default`.
3. **Three distinct failures**, in `StyloMailApiException.Failure`: `ApiKeyNotConfigured`,
   `HostRefused`, `Unreachable`, plus `UnreadableResponse` for drift. Each has a different remedy.
   A missing or blank key **sends no request at all**.
4. **A 503 from `/health/ready` is an answer, not a failure.** Not-ready carries the failed checks and
   must not be translated into an exception that discards them.
5. **The API key is never formatted into anything.** One read per request, one header, no exception
   message, log or `ToString` can produce it. Asserted over `ex.ToString()` on two failure kinds.
6. **A principal id is an address**, so path ids go through `Uri.EscapeDataString` (asserted: `%40`).

## Verification method: mutation checks

The DTO and failure tests were transcribed before their tests existed, so passing first time proved
nothing. I ran a mutation sweep against `StyloMailApiClient.cs` (backup in `/tmp`, restored after;
**no `.bak` files left in the tree**) to check the tests can actually fail:

| Mutation | Tests failed |
| --- | --- |
| decision route misspelled | 1 |
| enum-as-name converter dropped | 17 |
| 503 no longer accepted for readiness | 1 |
| missing-key guard removed | 2 |
| feedback / release / readiness route misspelled | 1 each |
| **API key header name misspelled** | **0, a real gap** |

The last row is the one that mattered: **no test asserted an authenticated request actually sends a
key.** I added `An_authenticated_request_sends_the_api_key_in_the_hosts_header` (asserting the literal
name, not the constant) and `The_api_key_header_constant_is_the_one_the_host_reads` (the constant,
because the readiness test asserts the header is *absent* and a renamed constant would make that
assertion pass for the wrong reason). Re-run: the header mutation now fails 2 tests.

One mutation **did not** fail: swapping runtime-type serialization for the inferred overload. The
comment in the client claiming a reproduced bug was therefore **wrong**, and is rewritten to state
what was measured and why the explicit form is still chosen.

## Blocked / awaiting

**Two read routes are missing.** Raised with `ingress-` (no reply yet):

1. `GET /v1/senders`: a tenant-scoped listing of principals with pause state, reason and actor. The
   sidebar needs it, and today a principal id must be known out of band before pause/resume is usable
   at all, so the control surface is only half reachable.
2. `GET /v1/messages`: a listing of queued/held/quarantined submissions with per-recipient state.
   `GET /v1/submissions/{id}` answers for one id and nothing enumerates.

Route 1 is on the critical path for the shell window's sidebar. Route 2 for the message list.

## Next

Shell window: three panes (sidebar from the API, message list, detail placeholder), then a screenshot
description to `overview-` **before** building the detail pane.

## Hard rules I am held to

1. Nothing in the console may be the only way to do something; every action must exist as a route.
2. Never render a credential.
3. Explainability over decoration: evidence and ordered reason codes, never a single score.
4. It is not a mailbox. No mail reading.
5. **No em-dashes** anywhere, in UI strings, comments, commit messages or documents. Colon or full stop.
6. **Injected `TimeProvider`** for anything displayed or computed; no `DateTimeOffset.UtcNow` in logic.
