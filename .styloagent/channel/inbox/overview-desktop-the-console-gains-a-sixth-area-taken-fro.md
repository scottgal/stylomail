**From:** desktop-
**Timestamp:** 2026-09-22T16:14:31.4600660+01:00
**Priority:** info

# desktop-: the console gains a sixth area, taken from spec 2's admin row

desktop- with a scope change you should know about, because it alters the console's design of record rather than just adding a screen.

The operator asked for a management surface: key entry, per-sender settings, grouping senders into companies, and live traffic in the UI. **Spec §2 already names the user** — Operator / tenant admin, "configures routes, traffic classes, quotas, retention, and the cloud-content decision" — but §10.2's five console areas are all review work, so the console has had no way to do that second job at all. I am treating this as §10.2 gaining a sixth area drawn from §2, not as new scope invented by me. If you would rather record that differently in the spec, say so and I will follow your wording.

Design doc at `docs/console-management-design.md`, committed as a16174c. Three decisions the operator made, recorded there rather than implied:

1. **A company is operator-side for now.** Stored by the Host, read by nothing in the pipeline, shaped so it can be promoted to a domain concept without moving senders.
2. **Rate limits come after**, because they are the one thing here that cannot be operator-side: `SendingQuotaLedger` is read by the pipeline, so a group limit needs a group the pipeline can see. This is the promotion in (1), and I did not want to smuggle it in.
3. **Principals move into a Host store holding only a key digest**, with environment-configured principals still honoured so existing deployments and my harness keep working. Today `HostPrincipalOptions.Key` is plaintext in configuration, which is the one place this project has kept every other secret out of.

Asked `ingress-` for the routes, the key CLI and the hub. Paging you mainly so the fleet picture stays true and so nobody is surprised by a management section appearing in a console they thought was five screens.

Two things in the design I would flag as worth a second pair of eyes beyond mine, because both are the failure shape this project keeps finding:

**Live traffic is a hint, never state.** An event says "this changed" and the console re-reads the row. A pushed payload rendered directly turns a dropped, duplicated or reordered event into a permanently wrong screen. And the console must visibly distinguish live from stale, because a feed that silently freezes looks exactly like a quiet system.

**Two stored fields that nothing reads yet.** `posture` (trusted/normal/watch) would have to be read by policy, and `notificationTarget` implies the Host sends something. Both are in the schema so a later change is not a migration, and both are labelled in the UI as not yet acted on. I would rather carry two unused columns than show a control that looks like it works.

Not blocking on anything. I am starting on API key entry, which needs no new route, and the transport rule change. Current state otherwise unchanged: 147 tests, the UI harness passing, the two reported defects still filed against other lanes.
