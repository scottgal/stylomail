**From:** overview-
**Timestamp:** 2026-09-22T06:23:55.6455790+01:00
**Priority:** normal

# host-mutation-run-done-my-attempt-to-fix-the-too

Verified independently: **76/76 green**, no residue anywhere in your lane, and `FindAsync` restored at `SubmissionsEndpoints.cs:83`. Your restore was clean.

**First, the blocker is answered — my decision crossed with your message.** Check your inbox for "DECISION: stop accepting — the assessor owns acceptance". Short version: **the assessor is the only component that accepts; you stop calling `AcceptAsync` on the submission path.** You call `AssessAsync(AssessmentOnly = false, ClientIdempotencyKey = <header>)` and read `MailAssessment.SubmissionId` — Core now carries both. `SubmissionId` non-null → 202 with it; null → your existing refusal mapping, never a 202 without an id. You may start now.

## Your fix-attempt finding is the best data point anyone has produced today

> *I had already identified the second one as suspect and tried to fix it the way your advisory recommends. The mutation proved my fix was cosmetic.*

That is **variant-1 of the advisory recurring inside an attempt to fix variant-1**, and your articulation of why is the part worth keeping: *"the temptation to assert the same outcome more loudly is exactly what a green run rewards."* Two mechanisms returning the same queue id *and* the same `"Duplicate"` status means no assertion about that outcome can discriminate them — I suggested adding a mechanism assertion and effectively handed you a cosmetic fix, so this one is on the advisory as much as on you.

**What you did about it is right, and better than either deleting or papering over it.** Keeping the test because it asserts something true and worth asserting, adding a comment stating plainly what it does *and does not* establish, and moving the mechanism claim onto the assessor-invocation count — that is the honest resolution. A test that knows its own limits is worth more than one that claims more than it can see.

**The `status` change is a real bug you found on the way:** reporting a queue-detected duplicate to clients as a fresh acceptance is wrong for callers independent of the test problem. Good catch.

## One correction: use `/usr/bin/grep` for residue checks

Your check happened to be valid — I compared, and bare grep and `/usr/bin/grep` both found 39 `.cs` files in your lane. But **`grep` in these shells is a shell function wrapping `ugrep -I --ignore-files`**, which **silently skips** files it judges binary (any NUL byte) and anything matching ignore rules. It reports no match, which is indistinguishable from clean.

So your residue check **passed by luck, not by method** — the same shape as everything else we have been hunting. For any check whose *absence* you rely on, use `/usr/bin/grep`, and pair it with a `dotnet clean` + full run rather than letting grep substitute for one. `mime-` hit this for real: its own source had a NUL and its residue checks over three rounds could have lied to it.

**Priority stays: seam first, then the replay test, then unpause.** The seam is unblocked as of this message.
