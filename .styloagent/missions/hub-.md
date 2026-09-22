# hub- : live traffic events for the console

You own the SignalR hub the operator console subscribes to, so the UI reflects traffic as it happens
rather than when someone refreshes. `ingress-` built everything around it and handed this item off
deliberately rather than half-building it. You are starting from a clean context for that reason.

**You are in an isolated worktree.** Another agent (`keys-`) is working in the Host project in the main
tree at the same time. You will not collide with it and you should not try to: stay in your branch,
and when you are done, call `wrap_up()`.

## Read first, in this order

1. `.styloagent/channel/saved-context/ingress--context.md`. `ingress-` wrote the emission map there
   before handing over, and it is the part only that lane knew. The map is also reproduced below so
   you do not depend on its checkpoint surviving.
2. `docs/console-management-design.md`, the section "Live traffic over SignalR".
3. `.styloagent/channel/saved-context/overview--context.md`, the ruling section, for the guards.

## The four rules, and the first two are why this exists

1. **Events are a hint, never state.** An event says "this changed"; the console re-reads the affected
   row over HTTP. A pushed payload rendered directly makes a dropped, duplicated or reordered event a
   permanently wrong screen.
2. **The console must visibly distinguish live from stale.** A feed that silently freezes looks
   exactly like a quiet system. When the hub is down the console falls back to its existing behaviour
   and says so.
3. **The key never goes in a query string.** SignalR's access-token pattern puts it in the URL, where
   it lands in access logs, proxies and crash reports. Send a header on **both** the negotiate request
   and the WebSocket handshake.
4. **`wss://` for anything that is not loopback**, on the same rule as the REST surface.

## The hard rule, and it is the whole point

**No pipeline code may depend on the hub.** Emission is fire-and-forget and its failure must never
fail an assessment or a delivery. A hub outage must be invisible to mail flow. If any emission can
throw into an assessment or delivery path, it is wrong regardless of how the events are shaped.

Make that **structural rather than a promise**: an `ITrafficEvents` port whose default implementation
is a no-op (that is the flag-off path), with every real implementation wrapping its own body so
nothing escapes, and **no endpoint or worker calling a hub context directly**.

The feature is **off by default**. A deployment that has not enabled it loses immediacy and nothing
else.

## The emission map `ingress-` wrote

| Event | Where it is produced |
| --- | --- |
| assessment completed | `SqliteDecisionLedger.RecordAsync`, the ledger write *is* the completion boundary |
| message state changed | `QueueDeliveryHostedService` (hosting is the Host's; the state transaction belongs to `queue-`) and `QuarantineEndpoints` |
| sender paused / resumed | `ControlsEndpoints.PauseSenderAsync` / `ResumeSenderAsync` |
| readiness changed | `ReadinessProbe.Check` / `ProviderCredentialHealth`, the **transition**, not every poll |

Exhaust these sites before asking any other lane for a hook. If you genuinely need a call from
another lane's completion boundary, ask that lane for a single call and nothing more; the hub must not
become a reason for two lanes to know about each other.

## Constraints

- **Do not run `git add` or `git commit`.** That is mine. Leave your work committed on your branch
  only via `wrap_up()`, which is the one path that merges.
- **Never `git commit --amend` or `git reset`.**
- **Stay in your lane**: the event seam, its port, the four emission sites, the option that gates it,
  and the tests. Do not re-implement the authentication path: `keys-` owns that. Do not change what
  any pipeline component does.
- Build with `export DOTNET_ROOT=/usr/local/share/dotnet` and
  `export PATH="/usr/local/share/dotnet:$PATH"`. Solution is `StyloMail.slnx`. Analyzers are
  **errors**.
- Do not read or print any credential value.

## Done when

- The hub is behind a flag that is **off** by default, and the no-op default is the path a deployment
  gets without configuring anything.
- Emission happens at the four sites above through the port, with nothing calling a hub context
  directly.
- There is a test that **"the hub being down changes nothing about mail"**: with the hub unavailable
  or throwing, an assessment and a delivery both succeed and produce the same decision they would have
  produced otherwise. That test is the evidence for the hard rule, so it is not optional.
- Readiness emits on the **transition**, not on every poll, and you have shown that.
- The console can tell live from stale, and that behaviour is covered.
- You have reported to `overview-` with: files created, test count, exactly what you ran and what came
  back, and anything you could not verify.

Report anything you find that belongs to another lane rather than fixing it yourself.