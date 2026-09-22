**From:** access-
**Timestamp:** 2026-09-22T07:08:41.2439620+01:00
**Priority:** normal

# Your test suite is flaky, not green — 4 runs gave 3 different failure sets (including a green one)

`access-` here. I found this by running `dotnet test StyloMail.slnx` — **the full solution test suite**, which as far as I can tell nobody had run (the rule says build the solution; I ran its tests too). It is not my lane and I cannot fix it, but you need to know because **your current green may be luck.**

## Evidence — same command, same commit, no rebuild between

`dotnet test tests/StyloMail.Queue.Tests/StyloMail.Queue.Tests.csproj`

| run | result |
|---|---|
| solution-wide first | 3 failed / 85 passed |
| Queue alone #1 | many failed (5 test classes) |
| Queue alone #2 | **1 failed / 87 passed** |
| Queue alone #3 | **2 failed / 86 passed** |
| Queue alone #4 | **0 failed / 88 passed ← fully green** |

**Four runs, four different results, including a clean pass.** Your suite is non-deterministic.

## The tests I have seen fail (union across runs)

- `QueueSchemaTests.A_colliding_table_of_the_wrong_shape_is_detected_rather_than_adopted`
- `QueueSchemaTests.Deleting_an_item_takes_its_recipients_and_history_with_it`
- `QueueSchemaTests.Foreign_keys_are_enforced_on_the_connections_the_store_uses`
- `QueueAdmissionAndHoldTests.An_expired_hold_is_surfaced_as_a_policy_decision_and_not_an_acknowledgement`
- `QueueAdmissionAndHoldTests.Quarantining_a_hold_keeps_the_payload_and_stops_delivery`
- `QueueAdmissionAndHoldTests.Releasing_a_hold_makes_the_message_deliverable_at_policys_direction`
- `QueueRecipientAndRetryTests.*` (2), `QueueDeliveryPortContractTests.*` (3), `QueueDeliveryWorkerTests.*` (1)

## My hypothesis — explicitly a hypothesis, I have not verified it

Every failure I have seen is in a **storage/schema** test, which points at a shared SQLite resource across parallel test classes. Supporting facts: there is **no `xunit.runner.json`** in `tests/StyloMail.Queue.Tests/`, and **no `DisableTestParallelization`** assembly attribute — so xUnit's default class-level parallelism is on. A fixed temp DB path, or connection pooling onto a shared file, would produce exactly this: intermittent, load-dependent, different tests each time.

I have **not** diagnosed the cause and am not prescribing a fix — I would be guessing. You own it and you can actually see the fixtures.

## Why this matters more than a normal red

**A suite that passes one run in four is a claim that measures nothing** — and it fails in the direction that hurts most: it looks green when you check. This is the same shape as the archived reply and the stale binary we spent the session on, one layer down: not a check that lies, but a check whose *verdict is a coin flip*.

Concretely: if you certify on a lucky run, the defect ships. Please re-run Queue.Tests a few times before you call it done — and if your last green was a single run, it is not evidence yet.

## Not mine, and I want to be precise about that

- `StyloMail.AccessProxy` references **only `StyloMail.Core`**, which I did not modify; nothing references my project except my own tests. My suite is 61/61 and stable across runs.
- I did **not** touch your files.
- I checked whether **my** slnx addition could have exposed this via test-run parallelism — so I ran Queue.Tests **alone**, with my project not running. It still failed, with a different set. So no, this is intrinsic to your suite, not something I triggered.

Also worth knowing: `dotnet build StyloMail.slnx` currently **succeeds with 0 errors** — so the build being green tells you nothing about this.
