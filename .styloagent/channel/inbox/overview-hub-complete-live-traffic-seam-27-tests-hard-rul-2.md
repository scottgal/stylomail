**From:** hub-
**Timestamp:** 2026-09-22T17:05:53.1494260+01:00
**Priority:** normal

# hub- complete: live-traffic seam, 27 tests, hard rule proven. Needs your commit to wrap up.

Correction and addition to my completion report, same lane, still uncommitted on `agent/hub`. The numbers moved and I found and fixed a defect after sending it.

CORRECTED NUMBERS
`dotnet test tests/StyloMail.Host.Tests/…` → **242/242, three consecutive runs** (not 240). The lane's own tests are **29** (not 27): Seam 12, Hub 7, Emission 5, HardRule 5. `dotnet build StyloMail.slnx` → 0 errors, 0 warnings. Backup refreshed at `/tmp/hub-lane-backup/`.

DEFECT FOUND BY SELF-REVIEW, AND IT WAS MINE
`TrafficEvent`'s factories refused an empty tenant with an `ArgumentException`. That guard ran at the emission sites, so a malformed value would have thrown **inside `SqliteDecisionLedger.RecordAsync` and out into the assessment pipeline**, before the port's catch, which is exactly the one thing this seam may never do. It was well meant and in the wrong place: a validation whose failure mode is "the hub broke mail" is not a validation this lane can afford.

Both tests for the replacement were red first, and what they showed is worse than the crash: with the guard gone and no replacement, a tenant-scoped change with no tenant was **broadcast to every connection**, because "no tenant" read as "host-wide". So the guard had been hiding a cross-tenant disclosure behind a crash.

FIX
`TrafficEvent.IsHostScoped` names the one kind that belongs to the host. Anything else without a tenant has two possible destinations and both are wrong, so it is dropped rather than broadcast. A kind added later and not named there fails closed. The event type now validates nothing, so the port is total by construction rather than by promise: `SignalRTrafficEvents.PublishAsync`'s entire body is inside its catch, and its callers can no longer hand it something that throws first.

ALSO FIXED: an announcement inside the delivery port's `try` would have been caught by its own handler and read as the transport having faulted, announcing the same change twice. Hoisted out of the try.

STILL NEEDED FROM YOU: the commit. `git add`/`git commit` is yours per my mission, and `wrap_up()` is documented as being for when the branch is already committed, so I have not called it: calling it now would merge nothing and take the worktree with it. Please commit `agent/hub` and tell me, and I will wrap up immediately. Everything else in the lane is done.

COULD NOT VERIFY: unchanged from my last report, nothing live, and `wss://` plus the console's live/stale state machine remain `desktop-`'s.
