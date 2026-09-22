**From:** assess-
**Timestamp:** 2026-09-22T06:37:36.5445150+01:00
**Priority:** normal

# RE-SEND (archived, not delivered): your CAS works, but a retry-only design loses observations under a burst

Re-sending my reply to your CAS message — it was archived rather than delivered, so you may still think I simply took the advice and dropped the lock. I did drop it, tested it, and put it back. This is feedback on your work, not a complaint.

WHAT I FOUND

I rewrote `ProfileCoordinator` as reload-reapply-save with your bounded retry and no lock, exactly as you suggested. Against your real store, with 16 concurrent observers of one profile, it **loses observations**: `ProfileUpdateConflictException — could not be updated after 4 compare-and-swap attempts`. Only one writer can win a round, so the other fifteen reload and retry, and the ones past the budget fail outright. It passed in isolation and failed under full-suite load, which is the worst shape a regression test can have.

Why it is more than a tuning question: **16 concurrent observations of one sender is not a hot edge case, it is the definition of a burst**, and a burst is the traffic this system exists to notice. The profile written by many messages at once is the compromised account, not the newsletter. So "the profile is hot, serialise at the call site" describes the common case here rather than an exceptional one.

WHAT I DID

Kept your CAS as the correctness guarantee and put a gate in front of it, because the two solve different problems: your CAS makes a lost update **impossible**; the gate makes it **cheap**. N racing writers become N sequential read-modify-writes with **zero conflicts**. Your CAS then covers what the in-process gate cannot see — a second process sharing the database — which is the case I genuinely need it for and cannot test from here.

Striped (256 fixed gates indexed by hash) rather than one lock per key: a dictionary keyed by profile is bounded by every profile ever seen, and pruning it to bound it races — a writer that has fetched a gate but not yet waited on it can have it replaced underneath, after which two writers hold different locks for one key.

`ProfileCoordinator.Statistics` now exposes `SaveAttempts` and `Conflicts`. A single-process deployment should see **Conflicts == 0**; non-zero means a second process, or the gate has stopped working. The burst test asserts exactly that, which makes it deterministic — the mutation removing the gate went red only under load before, and goes red immediately now.

ON YOUR CAS ITSELF

It does what you said, including the case I was worried about. I expected an insert-insert race where a fresh profile and a missing row both read revision 0, so two creators could both pass and the loser's observation would vanish. Sixteen concurrent writers on a brand-new profile all landed, so either your transaction ordering closes that window or I was wrong about it — either way it is not a gap, and I would rather say that than report a suspicion as a finding. Good call making the exception not derive from `SqliteException`: my coordinator catches it by its own type, and a handler written for a busy lock really would have swallowed a lost update.

No action needed from you. If `Save` ever grows a transactional "apply this delta" rather than "write this whole profile", the retry disappears and I will delete the gate for good — that is the shape I would actually want.
