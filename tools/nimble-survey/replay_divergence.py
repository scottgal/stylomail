#!/usr/bin/env python3
"""Name the per-dimension divergence between replay variants, from a replay artifact.

The replay probe records, for one captured request and several variants of it, the raw model
response of each run. "Three of eleven dimensions differ" and "one dimension flipped" are different
claims, and neither can be read off a count of A letters, so this resolves the codes to dimension
ids and lists what actually moved.

The mapping from `q0`, `q1` to a dimension id comes from the **captured adapter request** that the
replay was made from: the `format` schema's property order is the adapter's question order, and the
adapter's own answer map for the same case is keyed by dimension id. Both orders are the adapter's,
which is what makes the alignment a fact rather than a guess, and the script checks that the two
agree before it reports anything.

Run it from the repository root:

    python3 tools/nimble-survey/replay_divergence.py \
        --replay .styloagent/scratch/nimble/survey/nimble-replay-flip.json \
        --captured .styloagent/scratch/nimble/corpus-measurement-with-requests.json \
        --out .styloagent/scratch/nimble/replay-divergence.json
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import re
import sys
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
SURVEY = HERE / "survey_nimble.py"


def load_survey():
    """The survey's dimension reader, reused rather than re-implemented."""
    spec = importlib.util.spec_from_file_location("survey_nimble", SURVEY)
    module = importlib.util.module_from_spec(spec)
    sys.modules["survey_nimble"] = module
    spec.loader.exec_module(module)
    return module


def load(path: Path) -> dict:
    # A BOM is tolerated rather than assumed away: artifacts written before 1 Oct carry one.
    return json.loads(path.read_text(encoding="utf-8-sig"))


def question_order(case: dict, dimensions: list[dict]) -> list[str]:
    """Map `q0`..`qN` to dimension ids by CONTENT, not by position.

    The adapter renders `q0. <instructions>` into the system prompt, so the question text is in the
    captured request and the instruction text is in `SemanticDimension.cs`. Matching the two makes
    the mapping a fact about the request; taking the adapter's answer map in insertion order instead
    would be an assumption about how the adapter builds it, and an assumption is what this whole
    file exists to avoid.
    """
    system = case["sent_request"]["system"]
    rendered = re.findall(r"^q(\d+)\. (.+)$", system, re.M)
    properties = list(case["sent_request"]["format"]["properties"])
    if len(rendered) != len(properties):
        raise SystemExit(
            f"the captured system prompt carries {len(rendered)} questions and the schema "
            f"{len(properties)} properties; the capture is not self-consistent"
        )

    by_instructions = {d["instructions"].strip(): d["id"] for d in dimensions}
    if len(by_instructions) != len(dimensions):
        raise SystemExit("two dimensions share an instruction text, so the mapping would be ambiguous")

    order: list[str | None] = [None] * len(rendered)
    for index_text, instructions in rendered:
        dimension_id = by_instructions.get(instructions.strip())
        if dimension_id is None:
            raise SystemExit(
                f"q{index_text}'s instructions are not any dimension's in {SURVEY.name}; refusing to "
                "report a divergence against a mapping that could not be established"
            )
        order[int(index_text)] = dimension_id
    if any(entry is None for entry in order):
        raise SystemExit("the captured prompt's question numbering has a gap")
    return order  # type: ignore[return-value]


def codes_of(run: dict) -> dict[str, str]:
    return json.loads(run["raw_response"])


def majority_codes(variant: dict) -> dict[str, str]:
    """The code each question held in the most runs of this variant, and how many runs agreed."""
    per_question: dict[str, Counter] = {}
    for run in variant["runs"]:
        if run.get("error") or not run.get("raw_response"):
            continue
        for key, code in codes_of(run).items():
            per_question.setdefault(key, Counter())[code] += 1
    return {key: counts.most_common(1)[0][0] for key, counts in per_question.items()}


def agreement(variant: dict) -> tuple[int, int, list[str]]:
    """How many runs produced the identical whole vector, out of how many, and the vectors seen."""
    vectors = []
    for run in variant["runs"]:
        if run.get("error") or not run.get("raw_response"):
            continue
        codes = codes_of(run)
        vectors.append("".join(codes[key] for key in sorted(codes, key=lambda k: int(k[1:]))))
    if not vectors:
        return 0, 0, []
    counts = Counter(vectors)
    return counts.most_common(1)[0][1], len(vectors), sorted(counts)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--replay", required=True, help="A replay artifact (nimble-replay-flip*.json).")
    parser.add_argument("--captured", required=True, help="Captured adapter requests (corpus-measurement-with-requests.json).")
    parser.add_argument("--out", required=True)
    parser.add_argument(
        "--repo-root", default=".",
        help="Repository root, for reading SemanticDimension.cs. Run from the root and this is right.",
    )
    parser.add_argument(
        "--baseline", default="verbatim",
        help="The variant everything else is compared against. `verbatim` is the captured request "
             "unchanged, which is the only variant that is the shipping shape.",
    )
    args = parser.parse_args()

    replay = load(Path(args.replay))
    captured = load(Path(args.captured))
    case = next(c for c in captured["cases"] if c["case"] == replay["case"])
    survey = load_survey()
    dimensions = survey.load_dimensions(Path(args.repo_root).resolve())
    order = question_order(case, dimensions)

    # The alignment check. The replay's own record of what it replayed must match the adapter's
    # answer vector for the same case, or the mapping below is a guess and the report is noise.
    q_keys = [f"q{index}" for index in range(len(order))]
    baseline_codes = majority_codes(replay["variants"][args.baseline])
    adapter_codes = [case["codes"][dimension] for dimension in order]
    if [baseline_codes.get(key) for key in q_keys] != adapter_codes:
        raise SystemExit(
            "the replayed request's answer vector does not match the adapter's recorded answers for "
            f"{replay['case']}: the mapping from q-index to dimension cannot be trusted, so no "
            "divergence is reported. Re-capture the request and re-run the replay."
        )

    report = {
        "replay_artifact": args.replay,
        "captured_requests": args.captured,
        "case": replay["case"],
        "baseline_variant": args.baseline,
        "dimension_order": order,
        "alignment_checked_against_adapter_answers": True,
        "variants": {},
    }

    for name, variant in replay["variants"].items():
        codes = majority_codes(variant)
        differing_baseline = [
            {
                "index": index,
                "dimension": dimension,
                "baseline": baseline_codes.get(q_keys[index]),
                "variant": codes.get(q_keys[index]),
            }
            for index, dimension in enumerate(order)
            if codes.get(q_keys[index]) != baseline_codes.get(q_keys[index])
        ]
        agreed, total, vectors = agreement(variant)
        report["variants"][name] = {
            "prompt_characters": variant.get("prompt_characters"),
            "answers": variant.get("answers"),
            "whole_vector_agreement": {"runs_agreeing": agreed, "runs": total, "distinct_vectors": vectors},
            "differs_from_baseline_count": len(differing_baseline),
            "differs_from_baseline": differing_baseline,
        }
        print(f"\n{name}: {agreed}/{total} runs on the identical whole vector "
              f"({len(vectors)} distinct vector(s))")
        print(f"  differs from {args.baseline} in {len(differing_baseline)} of {len(order)} dimensions")
        for entry in differing_baseline:
            print(f"    q{entry['index']:<2} {entry['dimension']:<46} "
                  f"{entry['baseline']} -> {entry['variant']}")

    Path(args.out).write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"\nwrote {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
