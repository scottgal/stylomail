# `hub-`, saved context

Cold-start document. Written after the lane closed, so it carries the state of the merged tree
rather than the state I reported from.

## Identity + scope

`hub-` owns **the live-traffic event seam**: the `ITrafficEvents` port, the SignalR hub the console
subscribes to, the four emission boundaries, the option that gates it, and the tests.

**Files I own:** `src/StyloMail.Host/Traffic/**`, the emission-site edits inside the Host, and the
`Traffic*` test files.
**Never edit:** the pipeline components themselves, the authentication path (`keys-`), `Queue`,
`Transport`, `.styloagent/spec.md`, `jevkey.pvt`.

**Where I am now.** My worktree `.worktrees/hub` is **gone** (`wrap_up()` removed it after merging)
and the branch `agent/hub` is merged. I work in the main tree, `/Users/scottgalloway/RiderProjects/
stylomail`, on `main`, at `6387a53 Close the hub lane`.

Build/run:
```
export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"
dotnet build StyloMail.slnx
dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj
python3 /tmp/stylomail-hub-probe/probe.py     # the live probe, 15/15
```

## RESOLVED: the subscription window, measured and fixed in the harness

**Read this before writing another hub test.**

One full-suite run on merged `main` failed once: a test timed out after 15s waiting for the second
of two notices. I made the failure name what it had actually received (committed by `overview-` as
`1bc343e`), then probed it directly rather than waiting for a rare window, on `overview-`'s
instruction, because a flake that is never measured becomes folklore.

| Arm | Cycles | Missed |
| --- | --- | --- |
| Publish immediately after `StartAsync`, tenant-scoped | 900 (3 runs) | **7** |
| Publish 250ms after `StartAsync`, tenant-scoped | 300 | **0** |
| Publish immediately after `StartAsync`, broadcast | 300 | **0** |

**7 losses in 900 immediate tenant cycles, 0.78%**, every one with the subscriber having received
**nothing at all**. That is what a notice addressed to a group the connection has not joined looks
like: delivered to nobody, not queued. So the window is the **tenant group**, and it is specifically
that: the transport tracks a connection earlier than the hub's connection callback runs, so the
broadcast path is clean in 300 cycles, and 250ms is enough for the join every time.

**It is a dropped hint and not a production defect.** Rule 1 is that events are a hint and never
state, and the console renders from its own HTTP re-reads, so a missed notice cannot produce a wrong
screen. That is the property the rule buys, and this is the first time it was exercised rather than
asserted. **The test had been asserting a stronger property than the contract**: the contract is
"announced to whoever is subscribed", not "every subscriber is subscribed the instant it connects".

**The fix is in `TrafficSubscriber.ConnectAsync`**: it waits until the connection has demonstrably
heard a **tenant-scoped** notice before returning, retries if the first is lost, and forgets what it
consumed so assertions still read only what the test published. Hearing a tenant-scoped notice *is*
proof of membership, because the group is the only way it can arrive. It is in the helper so no test
has to know, and the reasoning and the table above are in its remarks.

Verified: **12 full-suite runs green** with the fix, 4 sequential and 8 with two instances
concurrent, which is the load the original failure appeared under. 300/300 each.

`overview-` declined a fifth wire kind (a `Subscribed` confirmation to `Clients.Caller`) for now:
it would strengthen a contract `desktop-` already consumes in order to satisfy a test, for a
guarantee the design deliberately does not make. If `desktop-` wants one for the live/stale
indicator, that is a different argument with its own evidence and it is theirs to bring.

The probe harness (`TrafficSubscriptionRaceProbe`) was scaffolding and has been deleted. It wrote
per-arm reports to `/tmp/hub-subscription-race-<arm>.txt`; the arithmetic is above and in the
helper's remarks.

**Never "fix" this by raising the timeout.** The test already waits 15 seconds, so time is not the
missing thing.

## What the feature is

`StyloMail:Traffic:Enabled` (**off by default**) maps `MapHub<TrafficHub>("/v1/traffic")` with
`RequireAuthorization(HostPolicies.Review)`. Off, the negotiate route **404s** (same shape as the
Cloudflare intake: a route that exists and always refuses invites a configuration change to "fix"
it), the port resolves to `NullTrafficEvents`, and nothing else changes.

**The wire contract, which `desktop-` consumes:** the client asks with `negotiateVersion=1`, sends
`X-StyloMail-Key` on the negotiate request **and** on the WebSocket handshake, and the server reads
nothing from the URL. It subscribes to one client method, `"traffic"`, receiving
`{ kind, subjectId, occurredAt }` where `kind` is a **name** (`DecisionRecorded`,
`MessageStateChanged`, `SenderControlChanged`, `ReadinessChanged`). It then re-reads the row over
HTTP. **No state is ever pushed**, not even a transition's direction.

## Commits

| | |
| --- | --- |
| `8250514` | The lane: seam, hub, four boundaries, tests |
| `3413325` | `main` merged in (12 commits, `TestSupport.cs` auto-merged cleanly) |
| `7374972` | `docs/running.md` and the checkpoint |
| `e06e7d8` | `wrap_up()`: merged `agent/hub` into `main` |
| `a52f486`, `6387a53` | `overview-` closing the lane and correcting the agent-commit rule |

`overview-` corrected my mission twice, both times rightly: **agents commit their own branches**
(`wrap_up()` requires a committed branch, so the original prohibition was self-defeating), and a
**completion report describes a frozen tree, with the count measured on that tree.** My first report
described a tree I was still editing and they caught it by re-running rather than by reading.

## Files

Created: `src/StyloMail.Host/Traffic/{TrafficEvent,ITrafficEvents,TrafficHub,SignalRTrafficEvents,`
`TrafficOptions,TrafficEmittingDeliveryPort}.cs` ·
`tests/StyloMail.Host.Tests/{TrafficSeamTests,TrafficHubTests,TrafficEmissionTests,`
`TrafficHardRuleTests,TrafficTestSupport}.cs`.

Modified: `Program.cs` (conditional map) · `Hosting/HostServices.cs` (`AddTrafficEvents`) ·
`Hosting/IngressHostedServices.cs` (`QueueDeliveryHostedService` wraps its port) ·
`Decisions/SqliteDecisionLedger.cs` · `Endpoints/OperatorEndpoints.cs` (release, pause, resume) ·
`Observability/ReadinessProbe.cs` · `TestSupport.cs` (`WithFailingHub`) · `docs/running.md` ·
the test csproj (`Microsoft.AspNetCore.SignalR.Client` 10.0.10, test-only, restores from cache).

## The decisions that carry the rules

1. **Events are a hint, never state.** `TrafficNotice` has exactly three fields and is a written-out
   projection rather than a serialisation of `TrafficEvent`, so a field added later is not a wire
   change by accident. `subjectId` is *the one identifier a client can re-read on its own*.
2. **Live vs stale.** 404 (no feed here), 403 (no `Review`), 401 (no key), and a failed connection
   are four distinguishable answers on purpose, because a console that reconnects forever against a
   deployment that will never offer a feed is the silent-freeze failure this rule exists for.
3. **No key in a query string.** The host has no query-string token path at all, and that is
   asserted: `?access_token=<key>` is refused. The WebSocket handshake is authenticated by the
   header, and deleting the header from the upgrade turns the test red with `401`.
4. **Tenant isolation is the group name.** `TrafficHub.OnConnectedAsync` derives it from
   `Context.User.TenantId()` and nothing else; the client supplies nothing and can ask for nothing.
5. **An unaddressable change is dropped, never broadcast.** `TrafficEvent.IsHostScoped` names the one
   host-wide kind; anything else without a tenant has no correct destination.

**The hard rule is structural, three ways.** (a) `ITrafficEvents.Publish` returns void and its
default is a no-op. (b) `SignalRTrafficEvents` is the only type in the host that holds an
`IHubContext`, and **that is a test**, not a promise. (c) every real implementation's body is a
try/catch, and **that is a test too** (`PublishAsync` is public precisely so the guarantee is
observable rather than inferred).

`overview-` accepted the completely-swallowed catch as a judgement call, with two consequences on
the record: **`desktop-`'s live/stale indicator is load-bearing for "failure is loud"**, and a hub
enabled with no console attached fails invisibly. `desktop-` has been told both.

## What running changed in the design

- **The discard `_ =` alone was doing the protecting, not the catch.** A mutation that disabled the
  catch left every test green, because an async method captures its own exception into the discarded
  task. Found only by running the mutation. `PublishAsync` is now awaited in tests.
- **The event factory guard was a throw on the emission path.** `DecisionRecorded` refused an empty
  tenant with an `ArgumentException`, which would have propagated out of `SqliteDecisionLedger`
  into the assessment pipeline. Behind that guard, an unaddressable change was being **broadcast to
  every tenant**: the well-meant line was hiding a cross-tenant disclosure behind a crash.
- **An announcement inside the delivery port's `try` would have been caught as a port fault** and
  announced twice. Hoisted out.
- **My type-walk in the structural test looped forever** (replacing a constructed generic with its
  definition is its own definition). It hung the class, which reads as "the suite is broken".
- **SignalR's negotiate is a POST**, and asks for `negotiateVersion=1` to get a `connectionToken`.
- **`Clients.Group(name)` needs no join to send**, only to receive, so the join must happen in
  `OnConnectedAsync` and be awaited. Isolation tests establish membership **positively** first.
- **A WebSocket cannot reach an in-process test host** (it dials `localhost:80`): the suite needs
  `options.WebSocketFactory` + `TestServer.CreateWebSocketClient()`, and `ConfigureRequest` is where
  the upgrade's header goes.

## Evidence

Suite on the merged tree: **300/300, and green on 4 sequential plus 8 concurrent runs**, with the
one failure described at the top. `dotnet build StyloMail.slnx`: 0 errors, 0 warnings. The lane's own
30 tests: Seam 13, Hub 7, Emission 5, HardRule 5.

| Mutation | Red |
| --- | --- |
| `IHubContext<TrafficHub>` parameter added to `SqliteDecisionLedger` | `No_component_of_this_host_holds_a_hub_context` |
| The adapter's `catch` disabled | both `A_hub_that_fails_cannot_escape_the_port` cases |
| Readiness announces every poll | `Readiness_is_announced_when_the_answer_changes_and_not_on_every_poll` |
| A tenant change sent to `Clients.All` | `A_change_about_a_tenant_is_addressed_to_that_tenants_group_alone` + the isolation test |
| The ledger's announcement removed | `An_assessment_is_announced_as_a_hint_that_names_the_decision` |
| The delivery port's announcement removed | `A_delivery_that_settles_is_announced_as_a_change_to_that_message` |
| The header dropped from the WebSocket upgrade | `A_console_subscribes_over_the_transport_it_will_actually_use` (401) |
| A tenant-less change broadcast instead of dropped | `A_change_that_cannot_be_addressed_is_dropped_rather_than_broadcast`, `An_event_built_with_nothing_in_it_is_dropped_rather_than_thrown` |

The hard-rule test compares a hub-down host against a no-hub host **field by field** (assessment: the
whole decision with the two generated ids normalised; delivery: cycle outcome, per-recipient state,
and that the transport was reached, so a wrapper that swallowed the delivery too would not pass).

## Live probe, 15/15, `/tmp/stylomail-hub-probe/probe.py`

The real `serve` process over real sockets, **no provider secrets** (every change it exercises is
produced at a boundary that does not consult the assessor, so the host runs with its refusing
sentinel). Two clean runs. Proved against Kestrel, which the suite cannot reach: negotiate without a
key 401; a key in the query string 401; negotiate with the header 200; **the WebSocket upgrade
without the header refused 401 and with it accepted**; a live pause arriving as a notice carrying
only `kind`, `subjectId`, `occurredAt`; a `chmod 500` on the live spool turning readiness to `503`
and arriving as `ReadinessChanged`, and back again; and a host with the feature off answering 404.

## Not verified, and why

- **`wss://` off loopback** is the console's rule and `desktop-`'s to test; the host serves what it
  is told to.
- **The console's own live/stale state machine** is `desktop-`'s. This lane guarantees the answers
  it needs to build one, and both are proved in the suite and live.
- **Readiness is announced from the probe, and the probe is polled.** A deployment that never polls
  `/health/ready` establishes no baseline and announces no transition. Correct on the ruling, worth
  naming as an assumption.

## Hard rules I hold

- **No pipeline code may depend on the hub.** Every emission is a call to `ITrafficEvents` and
  nothing else; failure must be invisible to mail.
- **Nothing calls a hub context directly** except `SignalRTrafficEvents`, and the test enforces it.
- **Emit at the completion boundary**, never before it: after the ledger write, after the release is
  applied, after the control write, after a delivery settles, on a readiness transition.
- **No other lane was asked for a hook.** The delivery boundary is the port the Host builds, which
  is why `queue-` never had to learn about the hub.
- **A report describes a frozen tree**, and its numbers are the ones measured on it.
- No `--amend`, no `reset`.
