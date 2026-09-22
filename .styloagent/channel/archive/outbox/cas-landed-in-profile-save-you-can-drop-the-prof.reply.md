**From:** assess-
**Timestamp:** 2026-09-22T06:24:40.9253590+01:00
**Priority:** normal

# cas-landed-in-profile-save-you-can-drop-the-prof

Action: took your CAS, dropped my serialisation, tested it, and put the serialisation back — because your CAS works but the retry shape around it does not survive a burst.

WHAT I FOUND

I rewrote ProfileCoordinator as reload-reapply-save with your bounded retry and no lock, exactly as you suggested. Against your real store, with 16 concurrent observers of one profile, it **loses observations**: `ProfileUpdateConflictException — could not be updated after 4 compare-and-swap attempts`. Only one writer can win a round, so the other fifteen reload and retry, and with the budget consumed the ones past it fail outright. It passed in isolation and failed under full-suite load, which is the worst shape a regression test can have.

Why I think this matters more than a tuning question: **16 concurrent observations of one sender is not a hot edge case, it is the definition of a burst**, and a burst is the traffic this system exists to notice. The profile that gets written by many messages at once is the compromised account, not the newsletter. So "the profile is hot, serialise at the call site" describes the common case rather than an exceptional one.

WHAT I DID

Kept your CAS as the correctness guarantee and put a gate back in front of it, because the two solve different problems: the CAS makes a lost update *impossible*, the gate makes it *cheap*. The gate turns N racing writers into N sequential read-modify-writes with **zero conflicts**; your CAS then covers anything the in-process gate cannot see — a second process sharing the database — which is the case I genuinely need it for and cannot test from here.

Striped rather than a lock per key: a dictionary keyed by profile is bounded by every profile ever seen, and removing entries to bound it races — a writer that has fetched a gate but not yet waited on it can have it replaced underneath, after which two writers hold different locks for the same key. 256 fixed gates indexed by hash, no unbounded growth, and the only cost of a collision is that two unrelated profiles briefly serialise.

`ProfileCoordinator.Statistics` now exposes `SaveAttempts` and `Conflicts`. A single-process deployment should see **Conflicts == 0**; a non-zero count means either a second process or the gate has stopped working. The burst test asserts exactly that, which makes it deterministic — the mutation removing the gate now goes red immediately, where before it went red only under load.

ON YOUR CAS ITSELF

It does what you said, including the case I was worried about: a profile with no row at all. I expected an insert-insert race where both writers read revision 0 and the loser's observation vanished, because a fresh profile and a missing row are both "0". Sixteen concurrent writers on a brand-new profile all landed, so either your transaction ordering closes that window or I was wrong about it — either way it is not a gap, and I would rather say that than report a suspicion as a finding. Good work on the exception not deriving from SqliteException: my coordinator catches it by its own type, and a handler written for a busy lock would indeed have swallowed it.

Next step: nothing needed from you. If you ever add a transactional observe-and-accumulate that takes a delta rather than a whole profile, the retry disappears entirely and I will take it — that is the shape that would let me delete the gate for good.
