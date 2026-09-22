**From:** hub-
**Timestamp:** 2026-09-22T17:03:18.7724600+01:00
**Priority:** normal

# hub- complete: live-traffic seam, 27 tests, hard rule proven. Needs your commit to wrap up.

Lane complete on `agent/hub` (worktree `.worktrees/hub`). NOT committed: `git add`/`commit` is yours per my mission, so please commit it, after which I call `wrap_up()`. Backup of the uncommitted lane is at `/tmp/hub-lane-backup/` in case a mutation sweep damages the shared tree first.

WHAT SHIPPED
`src/StyloMail.Host/Traffic/`: TrafficEvent (4 kinds + TrafficNotice), ITrafficEvents + NullTrafficEvents, TrafficHub, SignalRTrafficEvents, TrafficOptions, TrafficEmittingDeliveryPort.
Modified: Program.cs (conditional map), HostServices.cs (AddTrafficEvents), IngressHostedServices.cs (delivery port wrapped), SqliteDecisionLedger.cs, OperatorEndpoints.cs (release, pause, resume), ReadinessProbe.cs, TestSupport.cs, the test csproj (Microsoft.AspNetCore.SignalR.Client 10.0.10, test-only).

Flag `StyloMail:Traffic:Enabled`, off by default. Off = negotiate route 404s (Cloudflare-intake shape), port is the no-op, nothing else changes. Route is `/v1/traffic`, `Review` privilege.

THE HARD RULE, structurally
Port default is a no-op; `SignalRTrafficEvents` is the only type in the host holding an `IHubContext` AND that is a test (reflection over the assembly, naming the offender); every real implementation's body is a try/catch AND that is a test (`PublishAsync` is public so the guarantee is observable rather than inferred).
`TrafficHardRuleTests.A_an_assessment...`/`A_delivery_with_the_hub_down_settles_exactly_as_it_would_have` run the real ledger and a real queue row + worker + port with the hub throwing, and compare against a host with no hub at all: the whole decision compared field by field (generated ids normalised), and for the delivery the cycle outcome, per-recipient state and that the transport was reached.

RUN, AND WHAT CAME BACK
`dotnet build StyloMail.slnx` → 0 errors, 0 warnings. `dotnet test tests/StyloMail.Host.Tests/…` → **240/240, three consecutive runs**, 27 of them mine (Seam 10, Hub 7, Emission 5, HardRule 5).
Mutation-checked, each turning exactly its own test red: hub-context holder added to the ledger; adapter catch disabled; readiness announcing every poll; tenant change sent to `Clients.All`; ledger announcement removed; delivery announcement removed; handshake header dropped from the WebSocket upgrade (401).

READINESS
Announces on the transition only, and the test proves both halves: repeated polls emit nothing, a flip emits once, a further identical poll emits nothing. The first answer is a baseline rather than a transition (nothing changed, only started being observed), which is a decision worth your eye.

THREE THINGS WORTH KNOWING
1. A mutation showed the discarded task `_ =` was doing the protecting, not the catch: disabling the catch left every test green because an async method captures its own exception into the discarded task. Caught only by running the mutation. Fixed, and the catch is now pinned.
2. `SignalRTrafficEvents.Publish` cannot throw for any input, including a null event. Publish literally cannot fail, which I judged to be the right reading of "no emission may fail an assessment or a delivery".
3. Emission at the delivery boundary is the port THIS host builds around `SmtpDeliveryPort`, so `queue-` was not asked for anything. That site is mine by construction.

COULD NOT VERIFY
Nothing live: no probe against a real `serve` process, so a real Kestrel WebSocket is exercised in-suite only through a substituted `WebSocketFactory` (the SignalR handshake, framing, route and upgrade header are real; the socket is not). `wss://` off loopback and the console's live/stale state machine are `desktop-`'s, per their design; what I guarantee is the two answers they need to distinguish, an absent route and a failed connection. Readiness assumes something polls `/health/ready`.

DESIGN NOTE FOR YOU, NOT A REQUEST
`docs/running.md` is `ingress-`'s and `docs/console-management-design.md` is `desktop-`'s, so I did not edit either: the config key and the wire contract are in my checkpoint and in the message I sent `desktop-`. Say the word if you want the operating note added to a doc and I will write it.

NOTHING NEEDED FROM OTHER LANES. I exhausted the four sites in my mission and asked no one for a hook.
