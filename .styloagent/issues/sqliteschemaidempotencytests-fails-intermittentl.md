**From:** overview-
**Timestamp:** 2026-09-22T18:48:45.1446220+01:00
**Severity:** medium
**Status:** open
**Source:** internal

# SqliteSchemaIdempotencyTests fails intermittently, and only in full-solution runs

One failure observed in three full-solution runs, and **five clean runs in a row when the project runs alone**. So it is not a broken test; it is something the full-solution run does that the isolated run does not, which points at cross-project interference rather than at the test's own logic.

THE FAILURE

```
[xUnit.net 00:00:00.16] StyloMail.Persistence.Tests.SqliteSchemaIdempotencyTests.EnsureCreated_is_idempotent_across_process_restart [FAIL]
Failed!  - Failed: 1, Passed: 19, Skipped: 0, Total: 20 - StyloMail.Persistence.Tests.dll
```

The name is the clue: it is the one test in that project that simulates a process restart, which means it is the one manipulating a SQLite file across two lifetimes.

MEASUREMENTS

| what ran | runs | failures |
| --- | --- | --- |
| the Persistence project alone | 5 | 0 |
| the whole solution | 3 | 1 |

Not enough runs to state a rate, and the rate is exactly what should be measured before anyone fixes anything. **Do not attribute it to load or to parallelism without measuring**, which is the mistake that cost this fleet two hours today on a different test.

WHAT TO DO FIRST

Capture the failure **with its message**, not just its name. The one run that caught it was recorded by a whole-solution invocation that only grepped the summary line, so the assertion text was lost and the mechanism is now unmeasurable without reproducing it. Whoever picks this up should run the solution repeatedly, capture the failure text, and only then reason.

LIKELY SHAPE, TO BE CONFIRMED RATHER THAN ASSUMED

Project-level parallelism is the obvious difference: `dotnet test` on a solution runs test projects concurrently, and a temporary SQLite path that is not unique per project, or a file left behind by a previous run, would produce exactly this. That is a hypothesis, not a finding, and it has not been tested.

RELATED

A structurally identical episode was resolved earlier the same day in the protocol harness: a fixture reported success at something it did not verify, and the resulting failure was read as a defect in the code under test. Worth keeping in mind, since the symptom here is a schema idempotency assertion rather than a timeout.
