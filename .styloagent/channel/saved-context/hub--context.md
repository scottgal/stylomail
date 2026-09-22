# `hub-`, saved context

## Identity + scope

`hub-` owns **the live-traffic event seam**: the `ITrafficEvents` port, the SignalR hub the console
subscribes to, the four emission boundaries, the option that gates it, and the tests.
**Files I own:** `src/StyloMail.Host/Traffic/**`, plus my edits to the emission sites in the Host.
**Never edit:** the pipeline components themselves, the authentication path (`keys-`), `Queue`,
`Transport`, `.styloagent/spec.md`, `jevkey.pvt`.

## State, 2026-09-22

Repo `stylomail`, **worktree `.worktrees/hub`, branch `agent/hub`**.

**Committed at `8250514`** ("Announce traffic changes to the console, behind a flag that is off"),
then **`main` merged in at `3413325`**. `overview-` corrected my original mission: committing my own
lane was mine to do, because `wrap_up()` requires a committed branch. Plain path-list `git add` and
`git commit`, no `--amend`, no `reset`.

**300 Host tests green on the merged tree. `dotnet build StyloMail.slnx`: 0 errors, 0 warnings.**
The lane's own 30 tests are green (Seam 13, Hub 7, Emission 5, HardRule 5), and each mechanism was
mutation-checked (below). On the pre-merge fork the suite was 243 and all of those were green too.

**`overview-` rejected my first completion report as premature, and was right.** I reported 240/27
measured before I kept editing, then found the unaddressable-change defect and changed the tree
twice after sending it. They re-ran it mid-edit and saw two red. **A completion report has to
describe a frozen tree, and its test count has to be the count measured on that tree.** Finish,
freeze, verify, then report.

**A backup of the uncommitted lane is at `/tmp/hub-lane-backup/`** (all changed and new files, plus
`tracked-changes.diff`). It exists because this tree is shared and `queue-`'s harness rewrites
`src/` in place, so uncommitted work can be damaged by a SIGKILLed sweep.

Build/run:
```
export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"
dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj
```

## What the feature is

`StyloMail:Traffic:Enabled` (**off by default**) maps `MapHub<TrafficHub>("/v1/traffic")` with
`RequireAuthorization(HostPolicies.Review)`. Off, the negotiate route **404s** (same shape as the
Cloudflare intake: a route that exists and always refuses invites a configuration change to "fix"
it), the port resolves to `NullTrafficEvents`, and nothing else changes.

**The wire contract, which `desktop-` needs:** the client sends `X-StyloMail-Key` on the negotiate
request **and** on the WebSocket handshake; the server reads nothing from the URL. It subscribes to
one client method, `"traffic"`, and receives `{ kind, subjectId, occurredAt }` where `kind` is a
**name** (`DecisionRecorded`, `MessageStateChanged`, `SenderControlChanged`, `ReadinessChanged`).
It then re-reads the row over HTTP. **No state is ever pushed**, not even a transition's direction.

## `docs/running.md`

`overview-` ruled that the flag and the route are mine to document, after the merge so `keys-`'s
edits to that file came in first. Two additions: the hub in **§4 Conditional routes** (the 404 rule
and why the three answers are distinguishable), and a **§6 `### Live traffic`** subsection with the
`Enabled` key, the four facts an operator needs, and the client contract. **An undocumented flag is
a feature nobody can turn on.**

## Files

Created: `src/StyloMail.Host/Traffic/{TrafficEvent,ITrafficEvents,TrafficHub,SignalRTrafficEvents,`
`TrafficOptions,TrafficEmittingDeliveryPort}.cs` ·
`tests/StyloMail.Host.Tests/{TrafficSeamTests,TrafficHubTests,TrafficEmissionTests,`
`TrafficHardRuleTests,TrafficTestSupport}.cs`.

Modified: `Program.cs` (conditional map) · `Hosting/HostServices.cs` (`AddTrafficEvents`) ·
`Hosting/IngressHostedServices.cs` (`QueueDeliveryHostedService` wraps its port) ·
`Decisions/SqliteDecisionLedger.cs` · `Endpoints/OperatorEndpoints.cs` (quarantine release, pause,
resume) · `Observability/ReadinessProbe.cs` · `TestSupport.cs` (`WithFailingHub`) ·
the test csproj (`Microsoft.AspNetCore.SignalR.Client` 10.0.10, test-only, restores from cache).

## The four decisions that carry the rules

1. **Events are a hint, never state.** `TrafficNotice` has exactly three fields and is a written-out
   projection rather than a serialization of `TrafficEvent`, so a field added to the event later is
   not a wire change by accident. `subjectId` is *the one identifier a client can re-read on its
   own*, so a decision carries the assessment id and nothing about the verdict.
2. **Live vs stale.** Absent route (404) and unreachable route (401, or a failed connection) are
   different answers, because a console that reconnects forever against a deployment that will never
   offer a feed is the silent-freeze failure this rule exists for.
3. **No key in a query string.** The host has no query-string token path at all, and that is
   asserted: `?access_token=<key>` is refused. The WebSocket handshake is authenticated by the
   header, and deleting the header from the upgrade turns the test red with `401`.
4. **Tenant isolation is the group name.** `TrafficHub.OnConnectedAsync` derives it from
   `Context.User.TenantId()` and nothing else; the client supplies nothing and can ask for nothing.

**An unaddressable change is dropped, never broadcast.** `TrafficEvent.IsHostScoped` names the one
kind that belongs to the host; anything else without a tenant has no correct destination (nobody or
everybody) and is dropped. This replaced a factory guard that **threw** on an empty tenant, which a
self-review caught: that throw would have fired inside an assessment, before the port's catch, which
is precisely the one thing this seam may never do. The event type now validates nothing.

**The hard rule is structural, three ways.** (a) `ITrafficEvents.Publish` returns void and its
default is a no-op. (b) `SignalRTrafficEvents` is the only type in the host that holds an
`IHubContext`, and **that is a test**, not a promise. (c) every real implementation's body is a
try/catch, and **that is a test too** (`PublishAsync` is public precisely so the guarantee is
observable rather than inferred).

## What running changed in the design

- **The discard `_ =` alone was doing the protecting, not the catch.** A mutation that disabled the
  catch left every test green, because an async method captures its own exception into the discarded
  task. Found only because the mutation was run. Fixed by making `PublishAsync` awaitable and public
  and awaiting it in the test: disabling the catch now fails exactly the two cases that await.
- **The event factory guard was a throw on the emission path.** Found by self-review, not by a test:
  `TrafficEvent.DecisionRecorded` refused an empty tenant with an `ArgumentException`, which would
  have propagated out of `SqliteDecisionLedger.RecordAsync` into the assessment pipeline. The guard
  was well meant and in the wrong place. Replaced by a fail-closed drop at the adapter, and the two
  tests for it were red first (they showed an unaddressable change being **broadcast to every
  tenant**, which is the leak the guard had been hiding behind a crash).
- **An announcement inside the delivery port's `try` would have been caught as a port fault** and
  announced twice. Hoisted out of the try; the shape no longer depends on the port's no-throw
  contract being true.
- **My type-walk in the structural test looped forever.** Replacing a constructed generic with its
  definition never terminates, a definition being its own definition. It hung the whole test class,
  which reads as "the suite is broken" rather than as a bug in one test. Fixed by descending into
  type *arguments*.
- **SignalR's negotiate is a POST**, not a GET: a GET answers 405 over a mapped hub and 404 without
  one.
- **`Clients.Group(name)` needs no join to send**, only to receive, so the group join has to happen
  in `OnConnectedAsync` and be awaited. Every isolation test establishes membership **positively**
  first, because a client that silently joined nothing satisfies "heard nothing" perfectly.
- **A WebSocket cannot reach an in-process test host** (it dials `localhost:80`): the suite needs
  `options.WebSocketFactory` + `TestServer.CreateWebSocketClient()`, and `ConfigureRequest` is where
  the upgrade's header goes.

## Evidence, and what each mutation turned red

Suite: `dotnet test tests/StyloMail.Host.Tests/…` → **240/240, three runs, no `Failed!`.**

| Mutation | Red |
| --- | --- |
| `IHubContext<TrafficHub>` parameter added to `SqliteDecisionLedger` | `No_component_of_this_host_holds_a_hub_context` |
| The adapter's `catch` disabled | both `A_hub_that_fails_cannot_escape_the_port` cases |
| Readiness announces every poll | `Readiness_is_announced_when_the_answer_changes_and_not_on_every_poll` |
| A tenant change sent to `Clients.All` | `A_change_about_a_tenant_is_addressed_to_that_tenants_group_alone` + the isolation test |
| The ledger's announcement removed | `An_assessment_is_announced_as_a_hint_that_names_the_decision` |
| The delivery port's announcement removed | `A_delivery_that_settles_is_announced_as_a_change_to_that_message` |
| The header dropped from the WebSocket upgrade | `A_console_subscribes_over_the_transport_it_will_actually_use` (401) |
| A tenant-less change dropped instead of broadcast (the reverse: RED was the broadcast) | `A_change_that_cannot_be_addressed_is_dropped_rather_than_broadcast`, `An_event_built_with_nothing_in_it_is_dropped_rather_than_thrown` |

The hard-rule test compares a hub-down host against a no-hub host **field by field** (assessment:
the whole decision with the two generated ids normalised; delivery: cycle outcome, per-recipient
state, and that the transport was actually reached, so a wrapper that swallowed the delivery too
would not pass).

## Live probe, 15/15, `/tmp/stylomail-hub-probe/probe.py`

The real `dotnet run -- serve` process, real sockets, **no provider secrets** (every change it
exercises is produced at a boundary that does not consult the assessor, so the host runs with its
refusing sentinel). Two clean runs, 15/15 each. `python3 /tmp/stylomail-hub-probe/probe.py`.

Proved against Kestrel, which the suite could not reach: negotiate without a key is **401**; a key
in the query string is **401**; negotiate with the header is 200; **the WebSocket upgrade without
the header is refused 401 and with it succeeds**; a live `/v1/controls/senders/…/pause` arrives as a
`SenderControlChanged` notice carrying `kind`, `subjectId`, `occurredAt` and **nothing else**; a
`chmod 500` on the live spool turns `/health/ready` to `503 {"failedChecks":["spool"]}` and arrives
as `ReadinessChanged`, and changing it back arrives again; and a second host with the feature off
answers **404** on both negotiate and the hub route.

**One wrong expectation, mine, not the host's:** the first run asserted `connectionToken` on a
negotiate that did not ask for `negotiateVersion=1`, so the server correctly answered the legacy
`connectionId` shape. Fixed the probe, not the host.

## Not verified, and why

- **`wss://` off loopback** is the console's rule and `desktop-`'s to implement and test; the host
  serves what it is told to.
- **The console's own live/stale state machine** is `desktop-`'s, per their design. What this lane
  guarantees is the two answers it needs to distinguish: a route that is absent, and a connection
  that fails. Both are proved, in the suite and live.
- **Readiness is announced from the probe, and the probe is polled.** If a deployment never polls
  `/health/ready`, the baseline is never established and no transition is ever announced. That is
  the correct reading of "on the transition", not a defect, but it is an assumption worth naming.

## Hard rules I hold

- **No pipeline code may depend on the hub.** Every emission is a call to `ITrafficEvents` and
  nothing else; failure must be invisible to mail.
- **Nothing calls a hub context directly** except `SignalRTrafficEvents`, and the test enforces it.
- **Emit at the completion boundary**, never before it: after the ledger write, after the release
  applied, after the control write, after a delivery settles, on a readiness transition.
- **No other lane was asked for a hook.** The delivery boundary is the port the Host builds, which
  is why `queue-` did not have to learn about the hub.
- No `git add`, no `git commit`, no `--amend`, no `reset`.
