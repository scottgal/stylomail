**From:** hub-
**Timestamp:** 2026-09-22T17:10:13.7257350+01:00
**Priority:** normal

# hub- complete: live-traffic seam, 27 tests, hard rule proven. Needs your commit to wrap up.

Second addition to my completion report, same lane, still uncommitted on `agent/hub`. The gap I named as "nothing live" is now closed.

LIVE PROBE, 15/15, TWO CLEAN RUNS
`python3 /tmp/stylomail-hub-probe/probe.py`, the real `dotnet run -- serve` process over real sockets, **no provider secrets** (every change it exercises is produced at a boundary that does not consult the assessor, so the host runs with its refusing sentinel).

Against Kestrel, which the suite could not reach: negotiate without a key is **401**; a key in the query string is **401**; negotiate with the header is 200; **the WebSocket upgrade without the header is refused 401 and with it succeeds**; a live `POST /v1/controls/senders/ops@acme/pause` arrives on the socket as a `SenderControlChanged` notice carrying `kind`, `subjectId`, `occurredAt` and nothing else; a `chmod 500` on the live spool turns `/health/ready` into `503 {"failedChecks":["spool"]}` and arrives as `ReadinessChanged`, and restoring it arrives again; and a second host with the feature off answers **404** on negotiate and on the hub route.

**One corrected expectation, and it was mine, not the host's.** The first run asserted `connectionToken` on a negotiate that had not asked for `negotiateVersion=1`, so the server correctly answered the legacy `connectionId` shape. I fixed the probe. Worth knowing for `desktop-`: ask for `negotiateVersion=1`. Their client does this already; nothing to change.

WHAT REMAINS: the commit. Reports stay immutable, so this is a fresh one rather than an edit; the numbers are **243/243 Host tests over four runs**, **30 lane tests**, `dotnet build StyloMail.slnx` 0/0, probe 15/15 twice. Please commit `agent/hub` and say so, and I call `wrap_up()` immediately.
