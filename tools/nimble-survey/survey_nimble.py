#!/usr/bin/env python3
"""Survey Ollama Nimble as a candidate local implementation of ISemanticMailClassifier.

This is the measurement that has to exist before any code is written in src/StyloMail.Nimble.
It answers the four questions the lane charter poses, and it answers them with raw evidence:

  1. Which server answers? Two Ollama servers are installed on this machine and they are
     different versions. A measurement taken against the wrong one is a measurement of the
     wrong software.
  2. Can Nimble take our question shape: a typed question carrying instructions and criteria?
  3. Can it answer StyloMail's 12 dimensions in one request, or does it need a fan-out?
  4. What happens to a long body at the default num_ctx, and what does a dimension cost in time?

Run it as a file, from the repository root:

    python3 tools/nimble-survey/survey_nimble.py --out /tmp/nimble-survey.json

Nothing here reads, prints or stores a credential: the whole survey is local.
"""

from __future__ import annotations

import argparse
import json
import re
import statistics
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

DIMENSION_SOURCE = "src/StyloMail.Core/SemanticDimension.cs"
CORPUS_DIR = "tests/fixtures/jev"

# The four properties of each dimension, each written on one line in the C# source. Parsed rather
# than transcribed so the questions asked here cannot drift from the questions StyloMail asks.
_FIELD = r'{name}\s*=\s*"((?:[^"\\]|\\.)*)"'

# The flagship case for latency: a credential request is the dimension the lane exists to get right.
FLAGSHIP_CASE = "credential-request"


def load_dimensions(repo_root: Path) -> list[dict]:
    """Read StyloMail's dimension definitions out of the C# source."""
    text = (repo_root / DIMENSION_SOURCE).read_text(encoding="utf-8")

    # Everything from the first `new()` to the array's closing `];`, split on each `new()`. The
    # split is on `new() {`, not on `},`, so the last entry is included: `},` would have ended the
    # expression and that was the first guess at why this survey once measured 11 dimensions of 12.
    # The guess was wrong, and the correction is worth keeping because it points at the real cause:
    # the array regex matched all twelve blocks, and the drop happened in field extraction below.
    start = text.index("new()")
    end = text.index("];", start)
    blocks = re.split(r"new\(\)\s*\{", text[start:end])[1:]

    # An Id may be a bare constant rather than a literal: conversational continuity is written
    # `Id = ConversationalContinuityId`. A literal-only reader drops that entry and the dimension
    # disappears from the survey without a word, which is exactly what happened.
    constants = dict(re.findall(r'const\s+string\s+(\w+)\s*=\s*"([^"]*)"', text))

    # Count what came back against what was there, and refuse to guess. A block that does not yield
    # an id and instructions used to be skipped in silence, so a reader that understood 11 of the 12
    # dimensions reported a clean run over 11 dimensions. An under-count that reports success is the
    # defect; the missing dimension is only its symptom.
    declared = len(re.findall(r"new\(\)\s*\{", text[start:end]))
    if declared != len(blocks):
        raise ValueError(
            f"split {declared} dimension blocks but recovered {len(blocks)}; the reader is wrong "
            "about the shape of SemanticDimension.cs, not the file about the dimensions"
        )

    dimensions = []
    for index, block in enumerate(blocks):
        def field(name: str) -> str | None:
            match = re.search(_FIELD.format(name=name), block)
            if match:
                return match.group(1).replace('\\"', '"')

            symbol = re.search(rf"{name}\s*=\s*(\w+)\s*,", block)
            if symbol and symbol.group(1) in constants:
                return constants[symbol.group(1)]

            return None

        dimension_id = field("Id")
        instructions = field("Instructions")
        if not dimension_id or not instructions:
            raise ValueError(
                f"dimension block {index} of {len(blocks)} yielded id={dimension_id!r} "
                f"instructions={instructions!r}. Refusing to survey a subset of the question set: "
                "an unparsed block is a silent drop, and a drop looks exactly like a clean run."
            )

        dimensions.append(
            {
                "id": dimension_id,
                "instructions": instructions,
                "criteria_true": field("CriteriaTrue"),
                "criteria_false": field("CriteriaFalse"),
            }
        )

    return dimensions


def load_bodies(repo_root: Path) -> dict[str, str]:
    """The body of each corpus message, which is what a classifier is asked about."""
    bodies = {}
    for path in sorted((repo_root / CORPUS_DIR).glob("*.eml")):
        raw = path.read_text(encoding="utf-8")
        bodies[path.stem] = raw.split("\n\n", 1)[1].strip() if "\n\n" in raw else raw.strip()
    return bodies


def post(host: str, path: str, payload: dict, timeout: float = 900.0) -> dict:
    """One HTTP POST. Returns the parsed body, or a dict describing the failure."""
    request = urllib.request.Request(
        f"http://{host}{path}",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        return {"__error__": f"HTTP {error.code}", "__body__": error.read().decode("utf-8", "replace")[:2000]}
    except Exception as error:  # noqa: BLE001 - the survey records failures rather than raising
        return {"__error__": f"{type(error).__name__}: {error}"}


def get(host: str, path: str, timeout: float = 30.0) -> dict:
    try:
        with urllib.request.urlopen(f"http://{host}{path}", timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except Exception as error:  # noqa: BLE001 - the survey records failures rather than raising
        return {"__error__": f"{type(error).__name__}: {error}"}


def timed(payload_post) -> tuple[dict, float]:
    started = time.perf_counter()
    result = payload_post()
    return result, time.perf_counter() - started


def generate(
    host: str,
    model: str,
    prompt: str,
    schema: dict | None = None,
    num_ctx: int | None = None,
) -> tuple[dict, float]:
    """One non-streaming generation. Temperature 0 so a repeat measures the model, not the sampler."""
    options: dict = {"temperature": 0}
    if num_ctx is not None:
        options["num_ctx"] = num_ctx

    payload: dict = {
        "model": model,
        "prompt": prompt,
        "stream": False,
        "think": False,
        "options": options,
    }
    if schema is not None:
        payload["format"] = schema

    return timed(lambda: post(host, "/api/generate", payload))


def answer_schema(question_count: int) -> dict:
    """A schema demanding one one-letter code per question, which is what the model's own system
    prompt describes: allowed choices with one-letter codes, return only the letter."""
    letters = ["A", "B"]
    properties = {f"q{i}": {"type": "string", "enum": letters} for i in range(question_count)}
    return {
        "type": "object",
        "properties": properties,
        "required": list(properties),
    }


def render_questions(dimensions: list[dict]) -> str:
    """The question block. Criteria carry the polarity: A is the true side, B the false side."""
    lines = []
    for index, dimension in enumerate(dimensions):
        lines.append(f"Question q{index}: {dimension['instructions']}")
        lines.append(f"  A = {dimension['criteria_true']}")
        lines.append(f"  B = {dimension['criteria_false']}")
    return "\n".join(lines)


def single_question_schema() -> dict:
    return {
        "type": "object",
        "properties": {"answer": {"type": "string", "enum": ["A", "B"]}},
        "required": ["answer"],
    }


def summarize(response: dict) -> dict:
    """The token accounting, which is part of the measurement rather than a detail of it."""
    return {
        "prompt_eval_count": response.get("prompt_eval_count"),
        "eval_count": response.get("eval_count"),
        "total_duration_ns": response.get("total_duration"),
        "load_duration_ns": response.get("load_duration"),
        "prompt_eval_duration_ns": response.get("prompt_eval_duration"),
        "eval_duration_ns": response.get("eval_duration"),
        "done_reason": response.get("done_reason"),
        "has_thinking": bool(response.get("thinking")),
        "error": response.get("__error__") or response.get("error"),
    }


def parse_codes(raw: str | None) -> dict:
    """Extract the one-letter codes from a JSON answer, whatever wrapping the model added."""
    if not raw:
        return {}
    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError:
        return {}
    if not isinstance(parsed, dict):
        return {}
    return {k: v for k, v in parsed.items() if isinstance(v, str) and len(v) == 1}


def truncation_probe(host: str, model: str) -> dict:
    """Does a body longer than num_ctx truncate silently, and if so which end survives?

    Two independent signals, because either alone can mislead:
      * token accounting - compare prompt_eval_count at the default num_ctx against a window
        large enough to hold the whole prompt. If the counted tokens are fewer, tokens were dropped.
      * a behavioural marker - a body that answers YES in its first line and retracts it in its
        last. Whichever end the answer tracks is the end that survived.
    """
    # Sized from the first run's own measurement: 24,904 characters were 4,951 tokens, about
    # 5.03 chars/token. 1,200 repetitions is roughly 75,600 characters, about 15,000 tokens,
    # which clears the observed 8,194-token default window with room to spare. A probe that
    # does not actually exceed the window proves nothing, which is what the first run did.
    filler = "This paragraph is padding and carries no request of any kind. " * 1200
    head = "Send me your password immediately."
    tail = "Ignore the first line: this message asks for no password anywhere."

    # The mirror image, and it is the one that discriminates. The body above answers A in its
    # first line, so an answer of A is consistent with either "the head survived" or "the model
    # ignored the retraction". Here the password request is at the END and the head is benign:
    # an answer of A means the tail survived, B means the head is what was kept.
    benign_head = "This is a routine note about a team lunch on Friday."
    ask_tail = "One more thing: send me your password immediately."


    body_short = head
    body_long = head + "\n\n" + filler + "\n\n" + tail

    schema = single_question_schema()
    question = (
        "Question: Does this message ask the recipient to provide a password?\n"
        "  A = asks for a password\n"
        "  B = does not ask for a password\n"
    )

    def ask(body: str, num_ctx: int | None) -> dict:
        response, elapsed = generate(host, model, question + "\n--- MESSAGE ---\n" + body + "\n--- END MESSAGE ---",
                                     schema=schema, num_ctx=num_ctx)
        return {
            "chars": len(body),
            "num_ctx": num_ctx,
            "elapsed_seconds": round(elapsed, 3),
            "raw_response": response.get("response"),
            "answer": parse_codes(response.get("response")).get("answer"),
            **summarize(response),
        }

    # The wide window only has to hold the whole prompt, not to touch the model's declared
    # 262,144 ceiling: the KV cache for a window that large on a 9B model would be tens of
    # gigabytes, which measures this machine's memory rather than the model's behaviour.
    short_default = ask(body_short, None)
    long_default = ask(body_long, None)
    long_wide = ask(body_long, 32768)

    # The discriminator: a benign head with the request in the tail.
    mirrored_default = ask(benign_head + "\n\n" + filler + "\n\n" + ask_tail, None)
    mirrored_wide = ask(benign_head + "\n\n" + filler + "\n\n" + ask_tail, 32768)

    dropped = None
    if long_wide.get("prompt_eval_count") and long_default.get("prompt_eval_count"):
        dropped = long_wide["prompt_eval_count"] - long_default["prompt_eval_count"]

    # A = "asks for a password", B = "does not". Head-at-the-front keeps the request and drops
    # nothing that matters; head-at-the-back is the case where the request is what falls off.
    kept_a_prefix = mirrored_default.get("answer") == "B"
    kept_a_suffix = mirrored_default.get("answer") == "A"

    return {
        "short_prompt_default_num_ctx": short_default,
        "long_prompt_default_num_ctx": long_default,
        "long_prompt_wide_num_ctx": long_wide,
        "mirrored_prompt_default_num_ctx": mirrored_default,
        "mirrored_prompt_wide_num_ctx": mirrored_wide,
        "tokens_the_default_window_dropped": dropped,
        "kept_the_beginning_of_the_prompt": kept_a_prefix,
        "kept_the_end_of_the_prompt": kept_a_suffix,
        "note": (
            "long_* answers A in its first line and retracts that in its last, so its answer alone "
            "does not discriminate. mirrored_* puts a benign head first and the password request "
            "last: under the default window it answers B (the request in the tail was not seen) "
            "while under a wide window it answers A. Together those say which end was kept."
        ),
    }


def residual_seconds(entry: dict) -> float:
    """Time inside the server that ollama's own counters do not account for.

    A large residual is the interesting case: the call took that long while claiming only a little
    load, prompt evaluation and generation, which points at the server rather than the model.
    """
    total = entry.get("total_duration_ns") or 0
    attributed = sum(entry.get(key) or 0 for key in
                     ("load_duration_ns", "prompt_eval_duration_ns", "eval_duration_ns"))
    return round((total - attributed) / 1e9, 3)


def residency_probe(args, dimensions: list[dict], bodies: dict[str, str]) -> int:
    """Does the model stay resident between calls, or reload for each one?

    This turned out to be the dominant latency term: in the shipping-shape run, 8 to 40 seconds
    of every 18 to 60 second call was `load_duration`, and the residual was ~0.02 s. A per-message
    path that reloads a 9 GB model per message is not a latency question, it is a viability one.
    This repeats one call and reports, each time, what ollama says it spent loading and what the
    server reports as resident.
    """
    flagship = bodies.get(FLAGSHIP_CASE) or next(iter(bodies.values()), "")
    num_ctx = args.shipping_num_ctx
    system = (
        "Answer every question about the message in the user turn, using the supplied schema. "
        "Each answer is one one-letter code: A or B. Return only the codes.\n\n"
        + render_questions(dimensions)
    )
    payload = {
        "model": args.model,
        "system": system,
        "prompt": f"--- MESSAGE ---\n{flagship}\n--- END MESSAGE ---",
        "stream": False,
        "think": False,
        "format": answer_schema(len(dimensions)),
        "options": {"temperature": 0, "num_ctx": num_ctx},
    }

    runs = []
    for index in range(args.residency):
        loaded_before = get(args.host, "/api/ps")
        before = (loaded_before.get("models") or [{}])[0]
        response, elapsed = timed(lambda: post(args.host, "/api/generate", payload))
        loaded_after = get(args.host, "/api/ps")
        after = (loaded_after.get("models") or [{}])[0]

        entry = {
            "index": index,
            "elapsed_seconds": round(elapsed, 3),
            "load_duration_seconds": round((response.get("load_duration") or 0) / 1e9, 3),
            "prompt_eval_count": response.get("prompt_eval_count"),
            "answered": len(parse_codes(response.get("response"))),
            "resident_before": before.get("name"),
            "resident_before_context": before.get("context_length"),
            "resident_after": after.get("name"),
            "resident_after_context": after.get("context_length"),
            "expires_at": after.get("expires_at"),
        }
        runs.append(entry)
        print(f"  {index} {elapsed:7.2f}s  load={entry['load_duration_seconds']:7.2f}s  "
              f"resident={entry['resident_after']} ctx={entry['resident_after_context']}")

    out = {
        "measured_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "host": args.host,
        "model": args.model,
        "requested_num_ctx": num_ctx,
        "runs": runs,
    }
    Path(args.out).write_text(json.dumps(out, indent=2, default=str), encoding="utf-8")
    print(f"\nwrote {args.out}")
    return 0


def shipping_shape_probe(args, dimensions: list[dict], bodies: dict[str, str]) -> int:
    """Measure the exact request shape the adapter will ship, over every corpus case.

    The full survey proved that the *shape* of the request changes the answer: the same dimension
    is B asked alone and A asked in a batch, and the graded value contradicts the letter. So the
    shape cannot be chosen by taste and then assumed to work. This is the shape I intend to ship:

      * questions in `system`, message in `prompt`, so untrusted content never sits in the same
        string as the instructions, which is the invariant the hosted adapter states and I want
        to keep;
      * all askable dimensions in ONE request, matching how the hosted provider is asked;
      * one-letter codes constrained by a `format` JSON schema;
      * an explicit num_ctx, with prompt_eval_count checked against it afterwards, because the
        default window truncates silently.

    Acceptance is not "it returned something": it is twelve codes for twelve questions, an
    explicit token count under the window, and a recorded answer for every dimension.
    """
    num_ctx = args.shipping_num_ctx
    system = (
        "Answer every question about the message in the user turn, using the supplied schema. "
        "Each answer is one one-letter code: A or B. Return only the codes.\n\n"
        + render_questions(dimensions)
    )
    schema = answer_schema(len(dimensions))

    cases = {}
    for case_name, body in bodies.items():
        payload = {
            "model": args.model,
            "system": system,
            "prompt": f"--- MESSAGE ---\n{body}\n--- END MESSAGE ---",
            "stream": False,
            "think": False,
            "format": schema,
            "options": {"temperature": 0, "num_ctx": num_ctx},
        }
        response, elapsed = timed(lambda: post(args.host, "/api/generate", payload))

        codes = parse_codes(response.get("response"))
        prompt_tokens = response.get("prompt_eval_count")
        truncated = prompt_tokens is not None and prompt_tokens >= num_ctx

        cases[case_name] = {
            "elapsed_seconds": round(elapsed, 3),
            "asked": len(dimensions),
            "answered": len(codes),
            "answers": {dimensions[int(k[1:])]["id"]: v
                        for k, v in codes.items() if k[1:].isdigit() and int(k[1:]) < len(dimensions)},
            "raw_response": response.get("response"),
            "num_ctx": num_ctx,
            "truncation_detected": truncated,
            **summarize(response),
        }
        print(f"  {case_name:<26} {elapsed:7.2f}s  answered={len(codes)}/{len(dimensions)}  "
              f"prompt_tokens={prompt_tokens}  truncated={truncated}")

    out = {
        "measured_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "host": args.host,
        "model": args.model,
        "shape": {
            "endpoint": "/api/generate",
            "questions": "system",
            "message": "prompt",
            "dimensions_per_request": len(dimensions),
            "num_ctx": num_ctx,
            "temperature": 0,
            "think": False,
        },
        "cases": cases,
        "all_cases_answered_every_dimension": all(
            c["answered"] == c["asked"] for c in cases.values()),
    }
    Path(args.out).write_text(json.dumps(out, indent=2, default=str), encoding="utf-8")
    print()
    print(f"every case answered every dimension: {out['all_cases_answered_every_dimension']}")
    print(f"wrote {args.out}")
    return 0


def answer_shape_probe(args, dimensions: list[dict], bodies: dict[str, str]) -> int:
    """Can the model express anything richer than a one-letter code?

    This decides the port mapping and it is not cosmetic. StyloMail's dimensions are **Noul**
    questions: the answer is a probability in 0..1 where 0.5 means genuinely balanced. The model's
    own system prompt asks for one-letter codes, which would force every answer to 1.0 or 0.0 and
    make a balanced judgement indistinguishable from a confident one. So: try a graded schema
    before accepting a binary one.
    """
    flagship = bodies.get(FLAGSHIP_CASE) or next(iter(bodies.values()), "")
    dimension = next((d for d in dimensions if d["id"] == "semantic.credential_request"), dimensions[0])

    question = (
        f"Question: {dimension['instructions']}\n"
        f"  A = {dimension['criteria_true']}\n"
        f"  B = {dimension['criteria_false']}\n\n"
        f"--- MESSAGE ---\n{flagship}\n--- END MESSAGE ---\n\n"
    )

    shapes = {
        # The baseline the model's own prompt describes.
        "letter_only": (
            question + "Return only the one-letter code.",
            {"type": "object", "properties": {"answer": {"type": "string", "enum": ["A", "B"]}},
             "required": ["answer"]},
        ),
        # A graded answer, which is what a Noul actually is.
        "graded_only": (
            question + "Answer with a number from 0 to 1: 1 means the statement is certainly true, "
                       "0 means certainly false, 0.5 means it is genuinely balanced.",
            {"type": "object", "properties": {"value": {"type": "number"}},
             "required": ["value"]},
        ),
        # Both, so the letter can be checked against the number for coherence.
        "letter_and_graded": (
            question + "Give the one-letter code and, separately, a number from 0 to 1 for how "
                       "certain you are that A is correct rather than B.",
            {"type": "object", "properties": {"answer": {"type": "string", "enum": ["A", "B"]},
                                              "value": {"type": "number"}},
             "required": ["answer", "value"]},
        ),
    }

    results = {}
    for name, (prompt, schema) in shapes.items():
        runs = []
        for _ in range(2):
            response, elapsed = generate(args.host, args.model, prompt, schema=schema)
            runs.append({
                "elapsed_seconds": round(elapsed, 3),
                "raw_response": response.get("response"),
                "eval_count": response.get("eval_count"),
                "error": response.get("__error__") or response.get("error"),
            })
        results[name] = runs
        print(f"  {name:<20} {runs[0]['raw_response']!r}")

    out = {
        "measured_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "host": args.host,
        "model": args.model,
        "dimension": dimension["id"],
        "case": FLAGSHIP_CASE,
        "known_letter_answer_for_this_case": "B when asked alone (3/3 in the full survey)",
        "shapes": results,
    }
    Path(args.out).write_text(json.dumps(out, indent=2, default=str), encoding="utf-8")
    print(f"\nwrote {args.out}")
    return 0


def stall_probe(args, dimensions: list[dict], bodies: dict[str, str]) -> int:
    """Repeat one dimension to test whether a latency stall recurs.

    The full run produced one call of 103.1 s against a 2.3 s median, with 95.8 s of it outside
    ollama's own load/prompt-eval/eval counters. One sample cannot say whether that belongs to the
    model, to this machine, or to one unlucky moment. This alternates the dimension that stalled
    with one that did not: if only the former stalls, it is the dimension, and if both do, it is
    the machine.
    """
    flagship = bodies.get(FLAGSHIP_CASE) or next(iter(bodies.values()), "")
    by_id = {d["id"]: d for d in dimensions}

    # The dimension that stalled in the full run, alternating with one that stayed fast.
    order = ["semantic.link_lure", "semantic.credential_request"]

    runs = []
    for index in range(args.stall_probe):
        dimension = by_id.get(order[index % len(order)])
        if dimension is None:
            continue

        prompt = (
            f"Question: {dimension['instructions']}\n"
            f"  A = {dimension['criteria_true']}\n"
            f"  B = {dimension['criteria_false']}\n\n"
            f"--- MESSAGE ---\n{flagship}\n--- END MESSAGE ---\n\nReturn only the one-letter code."
        )
        response, elapsed = generate(args.host, args.model, prompt, schema=single_question_schema())
        entry = {
            "index": index,
            "id": dimension["id"],
            "elapsed_seconds": round(elapsed, 3),
            "answer": parse_codes(response.get("response")).get("answer"),
            **summarize(response),
        }
        entry["residual_seconds"] = residual_seconds(entry)
        runs.append(entry)
        print(f"  {index} {dimension['id']:<32} {elapsed:8.3f}s "
              f"residual={entry['residual_seconds']:7.3f}s answer={entry['answer']}")

    durations = [entry["elapsed_seconds"] for entry in runs]
    result = {
        "measured_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "host": args.host,
        "model": args.model,
        "repeats": len(runs),
        "runs": runs,
        "median_seconds": round(statistics.median(durations), 3) if durations else None,
        "max_seconds": max(durations) if durations else None,
        "note": (
            "Compare against the full run: fan-out median 2.307 s/dimension, one call at 103.259 s "
            "with a 95.79 s residual. A max here near the median means the stall did not recur in "
            "this window; it does not prove it cannot."
        ),
    }

    Path(args.out).write_text(json.dumps(result, indent=2, default=str), encoding="utf-8")
    print(f"\nmedian {result['median_seconds']}s  max {result['max_seconds']}s  wrote {args.out}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="nimble")
    parser.add_argument("--host", default="127.0.0.1:11435",
                        help="Ollama server to measure. 11435 is the 0.35.0 server.")
    parser.add_argument("--other-host", default="127.0.0.1:11434",
                        help="The other installed server, probed to show the version split.")
    parser.add_argument("--out", required=True)
    parser.add_argument("--repo", default=".")
    parser.add_argument("--stall-probe", type=int, default=0,
                        help="Run ONLY this many repeats of one dimension and exit, to test whether "
                             "a latency stall recurs. Used after a full run showed a 103 s outlier.")
    parser.add_argument("--answer-shape", action="store_true",
                        help="Run ONLY the answer-shape probe and exit: can the model give a graded "
                             "0..1 answer, or only a one-letter code?")
    parser.add_argument("--shipping-shape", action="store_true",
                        help="Run ONLY the intended shipping request shape over every corpus case "
                             "and exit.")
    parser.add_argument("--shipping-num-ctx", type=int, default=8192,
                        help="num_ctx for the shipping-shape probe.")
    parser.add_argument("--residency", type=int, default=0,
                        help="Repeat one shipping-shape call this many times and report whether the "
                             "model stayed resident between calls, then exit.")
    args = parser.parse_args()

    repo_root = Path(args.repo).resolve()
    dimensions = load_dimensions(repo_root)
    bodies = load_bodies(repo_root)

    if not dimensions:
        print(f"No dimensions parsed from {DIMENSION_SOURCE}", file=sys.stderr)
        return 1

    if args.answer_shape:
        return answer_shape_probe(args, dimensions, bodies)

    if args.shipping_shape:
        return shipping_shape_probe(args, dimensions, bodies)

    if args.residency:
        return residency_probe(args, dimensions, bodies)

    if args.stall_probe:
        return stall_probe(args, dimensions, bodies)

    print(f"dimensions: {len(dimensions)}   corpus messages: {len(bodies)}")
    print(f"measuring host {args.host}, model {args.model}")
    print()

    result: dict = {
        "measured_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "host": args.host,
        "model": args.model,
        "dimensions": dimensions,
        "corpus_cases": list(bodies),
    }

    # 1. Which server, which version. The split is the reason a default-endpoint measurement lies.
    result["servers"] = {
        args.host: get(args.host, "/api/version"),
        args.other_host: get(args.other_host, "/api/version"),
    }
    print("servers:")
    for host, version in result["servers"].items():
        print(f"  {host:<20} {version}")
    print()

    # 2. The model card as this server reports it.
    show = post(args.host, "/api/show", {"model": args.model})
    result["show"] = {
        "details": show.get("details"),
        "capabilities": show.get("capabilities"),
        "parameters": show.get("parameters"),
        "template": show.get("template"),
        "system": show.get("system"),
        "modelfile_head": (show.get("modelfile") or "")[:2000],
        "error": show.get("__error__") or show.get("error"),
    }
    other_show = post(args.other_host, "/api/show", {"model": args.model})
    result["show_on_other_host"] = {
        "error": other_show.get("__error__") or other_show.get("error"),
        "capabilities": other_show.get("capabilities"),
        "details": other_show.get("details"),
    }

    # 2b. Can the older server on the DEFAULT port actually run the model, or only describe it?
    # This is the consequence of the version split, and the failure mode matters: an explicit
    # error is safe, a silent wrong answer is not.
    other_run = post(args.other_host, "/api/generate", {
        "model": args.model,
        "prompt": "ping",
        "stream": False,
        "options": {"num_predict": 1},
    }, timeout=120)
    result["run_on_other_host"] = {
        "error": other_run.get("__error__") or other_run.get("error"),
        "done": other_run.get("done"),
    }
    print(f"capabilities: {result['show']['capabilities']}")
    print(f"details: {result['show']['details']}")
    print(f"model on the other server: {result['show_on_other_host']}")
    print()

    flagship_body = bodies.get(FLAGSHIP_CASE) or next(iter(bodies.values()), "")
    questions_all = render_questions(dimensions)

    # 3a. One question, one request: can it hold our shape at all?
    first = dimensions[0]
    single_prompt = (
        "Answer one question about the message below using the supplied schema.\n\n"
        f"Question: {first['instructions']}\n"
        f"  A = {first['criteria_true']}\n"
        f"  B = {first['criteria_false']}\n\n"
        f"--- MESSAGE ---\n{flagship_body}\n--- END MESSAGE ---\n\nReturn only the one-letter code."
    )
    response, elapsed = generate(args.host, args.model, single_prompt, schema=single_question_schema())
    result["one_question_one_request"] = {
        "elapsed_seconds": round(elapsed, 3),
        "raw_response": response.get("response"),
        "parsed": parse_codes(response.get("response")),
        **summarize(response),
    }
    print(f"[one question]  {elapsed*1000:7.0f} ms  answer={result['one_question_one_request']['parsed']}")

    # 3b. All 12 dimensions in one request: the shape the hosted provider uses.
    all_prompt = (
        "Answer every question about the message below using the supplied schema.\n"
        "Each answer is one letter: A or B.\n\n"
        f"{questions_all}\n\n"
        f"--- MESSAGE ---\n{flagship_body}\n--- END MESSAGE ---"
    )
    response, elapsed = generate(args.host, args.model, all_prompt, schema=answer_schema(len(dimensions)))
    all_parsed = parse_codes(response.get("response"))
    result["all_dimensions_one_request"] = {
        "elapsed_seconds": round(elapsed, 3),
        "asked": len(dimensions),
        "answered": len(all_parsed),
        "raw_response": response.get("response"),
        "parsed": all_parsed,
        **summarize(response),
    }
    print(f"[all in one]    {elapsed*1000:7.0f} ms  answered={len(all_parsed)}/{len(dimensions)}"
          f"  tokens_in={response.get('prompt_eval_count')}")

    # 3c. One request per dimension: the fan-out the decision capability implies.
    fanout = []
    for index, dimension in enumerate(dimensions):
        prompt = (
            f"Question: {dimension['instructions']}\n"
            f"  A = {dimension['criteria_true']}\n"
            f"  B = {dimension['criteria_false']}\n\n"
            f"--- MESSAGE ---\n{flagship_body}\n--- END MESSAGE ---\n\nReturn only the one-letter code."
        )
        response, elapsed = generate(args.host, args.model, prompt, schema=single_question_schema())
        entry = {
            "index": index,
            "id": dimension["id"],
            "elapsed_seconds": round(elapsed, 3),
            "raw_response": response.get("response"),
            "answer": parse_codes(response.get("response")).get("answer"),
            **summarize(response),
        }
        entry["residual_seconds"] = residual_seconds(entry)
        fanout.append(entry)
        print(f"  {index:2d} {dimension['id']:<45} {elapsed*1000:7.0f} ms  "
              f"{entry['answer']}  residual={entry['residual_seconds']:.2f}s")

    durations = [entry["elapsed_seconds"] for entry in fanout]
    result["fanout"] = {
        "per_dimension": fanout,
        "median_seconds": round(statistics.median(durations), 3) if durations else None,
        "min_seconds": round(min(durations), 3) if durations else None,
        "max_seconds": round(max(durations), 3) if durations else None,
        "total_seconds": round(sum(durations), 3) if durations else None,
        "answered": sum(1 for entry in fanout if entry["answer"]),
    }
    print()
    print(f"[fan-out]       median {result['fanout']['median_seconds']*1000:.0f} ms/dimension, "
          f"total {result['fanout']['total_seconds']:.1f} s, "
          f"answered {result['fanout']['answered']}/{len(dimensions)}")

    # 3d. Does the answer depend on how the questions are batched?
    #     The two shapes above disagreed on some dimensions. Repeat BOTH shapes on ONE dimension
    #     to separate a real batching effect from ordinary sampling noise: temperature 0 is not a
    #     guarantee of determinism, and a single disagreement is not a finding.
    target_index = next(
        (i for i, d in enumerate(dimensions) if d["id"] == "semantic.credential_request"), 0)
    target = dimensions[target_index]

    # Both templates are byte-identical to the prompts used above, so the shapes are comparable.
    single_template = (
        "Question: {instructions}\n"
        "  A = {true_criteria}\n"
        "  B = {false_criteria}\n\n"
        "--- MESSAGE ---\n{body}\n--- END MESSAGE ---\n\nReturn only the one-letter code."
    )
    batched_template = (
        "Answer every question about the message below using the supplied schema.\n"
        "Each answer is one letter: A or B.\n\n"
        "{questions}\n\n"
        "--- MESSAGE ---\n{body}\n--- END MESSAGE ---"
    )

    stability: dict = {"dimension": target["id"], "index": target_index, "alone": [], "batched": []}
    for attempt in range(3):
        response, elapsed = generate(args.host, args.model, single_template.format(
            instructions=target["instructions"],
            true_criteria=target["criteria_true"],
            false_criteria=target["criteria_false"],
            body=flagship_body), schema=single_question_schema())
        stability["alone"].append({
            "attempt": attempt,
            "answer": parse_codes(response.get("response")).get("answer"),
            "elapsed_seconds": round(elapsed, 3),
            "prompt_eval_count": response.get("prompt_eval_count"),
        })

        response, elapsed = generate(args.host, args.model, batched_template.format(
            questions=questions_all, body=flagship_body), schema=answer_schema(len(dimensions)))
        codes = parse_codes(response.get("response"))
        stability["batched"].append({
            "attempt": attempt,
            "answer": codes.get(f"q{target_index}"),
            "elapsed_seconds": round(elapsed, 3),
            "prompt_eval_count": response.get("prompt_eval_count"),
        })

    alone = [entry["answer"] for entry in stability["alone"]]
    batched = [entry["answer"] for entry in stability["batched"]]
    stability["alone_stable"] = len(set(alone)) == 1
    stability["batched_stable"] = len(set(batched)) == 1
    stability["shapes_agree"] = set(alone) == set(batched)
    result["batching_stability"] = stability
    print(f"[stability]     {target['id']}: alone={alone} batched={batched} "
          f"agree={stability['shapes_agree']}")

    # 3e. Do the two servers answer the same? The 0.31.1 server on the DEFAULT port serves the
    #     model (a bare ping returns done), so "the old server cannot run it" is NOT established
    #     by a ping. The question that decides anything is whether it answers a real decision
    #     identically, so that a client pointed at the default port either fails loudly or works.
    compare_prompt = single_template.format(
        instructions=target["instructions"],
        true_criteria=target["criteria_true"],
        false_criteria=target["criteria_false"],
        body=flagship_body)

    comparison: dict = {}
    for label, host in (("0.35.0", args.host), ("0.31.1", args.other_host)):
        answers = []
        error = None
        for _ in range(3):
            response, _elapsed = generate(host, args.model, compare_prompt,
                                          schema=single_question_schema())
            error = error or response.get("__error__") or response.get("error")
            answers.append(parse_codes(response.get("response")).get("answer"))
        comparison[label] = {"host": host, "answers": answers, "error": error}

    result["server_behaviour_comparison"] = comparison
    print(f"[servers]       {target['id']} alone: 0.35.0={comparison['0.35.0']['answers']} "
          f"0.31.1={comparison['0.31.1']['answers']}")

    # 4. Truncation.
    print()
    print("truncation probe (this is the slow one):")
    result["truncation"] = truncation_probe(args.host, args.model)
    print(f"  default window dropped "
          f"{result['truncation']['tokens_the_default_window_dropped']} tokens; "
          f"mirrored prompt: default={result['truncation']['mirrored_prompt_default_num_ctx']['answer']} "
          f"wide={result['truncation']['mirrored_prompt_wide_num_ctx']['answer']} "
          f"=> kept_beginning={result['truncation']['kept_the_beginning_of_the_prompt']} "
          f"kept_end={result['truncation']['kept_the_end_of_the_prompt']}")

    Path(args.out).write_text(json.dumps(result, indent=2, default=str), encoding="utf-8")
    print()
    print(f"wrote {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
