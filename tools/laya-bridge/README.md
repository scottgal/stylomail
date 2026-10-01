# Laya: measuring a local semantic classifier

Laya is an Apache 2.0 decision model whose question schema is **the same schema StyloMail already
speaks**: `noul`, `choice` and `score`, each with `instructions` and `criteria`. It is not a model to
adapt. It is a candidate local implementation of the provider interface in spec §6.

This directory holds the measurement that decides whether it is worth having. It does not hold the
bridge, because the measurement has not yet earned one. See "Where this stands" below.

## What is here

- `measure_corpus.py`: runs Laya over the six Jev corpus messages with StyloMail's own dimension
  definitions, and reports latency, token accounting and raw answers.

The venv and the converted model live in a working directory **deliberately outside the repository**:
about 1.5 GB. Nothing here commits them. Set `LAYA_SPIKES` to that directory before running anything
below.

## Running it

The venv is Python 3.11 and has OpenVINO 2026.4.0 and the fork installed. OpenVINO runs **CPU only on
Apple Silicon**: the GPU plugins are Intel-only, so there is no acceleration to be had on a Mac.

```bash
# from the repository root
"$LAYA_SPIKES/venv/bin/python" tools/laya-bridge/measure_corpus.py \
    --model "$LAYA_SPIKES/laya-ov-int8" \
    --out /tmp/laya-measurement.json
```

The script parses `src/StyloMail.Core/SemanticDimension.cs` for the question text rather than
transcribing it, so the questions asked here cannot drift from the questions StyloMail asks.

It parses **all twelve** dimensions, and it refuses rather than skips. Two defects lived in this
reader and both were silent: an `Id` written as a C# constant (`Id = ConversationalContinuityId`)
is not a string literal, so a literal-only pattern dropped conversational continuity, and a block
that yielded no id was skipped with a `continue`. Either one produced a clean run over eleven
dimensions of twelve, which reads exactly like a clean run over twelve. The reader now resolves
constant references against the `const string` declarations in the same file, and raises with the
block index and the dimensions it did read if any block does not parse.

**Run it as a file, never through `stdin`, `-c`, or a heredoc.** `OVAgent` loads the model with
`multiprocessing` spawn, which re-imports `__main__` by path; from a heredoc that path is `<stdin>` and
every worker dies with `FileNotFoundError`. The answers still print, which is what makes it a trap
rather than an obvious failure.

## Where this stands

**The hosted half of the comparison is missing.** Comparing Laya against the hosted provider needs
`TYPESAFE_API_KEY`, and the environment this was measured in could not export it. Until both sides
exist, "compatible local classifier" is a schema match and not a behavioural one.

What the local half found is in the lane report. In short: the context limit truncates silently and
is confirmed by reproduction, and `confidence` is a restatement of the answer rather than new
information.

**The third finding this section used to carry is withdrawn.** It read: "StyloMail's current criteria
text suppresses the model's signal on the flagship phishing case, which is the finding that decides
the lane." It should not have been written, and not because it was later measured to be false here:
the Laya run asked its questions with the criteria in the prompt and recorded an answer, so
"the criteria suppress the signal" was an inference from a single observation, and it named a cause
that the evidence could not distinguish from any other difference between the prompts. Nothing in
this directory varies the criteria, so nothing in this directory can settle it. The flagship case is
recorded as an answer, with no cause attached.

The isolation since performed belongs to the **Nimble lane and does not transfer to Laya.** It is
noted here only so that the withdrawn sentence is not quietly replaced by a number from another
lane. Holding the user turn constant and varying only the criteria and the guard paragraphs, four
variants three runs each, at twelve dimensions and again at eleven, answered
`semantic.credential_request` **A in every variant**: on the local decision model the criteria are
not what changes that answer. Replaying the shipping adapter's own captured request then isolated
the difference that is real there, which is the **user-turn rendering** of the message (the
structured JSON state answers B three times of three, the same message as delimited text answers A
three times of three, three of eleven dimensions differing),
`tools/nimble-survey/probe_shape_flip.py --replay`. That is a fact about that model and that
adapter. The hosted half of the Laya comparison is still missing, and until it exists the Laya
question stays open rather than decided in either direction.

## Reproducing the credential half

The hosted side is captured by the Jev corpus recorder in `tests/StyloMail.Jev.Tests`, which writes
`tests/fixtures/jev/<case>.response.json` and a provenance sidecar. It is driven by a wrapper script
that is a working tool outside this repository, so a clone does not have it. Run it with the key
exported from a file outside the repository so the value never reaches a command line or any output:

```bash
export TYPESAFE_API_KEY="$(cat /path/to/key)"
/path/to/record-jev-corpus.sh
```

With both halves present the comparison is a join on case name and dimension id.
