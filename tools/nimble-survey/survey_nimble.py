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

# The dimension the batching-stability cell points at unless --stability-target overrides it. It was
# hard-coded to this id, which is why the cell read as a fact about credential_request rather than
# about the asking shape; the cell is the fleet's controlled measurement of the SHAPE effect, and a
# second target is what puts another dimension on the same footing.
STABILITY_TARGET = "semantic.credential_request"

# The two asking shapes, byte-identical between the full run's own cells and the standalone cell
# below, so "the shapes differ" is a fact about the question count and nothing else.
SINGLE_QUESTION_TEMPLATE = (
    "Question: {instructions}\n"
    "  A = {true_criteria}\n"
    "  B = {false_criteria}\n\n"
    "--- MESSAGE ---\n{body}\n--- END MESSAGE ---\n\nReturn only the one-letter code."
)
BATCHED_TEMPLATE = (
    "Answer every question about the message below using the supplied schema.\n"
    "Each answer is one letter: A or B.\n\n"
    "{questions}\n\n"
    "--- MESSAGE ---\n{body}\n--- END MESSAGE ---"
)


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
    extra_options: dict | None = None,
) -> tuple[dict, float]:
    """One non-streaming generation. Temperature 0 so a repeat measures the model, not the sampler.

    `extra_options` exists so a probe can vary one server option against a fixed prompt without a
    second copy of the request shape. It is merged last, so a probe that sets `num_ctx` both ways on
    purpose is the probe's business.
    """
    options: dict = {"temperature": 0}
    if num_ctx is not None:
        options["num_ctx"] = num_ctx
    if extra_options:
        options.update(extra_options)

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


def measure_effective_window(
    host: str,
    model: str,
    num_ctx: int,
    extra_options: dict | None = None,
) -> dict:
    """The window the server actually applies, measured by saturating it.

    <b>Why this is not `num_ctx`.</b> Comparing a prompt's token count against the *requested*
    window cannot detect truncation when the server applies a smaller one than it was asked for:
    the count comes back below the request and the check reads clean. That is decision 26, and it
    is not hypothetical. A prompt far over the window was measured evaluating at 4,099 tokens with
    nothing asked, while the model's loaded context is 8,194 and its card claims 262,144.

    So the window is measured rather than assumed: a prompt deliberately larger than any plausible
    window is evaluated as exactly the window it was cut to, and the plateau of `prompt_eval_count`
    is that window. The requested value is recorded beside it so a disagreement is visible.

    <b>What the number is, exactly.</b> Not the window: the largest evaluated count the probe could
    reach below it. The filler is a short repetition, so the plateau is quantised by that
    repetition, and it lands a couple of tokens above the true window rather than on it. Treat it as
    a ceiling-with-tolerance, which is what a truncation check needs.
    """
    filler = "This paragraph is padding and carries no request of any kind. " * 1500
    response, _elapsed = generate(
        host, model, "Classify this message.\n--- MESSAGE ---\n" + filler + "\n--- END MESSAGE ---",
        schema=single_question_schema(), num_ctx=num_ctx, extra_options=extra_options)

    evaluated = response.get("prompt_eval_count")
    show = post(host, "/api/show", {"model": model})

    # The architecture-prefixed key, not a fixed one: this server reports `qwen35.context_length`
    # and has no `general.context_length` at all, so the earlier fixed lookup returned None and
    # would have kept returning None quietly.
    info = show.get("model_info") or {}
    family = (show.get("details") or {}).get("family")
    loaded = info.get(f"{family}.context_length") if family else None
    if loaded is None:
        loaded = next(
            (value for key, value in info.items() if key.endswith(".context_length")), None)

    return {
        "requested_num_ctx": num_ctx,
        "extra_options": extra_options or {},
        "evaluated_tokens": evaluated,
        "effective_window_measured": evaluated,
        "loaded_context_length": loaded,
        "model_parameters": show.get("parameters"),
        "probe_characters": len(filler),
        "note": (
            "effective_window_measured is the plateau of prompt_eval_count for a prompt larger than "
            "the window, which is the window the server applied. Truncation is detected by comparing "
            "a real prompt against THIS, never against requested_num_ctx."
        ),
    }


def context_slots_probe(args) -> int:
    """Why the applied window is half the requested one, tested rather than guessed.

    Measured: requested 4096/8192/16384 applied 2050/4098/8194, which is a stable half, not a
    one-off. The obvious explanation is that the server divides the requested context among parallel
    slots, so the window one request actually gets is `num_ctx / num_parallel`, and the run above
    implies two slots. That is a hypothesis and this is the test: hold the prompt fixed and ask for
    one slot. If the applied window becomes the requested one, the hypothesis is the finding and the
    repair is to say how many slots the deployment wants. If it does not, the cause is elsewhere and
    the honest result is "measured, unexplained, and still half".

    Nothing here decides what the adapter should do. It establishes the mechanism, and the adapter's
    own margin arithmetic is a separate question with a separate owner.
    """
    measured = {"host": args.host, "model": args.model, "runs": {}}

    for label, num_ctx, options in (
        ("8192 default slots", 8192, None),
        ("8192 one slot", 8192, {"num_parallel": 1}),
        ("16384 one slot", 16384, {"num_parallel": 1}),
        ("4096 two slots", 4096, {"num_parallel": 2}),
    ):
        window = measure_effective_window(args.host, args.model, num_ctx, extra_options=options)
        measured["runs"][label] = window
        ratio = (
            window["effective_window_measured"] / num_ctx
            if window["effective_window_measured"] and num_ctx else None
        )
        print(f"  {label:<18} requested {num_ctx:>6}  applied "
              f"{window['effective_window_measured']}  ratio {ratio}")

    # The control that decides whether the number above is a window or an instrument.
    #
    # Everything so far saturates the prompt, so a server that simply reported half of what it
    # evaluated would produce exactly the same table. This sends a prompt comfortably UNDER both
    # windows and asks the same question at two different requested sizes. An honest count is the
    # token count of the prompt and does not move when the window does; a halved count would move
    # with it, and the whole measurement would be a property of the probe rather than the server.
    short = "This paragraph is padding and carries no request of any kind. " * 60
    control = {}
    for num_ctx in (2048, 8192):
        response, _elapsed = generate(
            args.host, args.model,
            "Classify this message.\n--- MESSAGE ---\n" + short + "\n--- END MESSAGE ---",
            schema=single_question_schema(), num_ctx=num_ctx)
        control[str(num_ctx)] = {
            "requested_num_ctx": num_ctx,
            "evaluated_tokens": response.get("prompt_eval_count"),
            "characters_sent": len(short),
        }
        print(f"  under-window control  requested {num_ctx:>6}  evaluated "
              f"{response.get('prompt_eval_count')}  characters {len(short)}")

    measured["under_window_control"] = control
    counts = {entry["evaluated_tokens"] for entry in control.values()}
    measured["control_reading"] = (
        "counting is absolute: the same prompt evaluated to the same token count under two "
        "different windows, so the saturated plateau is the window and not half of the prompt"
        if len(counts) == 1 else
        f"counting MOVES with the window ({sorted(counts)}), so the instrument is halving and the "
        "plateau above is not a window measurement"
    )
    print(f"  {measured['control_reading']}")

    Path(args.out).write_text(json.dumps(measured, indent=2, default=str), encoding="utf-8")
    print(f"\nwrote {args.out}")
    return 0


def window_quantum_probe(args) -> int:
    """Is the plateau the applied window, or a quantum of the filler that measured it?

    `measure_effective_window` saturates the prompt with a **repeated** paragraph and calls the
    plateau of `prompt_eval_count` the window. Its own docstring already warns that a repeated filler
    quantises the plateau, so the number it returns could be the window or could be the window plus
    however much of the last repetition fitted. The ladder reads 2050 / 4098 / 8194 for 4096 / 8192 /
    16384, which is half plus two at all three points: an exact arithmetic relation, and exactness is
    what a quantum artifact would be least likely to produce. This settles it by measuring the same
    window with fillers of four different quanta.

    Same window, same question, different filler: if the plateau is the window it does not move, and
    half-plus-two is a property of the server. If it moves with the filler, the plateau is an
    instrument artifact, the true window is at or below `num_ctx / 2`, and the adapter's derived
    `NumCtx / 2` is the safe bound rather than a number two tokens short.
    """
    fillers = {
        # Quanta from ~1 token to ~15 tokens, one filler that does not repeat at all, and two small
        # fillers that should stay well under any window. `word, 2k` is the control that says whether
        # a small filler is being cut or simply counted: it is the same text as `word, 4k`, half as
        # long, so if the long one is cut and the short one is not, the cut is visible between them.
        "single character, 20k": "x" * 20000,
        "word, 2k": "padding " * 2000,
        "word, 4k": "padding " * 4000,
        "sentence, 4k": "This sentence is padding and asks nothing. " * 4000,
        "paragraph, 1500 (the ladder's filler)":
            "This paragraph is padding and carries no request of any kind. " * 1500,
        "non-repeating words": " ".join(f"pad{index}word" for index in range(4000)),
    }

    measured = {"host": args.host, "model": args.model, "requested_num_ctx": args.num_ctx, "plateaus": {}}
    for label, filler in fillers.items():
        response, _elapsed = generate(
            args.host, args.model,
            "Classify this message.\n--- MESSAGE ---\n" + filler + "\n--- END MESSAGE ---",
            schema=single_question_schema(), num_ctx=args.num_ctx)
        measured["plateaus"][label] = {
            "characters_sent": len(filler),
            "evaluated_tokens": response.get("prompt_eval_count"),
        }
        print(f"  {label:<40} characters {len(filler):>6}  evaluated "
              f"{response.get('prompt_eval_count')}")

    # A filler that never reached the cut was never truncated, so its count is the size of its own
    # prompt and says nothing about the window: the first version of this probe reported that as the
    # plateau "moving", which is the same mistake as reading an unsaturated prompt as a truncation.
    # Only fillers that reach within 10 percent of the highest count are evidence about the cut.
    counts = {
        label: entry["evaluated_tokens"]
        for label, entry in measured["plateaus"].items()
        if entry["evaluated_tokens"]
    }
    ceiling = max(counts.values())
    reached = {label: count for label, count in counts.items() if count >= 0.9 * ceiling}
    short = {label: count for label, count in counts.items() if count < 0.9 * ceiling}
    spread = max(reached.values()) - min(reached.values())

    measured["reached_the_cut"] = reached
    measured["never_reached_the_cut"] = short
    measured["plateau_invariant"] = spread == 0
    measured["cut_spread_tokens"] = spread
    measured["reading"] = (
        f"every filler that reached the cut returned exactly {next(iter(reached.values()))}, so the "
        "cut is invariant across filler content and this is the window the server applied"
        if measured["plateau_invariant"] else
        f"every filler that reached the cut returned between {min(reached.values())} and "
        f"{max(reached.values())} tokens, a spread of {spread}, and the fillers agreeing at "
        f"{min(reached.values())} include one that does not repeat at all. So the cut is a band a few "
        "tokens wide rather than an exact boundary, the applied window is AT OR BELOW the band's "
        f"floor ({min(reached.values())}), and a derivation that rounds below that floor is the safe "
        "one to reason about. Fillers that never reached the cut are excluded: "
        f"{short}"
    )
    print(f"  {measured['reading']}")

    Path(args.out).write_text(json.dumps(measured, indent=2, default=str), encoding="utf-8")
    print(f"\nwrote {args.out}")
    return 0


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

    This turned out to be the dominant latency term: in the delimited-body run, 8 to 40 seconds
    of every 18 to 60 second call was `load_duration`, and the residual was ~0.02 s. A per-message
    path that reloads a 9 GB model per message is not a latency question, it is a viability one.
    This repeats one call and reports, each time, what ollama says it spent loading and what the
    server reports as resident.
    """
    flagship = bodies.get(FLAGSHIP_CASE) or next(iter(bodies.values()), "")
    num_ctx = args.body_num_ctx
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


def delimited_body_shape_probe(args, dimensions: list[dict], bodies: dict[str, str]) -> int:
    """Measure a delimited-body request shape over every corpus case. Exploratory, not shipping.

    **This is not the shape the adapter sends, and the docstring used to say that it was.** The
    adapter sends the serialized message *state* as one member of its request
    (`NimbleSemanticMailClassifier.FitState` in the `/2` shape; `/1` built the same state into a
    user turn in `NimbleSemanticMailClassifier.FitPrompt`),
    while this sends `--- MESSAGE ---\\n{body}`. The two are not interchangeable: replaying one
    captured adapter request and substituting only the user turn, the delimited body differs from
    the adapter's own request in **3 of 11 dimensions** on the flagship case
    (`.styloagent/scratch/nimble/replay-divergence.json`), which is a claim about this model and
    nothing about the deployment.

    What the shape itself does, and what the adapter also does, so it is worth measuring on its own
    terms: questions in `system` and the message in `prompt`, so untrusted content never shares a
    string with the instructions; all askable dimensions in ONE request; one-letter codes
    constrained by a `format` JSON schema; an explicit num_ctx, with `prompt_eval_count` checked
    against the **measured** window afterwards, because the requested window is not the applied one.

    Acceptance is not "it returned something": it is one code per question, an explicit token count
    under the applied window, and a recorded answer for every dimension asked.
    """
    num_ctx = args.body_num_ctx
    window = measure_effective_window(args.host, args.model, num_ctx)
    effective_window = window["effective_window_measured"]
    print(f"  effective window measured: {effective_window} "
          f"(requested {num_ctx}, loaded context {window['loaded_context_length']})")
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

        # Against the MEASURED window, never the requested one. `prompt_tokens >= num_ctx` cannot see
        # truncation when the server applies a smaller window than it was asked for: the count comes
        # back under the request and the check reads clean. That was decision 26, and it is why the
        # measured window is a parameter of this comparison rather than an assumption.
        truncated = (
            prompt_tokens is not None
            and effective_window is not None
            and prompt_tokens >= effective_window
        )

        cases[case_name] = {
            "elapsed_seconds": round(elapsed, 3),
            "asked": len(dimensions),
            "answered": len(codes),
            "answers": {dimensions[int(k[1:])]["id"]: v
                        for k, v in codes.items() if k[1:].isdigit() and int(k[1:]) < len(dimensions)},
            "raw_response": response.get("response"),
            "num_ctx": num_ctx,
            "effective_window_measured": effective_window,
            "truncation_detected": truncated,
            **summarize(response),
        }
        print(f"  {case_name:<26} {elapsed:7.2f}s  answered={len(codes)}/{len(dimensions)}  "
              f"prompt_tokens={prompt_tokens}  truncated={truncated}")

    out = {
        "measured_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "host": args.host,
        "model": args.model,
        "effective_window": window,
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


def batching_stability_cell(
    host: str,
    model: str,
    target: dict,
    target_index: int,
    dimensions: list[dict],
    questions_all: str,
    body: str,
) -> dict:
    """One dimension, one body, one code path, asked ALONE and BATCHED, three calls each.

    This is the fleet's CONTROLLED measurement of the asking-shape effect, and it is the reason the
    effect does not rest on a cross-lane inference: the two shapes are byte-identical prompts apart
    from the number of questions, over the same body and through the same adapter. Read it as a
    SHAPE comparison and never as a stability claim about the dimension: the single-question shape
    is itself the variable, so "alone was stable at B" is a fact about the shape, not evidence that
    B is what this dimension means for this body.
    """
    stability: dict = {"dimension": target["id"], "index": target_index, "alone": [], "batched": []}
    for attempt in range(3):
        response, elapsed = generate(host, model, SINGLE_QUESTION_TEMPLATE.format(
            instructions=target["instructions"],
            true_criteria=target["criteria_true"],
            false_criteria=target["criteria_false"],
            body=body), schema=single_question_schema())
        stability["alone"].append({
            "attempt": attempt,
            "answer": parse_codes(response.get("response")).get("answer"),
            "elapsed_seconds": round(elapsed, 3),
            "prompt_eval_count": response.get("prompt_eval_count"),
        })

        response, elapsed = generate(host, model, BATCHED_TEMPLATE.format(
            questions=questions_all, body=body), schema=answer_schema(len(dimensions)))
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
    return stability


def stability_probe(args, dimensions: list[dict], bodies: dict[str, str]) -> int:
    """Run ONLY the batching-stability cell, for the dimension named by --stability-target.

    Added so a dimension can be put on the same controlled footing as `credential_request`, whose
    cell is the one that established the shape effect in this lane. It runs the SAME cell through
    the SAME helper, so the two readings cannot drift apart by being two implementations.
    """
    by_id = {d["id"]: d for d in dimensions}
    target = by_id.get(args.stability_target)
    if target is None:
        print(f"no dimension with id {args.stability_target!r}; known ids: "
              + ", ".join(sorted(by_id)), file=sys.stderr)
        return 1

    target_index = dimensions.index(target)
    body = bodies.get(FLAGSHIP_CASE) or next(iter(bodies.values()), "")
    print(f"stability cell: {target['id']} (index {target_index} of {len(dimensions)}), "
          f"body {FLAGSHIP_CASE}, 3 calls alone and 3 batched, host {args.host}")

    stability = batching_stability_cell(
        args.host, args.model, target, target_index, dimensions,
        render_questions(dimensions), body)

    result = {
        "measured_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "host": args.host,
        "model": args.model,
        "corpus_case": FLAGSHIP_CASE,
        "batching_stability": stability,
        "note": (
            "A SHAPE comparison, not a stability claim about the dimension: the single-question "
            "shape is itself the variable. Same body, same code path, same adapter; the two shapes "
            "differ only in the question count."
        ),
    }

    Path(args.out).write_text(json.dumps(result, indent=2, default=str), encoding="utf-8")
    print(f"[stability]     {target['id']}: alone={[e['answer'] for e in stability['alone']]} "
          f"batched={[e['answer'] for e in stability['batched']]} "
          f"agree={stability['shapes_agree']}  wrote {args.out}")
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
    parser.add_argument("--body-shape", action="store_true",
                        help="Run ONLY the delimited-body shape over every corpus case and exit. This "
                             "is NOT the shipping shape: the adapter sends the serialized message "
                             "state (NimbleSemanticMailClassifier.FitState), while "
                             "this sends a delimited bare body, and on the flagship case the two "
                             "differ in 3 of 11 dimensions. Numbers from it describe the model, never "
                             "the deployment. Renamed from --shipping-shape, which claimed otherwise.")
    parser.add_argument("--shipping-shape", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--body-num-ctx", type=int, default=8192,
                        help="num_ctx for the delimited-body probe.")
    parser.add_argument("--residency", type=int, default=0,
                        help="Repeat one delimited-body call this many times and report whether the "
                             "model stayed resident between calls, then exit.")
    parser.add_argument("--window-quantum", action="store_true",
                        help="Run ONLY the window-quantum probe and exit: is the measured plateau "
                             "the applied window, or a quantum of the repeated filler that measured "
                             "it? Five fillers, five calls, one window.")
    parser.add_argument("--num-ctx", type=int, default=8192,
                        help="Requested num_ctx for the window-quantum probe.")
    parser.add_argument("--stability-only", action="store_true",
                        help="Run ONLY the batching-stability cell and exit: one dimension, one "
                             "body, asked alone and batched, three calls each. This is the "
                             "controlled asking-shape measurement, and --stability-target says "
                             "which dimension it points at.")
    parser.add_argument("--stability-target", default=STABILITY_TARGET,
                        help="The dimension id the batching-stability cell points at. Defaults to "
                             "semantic.credential_request, which is what it has always measured; "
                             "the cell is about the SHAPE, so a second target puts another "
                             "dimension on the same footing rather than changing the cell.")
    parser.add_argument("--context-slots", action="store_true",
                        help="Run ONLY the context-slots probe and exit: does the server divide the "
                             "requested num_ctx among parallel slots, which would explain the applied "
                             "window measuring half the requested one?")
    args = parser.parse_args()

    repo_root = Path(args.repo).resolve()
    dimensions = load_dimensions(repo_root)
    bodies = load_bodies(repo_root)

    if not dimensions:
        print(f"No dimensions parsed from {DIMENSION_SOURCE}", file=sys.stderr)
        return 1

    # Refused rather than redirected, so a script that still passes the old flag fails loudly
    # instead of quietly measuring a shape under a name that used to claim it was the shipping one.
    if args.shipping_shape:
        print(
            "--shipping-shape was never the shipping shape and has been renamed: the adapter sends "
            "the serialized message state, this tool sends a delimited body. Use --body-shape, and "
            "read its numbers as a fact about the model rather than about the deployment.",
            file=sys.stderr,
        )
        return 2

    if args.answer_shape:
        return answer_shape_probe(args, dimensions, bodies)

    if args.stability_only:
        return stability_probe(args, dimensions, bodies)

    if args.body_shape:
        return delimited_body_shape_probe(args, dimensions, bodies)

    if args.residency:
        return residency_probe(args, dimensions, bodies)

    if args.context_slots:
        return context_slots_probe(args)

    if args.window_quantum:
        return window_quantum_probe(args)

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
        (i for i, d in enumerate(dimensions) if d["id"] == args.stability_target), 0)
    target = dimensions[target_index]

    stability = batching_stability_cell(
        args.host, args.model, target, target_index, dimensions, questions_all, flagship_body)
    result["batching_stability"] = stability
    print(f"[stability]     {target['id']}: alone={[e['answer'] for e in stability['alone']]} "
          f"batched={[e['answer'] for e in stability['batched']]} agree={stability['shapes_agree']}")

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
