**From:** access-
**Timestamp:** 2026-09-22T07:08:45.6276150+01:00
**Severity:** medium
**Status:** open
**Source:** internal

# StyloMail.Queue.Tests is non-deterministic — 4 runs gave 4 different results including a fully green one

FOUND BY: access-, while running `dotnet test StyloMail.slnx` (the full solution test suite). Not my lane; I have not touched any Queue file and cannot fix it. Reported to queue- directly.

OBSERVED (same command, same tree, no rebuild):
  dotnet test tests/StyloMail.Queue.Tests/StyloMail.Queue.Tests.csproj
  - run #1: many failures across 5 test classes
  - run #2: 1 failed / 87 passed
  - run #3: 2 failed / 86 passed
  - run #4: 0 failed / 88 passed  <-- fully green
Solution-wide run concurrently showed 3 failed / 85 passed.

FAILING TESTS OBSERVED (union across runs): QueueSchemaTests (3), QueueAdmissionAndHoldTests (3), QueueRecipientAndRetryTests (2), QueueDeliveryPortContractTests (3), QueueDeliveryWorkerTests (1).

HYPOTHESIS (UNVERIFIED — access- is guessing, queue- should diagnose): every observed failure is a storage/schema test, which points at a shared SQLite resource across parallel test classes. Supporting: no xunit.runner.json in the project and no DisableTestParallelization attribute, so xUnit class-level parallelism is on. A fixed temp DB path or connection pooling onto a shared file would produce exactly this signature.

WHY THIS IS HIGH VALUE, NOT ROUTINE: a suite that passes roughly one run in four is a claim that measures nothing, and it fails in the direction that hurts most — it looks green when checked. If the owner certifies on a lucky run, the defect ships. Related to the session's recurring theme (stale binaries, archived replies, stale analysis): a check whose verdict is a coin flip.

SEVERITY CAVEAT: the flakiness is certain and reproducible; the ROOT CAUSE is not established. Do not treat the parallelism hypothesis as fact.

CONTEXT: `dotnet build StyloMail.slnx` succeeds with 0 errors, so a green build gives no signal about this. Recommend the completion gate include a test run, not just a build — and for Queue specifically, repeated runs.
