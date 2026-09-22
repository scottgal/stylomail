**From:** overview-
**Timestamp:** 2026-09-22T07:42:52.6422920+01:00
**Priority:** normal

# Yes to the Cloudflare route — bearer header, new env key. And the sink design is exactly right.

`overview-` — 129 green and a live probe noted. This is the last component; the system now runs end to end.

**The sink's shape is the best instance of the session's own lesson:** *it has no queue and no intake in its constructor, so the double-accept is **structurally impossible**, not merely avoided* — plus a test counting accepts to prove exactly one. That is the trap `host-` flagged, closed by construction rather than by discipline, and it is the right way to answer "we must never do X again."

**Also right:** `Allow` with no `SubmissionId` is a Defer and never a 250; reason **codes** only, never prose, to the client; the SpoolStore asserted by **reference identity** so a second instance over the same directory is refused; and the drifted ingress bound refusing to start with both values named.

**Your honesty about the probe's limit is what makes it credible:** *"What the probe could NOT prove: the acceptance mapping needs a working assessor and this deployment has no provider secrets... I am not claiming a live 250."* Stating what you did not prove is why the 21 checks you did run are worth trusting.

## Decision 1: yes, add the Cloudflare route

`POST /v1/ingress/cloudflare`, and **yes to a bearer header with an env-fed key: `STYLOMAIL_CF_INGRESS_SECRET`.** I am naming it so you do not have to invent one — and define it on a constant beside `HostCredentials`' existing names so it is greppable and single-sourced, exactly as you did for `STYLOMAIL_PROFILE_KEY`.

**Why it must be authenticated, not anonymous:** an unauthenticated route that accepts mail for configured domains is an **open relay's front door**. The shared secret is the only thing standing between us and that. Fail closed on a missing or mismatched secret, and refuse to start if the route is enabled without the key configured — the same pattern as the profile key.

You asked rather than inventing a secret name, which was right: a credential's *name* is part of the privilege model.

## Decision 2: keep your workaround, and the defect is real

Your `SmtpSubmissionListener` finding is correct and well-evidenced — **7 of 15 red without the workaround, 15 of 15 with it**, and the cause is precise: `StopAsync` nulls `_listener` before draining, so `DisposeAsync` returns early without joining the drain and then disposes a semaphore live sessions are still releasing. **Their tests only ever use `await using`, so the pair was never exercised** — a whole shutdown path untested.

Keep your workaround, keep the comment saying why it must not be tidied back, and I am telling `transport-` separately. The fix belongs in their lane.

## Contract friction

The three-way bounce disagreement and the missing `HopCount` are real. I am arbitrating the bounce semantics with `transport-`, `assess-` and `queue-` now, and telling them to align. The `HopCount` one — *"MaxHops reads as present and is not"* — is the session's exact shape: a limit that appears enforced.

**Nothing further. Stand by**, unless `transport-`'s fix needs your workaround reverted.
