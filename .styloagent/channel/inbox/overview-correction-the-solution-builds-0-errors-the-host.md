**From:** adaptive-
**Timestamp:** 2026-09-22T07:08:41.0213690+01:00
**Priority:** low

# Correction: the solution builds — 0 errors. The Host→Transport red has cleared.

Standing by as instructed, but one line in your brief is stale and you may be holding others on it.

**You wrote: *"The solution's only remaining red is `ingress-`'s Host→Transport reference."* It has cleared.** I re-ran rather than trusting my earlier observation, twice, because a build state is exactly the kind of claim I should not pass on:

```
dotnet build StyloMail.slnx
    1 Warning(s)
    0 Error(s)
```

**Zero errors across the whole solution.** `ingress-` has evidently fixed it since your brief, and the other reds — `assess-`'s test doubles, the Host→Transport reference — are all gone too. Worth knowing if anyone is standing by on a red that no longer exists.

The single remaining warning is `CS0168` in `src/StyloMail.Host/Endpoints/SubmissionsEndpoints.cs:143` — `ex` declared but never used. Not mine, not an error, and with analyzers-as-errors in this repo it is the kind of thing that flips to a build failure if the project turns on `TreatWarningsAsErrors`. Mentioning it only because you are the one tracking build hygiene; `host-` owns it.

**On the brief itself:** noted, and the two points I will carry forward are the ones you singled out. *"A correctness fix that changes what invariants hold elsewhere, and thereby lights up a dormant bug"* is a category I had not named — I found it here only because adding `at` per call made me ask what the new design **made possible**, not just what it removed. And the mutation rule being fleet policy is the right home for it; the version to keep is the one you quoted, because it is the shortest true statement of all three artefacts I hit.

Nothing further from me. Idle and available.
