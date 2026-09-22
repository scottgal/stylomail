**From:** access-
**Timestamp:** 2026-09-23T00:41:02.0948820+01:00
**Priority:** normal

# Jev corpus: harness and replay built and green. Nothing recorded (no credential). One design decision to flag.

`access-`. **Everything that does not need a credential is built and verified. The recording itself is the only thing outstanding, and it needs the key.** Files are in the tree uncommitted for your review.

## What is in

| File | What it does |
|---|---|
| `tests/StyloMail.Jev.Tests/JevCorpus.cs` | One loader for both halves: case discovery, `.eml` parsing through the real MIME adapter, credential lookup, recording IO, provenance record |
| `tests/StyloMail.Jev.Tests/JevCorpusFactAttributes.cs` | `JevLiveFact` skips without a credential; `JevCorpusReplayFact` skips while the corpus is empty |
| `tests/StyloMail.Jev.Tests/JevCorpusRecordingTests.cs` | The recorder: drives the real adapter through a `DelegatingHandler` that tees the raw response body |
| `tests/StyloMail.Jev.Tests/JevCorpusReplayTests.cs` | The replay assertions, plus a machinery self-test |
| `.styloagent/tools/record-jev-corpus.sh` | Reproducible recording; refuses loudly; never prints the key |

Plus a `ProjectReference` from `StyloMail.Jev.Tests` to `StyloMail.Mime`, which I flagged in advance. Nothing in `src/` was touched.

## Verified, not assumed

- **Jev suite: 16 passed, 3 skipped, 0 failed.** The 15 pre-existing tests are untouched.
- **The script exits 2 with no credential** and names both places, measured without a pipe this time because my first measurement read `head`'s exit code rather than the script's.
- **The key cannot reach the output.** I ran the script with a canary value in `TYPESAFE_API_KEY` and grepped the output for it: zero occurrences.
- **0 em-dashes** across all five new files.
- **Zero recordings present**, which is correct.

## The design decision I want you to look at

**Every corpus test skipped, which meant the entire record-and-replay path was unproven.** That is the failure this project has spent two days finding, so I did not leave it there: `TheReplayMachineryWorksOnASyntheticBody` exercises the whole path in memory, covering all three availability states at once, an answered dimension, a dimension asked and left unanswered, and continuity not asked at all.

**That synthetic body is never written to `tests/fixtures/jev/`.** A synthetic body committed as a recording is exactly what the `source: live` provenance field exists to prevent, and it would poison the corpus for everyone downstream. It lives in the test source only.

Two smaller guards in the same spirit: the recorder **refuses to write** a recording in which no dimension came back `Available`, because committing a broken shape would make the replay test assert on the breakage as though it were intended. And the replay test asserts the recorded `RequestedModel` equals the `ReportedModel`, so an alias that resolved elsewhere cannot hide in a fixture.

## What the replay test actually checks, since it is the load-bearing half

The expectations come from the recording, not from a second hand-written file. For each case it parses the recorded body, works out which dimensions were answered and with what probability, and requires the adapter to agree, including `Unavailable` for a dimension asked and not answered and `NotApplicable` for continuity with no prior context. It also asserts `Confidence` is null on every Noul, so a fabricated certainty fails the suite.

A hand-written expectation beside a recording can drift from it, and then the test passes while asserting something the provider never said.

## What I need

**A credential.** When one appears, either run `.styloagent/tools/record-jev-corpus.sh` or set `TYPESAFE_API_KEY` and run the recording test; it unskips by itself. The replay test then unskips by itself too, since its gate is the presence of a recording rather than an environment variable. Neither needs anyone to remember to remove a gate.

**And a ruling on the `jevkey.pvt` question from my last message**, which is still open and still moot while the file is absent. My default remains: environment first, the file as fallback, neither value ever rendered.
