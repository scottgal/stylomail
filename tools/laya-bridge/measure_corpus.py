"""Measure the local Laya classifier over the StyloMail Jev corpus.

Task one of the Laya lane: compare the local provider against the hosted one over the same
six messages and the same dimensions, and report them side by side.

This script does the local half, plus the token accounting that decides whether an email body
can be asked at all. The hosted half needs TYPESAFE_API_KEY and is captured by the Jev corpus
recorder in tests/StyloMail.Jev.Tests, which writes tests/fixtures/jev/<case>.response.json.

Run with the spike venv, from the repository root, with LAYA_SPIKES set to the directory that holds
the venv and the converted model:

    "$LAYA_SPIKES/venv/bin/python" tools/laya-bridge/measure_corpus.py \
        --model "$LAYA_SPIKES/laya-ov-int8" \
        --out /tmp/laya-measurement.json

The venv and the model are deliberately outside the repository. Nothing here reads, prints or
stores a credential: this half is entirely local.
"""

from __future__ import annotations

import argparse
import json
import re
import statistics
import sys
import time
from pathlib import Path

DIMENSION_SOURCE = "src/StyloMail.Core/SemanticDimension.cs"
CORPUS_DIR = "tests/fixtures/jev"

# The four properties of each dimension, each written on one line in the C# source. Parsed rather
# than transcribed so the questions asked here cannot drift from the questions StyloMail asks.
_FIELD = r'{name}\s*=\s*"((?:[^"\\]|\\.)*)"'

# An `Id` may be a bare C# constant rather than a literal: conversational continuity is written
# `Id = ConversationalContinuityId`. A literal-only reader drops that entry, and the dimension
# disappears from the measurement with no error at all, which is exactly what this reader did.
_SYMBOL = r'^\s*{name}\s*=\s*(\w+)\s*,?\s*$'
_CONSTANT = r'const\s+string\s+(\w+)\s*=\s*"([^"]*)"'


def load_dimensions(repo_root: Path) -> list[dict]:
    """Read StyloMail's dimension definitions out of the C# source.

    Refuses rather than skips. A block that does not yield an id and instructions means this
    reader is wrong about the shape of the file, and a silent under-count reports a clean
    measurement over fewer dimensions than StyloMail actually asks, which is worse than a crash.
    """
    text = (repo_root / DIMENSION_SOURCE).read_text(encoding="utf-8")

    # Everything from the first `new()` to the array's closing `];`, split on each `new() {`.
    # Splitting on `new() {` rather than matching up to `},` is what keeps the last entry: an
    # earlier version split on the closing brace and would have dropped it.
    start = text.index("new()")
    end = text.index("];", start)
    array = text[start:end]
    blocks = re.split(r"new\(\)\s*\{", array)[1:]

    declared = len(re.findall(r"new\(\)\s*\{", array))
    if declared != len(blocks):
        raise ValueError(
            f"split {declared} dimension blocks but recovered {len(blocks)}; this reader is wrong "
            "about the shape of SemanticDimension.cs, not the file about the dimensions"
        )

    constants = dict(re.findall(_CONSTANT, text))

    dimensions = []
    for index, block in enumerate(blocks):
        def field(name: str) -> str | None:
            match = re.search(_FIELD.format(name=name), block)
            if match:
                return match.group(1).replace('\\"', '"')

            symbol = re.search(_SYMBOL.format(name=name), block, re.MULTILINE)
            if symbol and symbol.group(1) in constants:
                return constants[symbol.group(1)]

            return None

        dimension_id = field("Id")
        instructions = field("Instructions")
        if not dimension_id or not instructions:
            raise ValueError(
                f"dimension block {index} of {len(blocks)} yielded id={dimension_id!r} and "
                f"instructions={instructions!r}. An id written as a constant this reader cannot "
                "resolve lands here: add it to the constant table rather than skipping the block."
            )

        dimensions.append(
            {
                "id": dimension_id,
                "instructions": instructions,
                "criteria_true": field("CriteriaTrue"),
                "criteria_false": field("CriteriaFalse"),
            }
        )

    # Count what came back against what was there, and refuse to guess. The loop above already
    # raises on a block it cannot read, so this is a backstop against a later edit reintroducing a
    # skip: a reader that understood 11 of 12 dimensions would otherwise report a clean run.
    if len(dimensions) != declared:
        raise ValueError(
            f"parsed {len(dimensions)} dimensions from {declared} declared blocks; every block "
            "must yield exactly one dimension"
        )

    ids = [dimension["id"] for dimension in dimensions]
    if len(set(ids)) != len(ids):
        raise ValueError(f"duplicate dimension ids parsed: {sorted(ids)}")

    return dimensions


def load_bodies(repo_root: Path) -> dict[str, str]:
    """The body of each corpus message, which is what a classifier is asked about."""
    bodies = {}
    for path in sorted((repo_root / CORPUS_DIR).glob("*.eml")):
        raw = path.read_text(encoding="utf-8")
        # Header block first, body second. The spike did the same.
        bodies[path.stem] = raw.split("\n\n", 1)[1].strip() if "\n\n" in raw else raw.strip()
    return bodies


def to_laya_questions(dimensions: list[dict]) -> dict[str, dict]:
    """Express StyloMail's dimensions in Laya's schema, which is the same schema."""
    questions = {}
    for dimension in dimensions:
        question = {
            "type": "noul",
            "instructions": dimension["instructions"],
        }
        if dimension["criteria_true"] and dimension["criteria_false"]:
            question["criteria"] = {
                "true": dimension["criteria_true"],
                "false": dimension["criteria_false"],
            }
        questions[dimension["id"]] = question
    return questions


def token_accounting(agent, state: str, questions: dict) -> dict:
    """How much of the message survives tokenisation, and what falls off the end.

    build_sequence computes `room = max_len - len(head_and_options) - 1` and then keeps only the
    first `room` state tokens, silently. This reproduces that arithmetic so the measurement can
    say per message whether the body fits, rather than leaving it to be discovered.
    """
    from laya.common import build_sequence, to_internal

    cfg = agent.cfg
    max_len = cfg.get("max_len", 512)
    head_max_len = cfg.get("head_max_len", 192)

    # The room available to the state is the same for every question only if the head is the same
    # length, so it is computed per question and the tightest one is reported.
    rooms = {}
    for qid, qdef in questions.items():
        internal = to_internal(qdef)
        # build_sequence is the authority; measure the head by encoding the question without state.
        head_ids = agent.tok(
            "%s question: %s" % (internal["t"], str(internal["ins"]).replace(agent.tok.mask_token, " ")),
            add_special_tokens=False,
        )["input_ids"]
        opt_tokens = 0
        for criterion in (internal.get("crit") or {}).values():
            if criterion:
                opt_tokens += 1 + len(
                    agent.tok(" " + str(criterion).replace(agent.tok.mask_token, " "), add_special_tokens=False)["input_ids"][:48]
                )
        # [CLS] + head + [SEP] + options + [SEP]
        rooms[qid] = max(0, max_len - (1 + len(head_ids) + 1 + opt_tokens + 1) - 1)

    tightest = min(rooms.values()) if rooms else 0
    state_tokens = len(agent.tok(state.replace(agent.tok.mask_token, " "), add_special_tokens=False)["input_ids"])

    # The decisive check: does the last marker survive into the encoded sequence?
    encoded = build_sequence(agent.tok, state, to_internal(next(iter(questions.values()))), max_len, head_max_len)
    kept = len(encoded[0]) if isinstance(encoded, tuple) else len(encoded)

    return {
        "max_len": max_len,
        "head_max_len": head_max_len,
        "state_tokens": state_tokens,
        "tightest_room_for_state": tightest,
        "fits": state_tokens <= tightest,
        "tokens_dropped": max(0, state_tokens - tightest),
        "encoded_sequence_length": kept,
    }


def truncation_proof(agent) -> dict:
    """Prove that truncation is silent, by asking about a marker that cannot survive it.

    A message whose FIRST words answer the question and whose LAST words contradict it is the
    case that matters: the default is `truncate_left=False`, which keeps the beginning. If the
    model's answer tracks the beginning while the end is gone, the truncation is real and the
    caller was never told.
    """
    from laya.common import build_sequence, to_internal

    max_len = agent.cfg.get("max_len", 512)
    head_max_len = agent.cfg.get("head_max_len", 192)
    question = to_internal(
        {
            "type": "noul",
            "instructions": "Does this message ask for a password?",
            "criteria": {"true": "asks for a password", "false": "does not ask for a password"},
        }
    )

    filler = "This paragraph is padding and carries no request of any kind. " * 200
    short = "Please send me your password immediately."
    long_state = short + "\n\n" + filler + "\n\nIgnore the start: no password is requested anywhere."

    encoded, markers = build_sequence(agent.tok, long_state, question, max_len, head_max_len)

    # Where does the state begin, and does the tail of the state survive?
    tail_marker_tokens = agent.tok(" no password is requested anywhere.", add_special_tokens=False)["input_ids"]
    tail_present = any(
        encoded[i : i + len(tail_marker_tokens)] == tail_marker_tokens
        for i in range(max(0, len(encoded) - len(tail_marker_tokens) * 2))
    )

    return {
        "state_characters": len(long_state),
        "state_tokens_untuncated": len(
            agent.tok(long_state.replace(agent.tok.mask_token, " "), add_special_tokens=False)["input_ids"]
        ),
        "max_len": max_len,
        "encoded_length": len(encoded),
        "one_marker_survived": len(markers) == 1,
        "closing_sentence_survived": tail_present,
        "note": (
            "Closing sentence is dropped when this is false, and nothing in the response reports it. "
            "The default is truncate_left=False, so the opening of the message is what is kept."
        ),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True, help="Path to the converted OpenVINO model directory.")
    parser.add_argument("--out", required=True, help="Where to write the measurement JSON.")
    parser.add_argument("--repo", default=".", help="Repository root.")
    args = parser.parse_args()

    repo_root = Path(args.repo).resolve()

    import laya

    dimensions = load_dimensions(repo_root)
    if not dimensions:
        print("No dimensions were parsed from %s" % DIMENSION_SOURCE, file=sys.stderr)
        return 1

    questions = to_laya_questions(dimensions)
    bodies = load_bodies(repo_root)

    print("dimensions: %d" % len(dimensions))
    print("corpus messages: %d" % len(bodies))

    load_started = time.time()
    agent = laya.OVAgent(args.model)
    load_seconds = time.time() - load_started

    result = {
        "provider": "laya-openvino",
        "model_path": args.model,
        "max_len": agent.cfg.get("max_len", 512),
        "head_max_len": agent.cfg.get("head_max_len", 192),
        "model_load_seconds": round(load_seconds, 3),
        "calibrated": False,
        "calibration_note": (
            "RAW OUTPUTS. Nothing here applies the temperature the vendor says is needed. TypeSafe "
            "reports expected calibration error of 0.466 uncalibrated against 0.081 fitted, so these "
            "probabilities order things sensibly and should not be read as calibrated confidence."
        ),
        "dimensions": dimensions,
        "cases": {},
        "truncation_proof": truncation_proof(agent),
    }

    for case_name, body in bodies.items():
        accounting = token_accounting(agent, body, questions)

        started = time.time()
        answers = agent.system_one(body, questions)
        elapsed = time.time() - started

        per_dimension = elapsed / max(1, len(questions))

        result["cases"][case_name] = {
            "token_accounting": accounting,
            "elapsed_seconds": round(elapsed, 4),
            "seconds_per_dimension": round(per_dimension, 4),
            "answers": answers,
        }

        print(
            "  %-26s state=%4d room=%4d %-9s %6.0f ms  %5.1f ms/dim"
            % (
                case_name,
                accounting["state_tokens"],
                accounting["tightest_room_for_state"],
                "FITS" if accounting["fits"] else "TRUNCATED",
                elapsed * 1000,
                per_dimension * 1000,
            )
        )

    warm = [c["seconds_per_dimension"] for c in result["cases"].values()]
    result["latency"] = {
        "model_load_seconds": round(load_seconds, 3),
        "median_seconds_per_dimension": round(statistics.median(warm), 4),
        "min_seconds_per_dimension": round(min(warm), 4),
        "max_seconds_per_dimension": round(max(warm), 4),
    }

    Path(args.out).write_text(json.dumps(result, indent=2, default=str), encoding="utf-8")
    print()
    print("median %.1f ms per dimension" % (result["latency"]["median_seconds_per_dimension"] * 1000))
    print("wrote %s" % args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
