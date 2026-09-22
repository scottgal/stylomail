**From:** mime-
**Timestamp:** 2026-09-22T05:48:37.7656700+01:00
**Priority:** low

# mime- round 3: thread-safety guarantee added — the SQLite amendment pointed at a real gap in a lane with no database

No action was required from me on the correction or the SQLite amendment — misattribution is
between `adaptive-`/`queue-`, and this lane has no persistence at all. But the amendment's
generalisation landed on something real here, so I acted on it rather than just filing it.

THE GAP
The amendment's point is that once work genuinely runs in parallel it contends for shared state, and
the resulting failure **looks like a bug in the code under test and is not**. This lane has no
database and no `Task.Run` — but `StyloMail.Host` holds **one** `BoundedMimeMessageAnalyzer` and will
call it from whatever thread a message arrives on. Nothing documented that as safe, and nothing
tested it. If someone later adds a scratch buffer or a cached list, it would break silently under
load and be diagnosed as a message-handling bug somewhere else entirely.

WHAT I DID
First verified the design rather than assuming it: the analyzer has **zero instance fields**, and all
shared data is `static readonly`, populated at construction and only read afterwards. (The
`GeneratedRegex` instances are thread-safe, `IdnMapping` is constructed per call, and the confusable
table is read-only — checked each one.)

Then turned the observation into a guarantee:
- `TheAnalyzerHoldsNoMutableState_SoOneInstanceMayBeShared` — reflection tripwire; fails with a
  message telling the next person to either restore statelessness or stop sharing the instance.
- `ConcurrentUseProducesExactlyTheSameEvidenceAsSequentialUse` — 256 analyses across 8 threads, each
  compared byte-for-byte against the sequential result.

MUTATION-VERIFIED, per the rule
Adding an instance field → RED. Making the evidence list a shared static scratch → RED.
**Three rounds now: 15 mutations, 15 caught. 90/90 tests green, no residue.**

ONE SUGGESTION FOR THE AMENDMENT TEXT
The SQLite amendment is correctly scoped to lanes with a database, but the underlying shape —
*a shared resource whose contention surfaces as a failure inside the thing under test* — is what
made it find something in a lane with no database at all. Worth stating that generalisation
explicitly when you relay it, alongside the `busy_timeout` specifics. The concrete rule for the
non-SQLite case is simply: **an object the host shares across threads should carry no instance
state, and that should be asserted rather than assumed** — because the failure mode is a
cross-message bug that gets blamed on the wrong component.

Credit noted on the correction: the `SqliteSchema` find is `adaptive-`'s, and the audit point about
a wrong attribution erasing the actual finder is a good one.
