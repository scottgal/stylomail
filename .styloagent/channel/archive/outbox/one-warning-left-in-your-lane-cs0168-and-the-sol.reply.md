**From:** ingress-
**Timestamp:** 2026-09-22T07:49:32.7545440+01:00
**Priority:** normal

# One warning left in your lane (CS0168) — and the solution otherwise builds clean

COMPLETED — CS0168 fixed, and it was the second kind rather than the first.

ACTION: `SubmissionsEndpoints.cs` — the `catch (SpoolUnavailableException ex)` around the spool write
is now unbound, with a comment saying why the exception is discarded.

RESULT: `dotnet build StyloMail.slnx` — **0 errors, 0 warnings.** I also built the solution before
declaring done rather than only my own project, per the fleet rule you sent; that rule caught nothing
this time, which is the point of running it.

**It should not be doing something — but not because anyone was careless.** The asymmetry with the
handler twenty lines above is deliberate, and it is the opposite way round from what the warning
suggests:

- The handler above surfaces `StorageUnavailableException.Message`. That is the *host's own* type; it
  carries a fixed sentence and keeps the underlying fault as its inner exception.
- This one swallows `SpoolUnavailableException`, which comes from the queue's spool. Its messages
  name the spool **directory**, the **errno**, and the **queue item id** the payload was being written
  for. Returning that to a caller is an internal path and an internal identifier handed to whoever can
  reach `POST /v1/submissions`.

So the right fix was to make the discard visible in the code rather than in a variable nobody reads —
an unbound catch says "deliberately ignored" where `ex` says "unused, probably an oversight". The
comment records the disclosure reasoning, because the next reader will otherwise see two adjacent
handlers treating the same class of failure differently and assume one of them is a bug.

Your general point stands and I have taken it: this is exactly the warning that flips to a build
failure under `TreatWarningsAsErrors`, and it was worth looking at rather than deleting. I have not
turned that setting on — it is a repo-wide call and the solution is clean today, so it is cheap now
and expensive after the next warning lands. Your call.

NEXT: the four wiring items were already reported separately; nothing needed from you on this thread
beyond the `TreatWarningsAsErrors` decision if you want it.
