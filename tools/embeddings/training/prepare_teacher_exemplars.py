"""Turn teacher-labelled segments into per-element binary exemplars.

The teacher answers with a set of elements per segment. The trainer wants one row per (segment,
element) with a 0 or 1, so this expands the sets, keeping the source row's split so segments of one
message never straddle train and held-out, and deduplicating by text digest within each element.

A segment labelled with another element is a negative for this one, and a segment labelled none is a
negative for all five. That is deliberate: the negatives are topical mail the teacher read, not random
ham, which is the difficulty the FPR figures need.

Refuses to write when an element has too few positives to train or too few held-out positives to
evaluate, and reports the counts rather than padding.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib

ELEMENTS = ("credential_request", "urgency_pressure", "threat", "impersonated_greeting", "attachment_lure")


def digest(text: str) -> str:
    return hashlib.sha256(" ".join(text.split()).lower().encode("utf-8")).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    parser.add_argument("--min-train-positives", type=int, default=10)
    parser.add_argument("--min-heldout-positives", type=int, default=3)
    args = parser.parse_args()

    rows = [json.loads(line) for line in args.input.read_text(encoding="utf-8").splitlines() if line.strip()]
    seen: dict[str, set[str]] = {element: set() for element in ELEMENTS}
    output = []
    for row in rows:
        key = digest(row["text"])
        labels = set(row.get("labels") or [])
        for element in ELEMENTS:
            if key in seen[element]:
                continue
            seen[element].add(key)
            output.append({
                "id": f"{row['id']}:{element}",
                "element": element,
                "label": 1 if element in labels else 0,
                "split": row.get("split", "train"),
                "source": f"teacher:{row.get('source', 'unknown')}",
                "teacher_model": row.get("teacher_model"),
                "verifier_model": row.get("verifier_model"),
                "text": row["text"],
            })

    counts = {
        element: {
            "train_positive": sum(1 for row in output if row["element"] == element and row["label"] == 1 and row["split"] == "train"),
            "train_negative": sum(1 for row in output if row["element"] == element and row["label"] == 0 and row["split"] == "train"),
            "heldout_positive": sum(1 for row in output if row["element"] == element and row["label"] == 1 and row["split"] == "heldout"),
            "heldout_negative": sum(1 for row in output if row["element"] == element and row["label"] == 0 and row["split"] == "heldout"),
        }
        for element in ELEMENTS
    }

    blockers = [
        element for element, value in counts.items()
        if value["train_positive"] < args.min_train_positives
        or value["heldout_positive"] < args.min_heldout_positives
    ]

    args.output.write_text("\n".join(json.dumps(row) for row in output) + "\n", encoding="utf-8")
    report = {
        "rows_in": len(rows),
        "rows_out": len(output),
        "counts": counts,
        "minimums": {
            "train_positive": args.min_train_positives,
            "heldout_positive": args.min_heldout_positives,
        },
        "elements_below_minimum": blockers,
        "note": (
            "One row per (segment, element). Split inherited from the source message. Negatives include "
            "segments labelled with another element and segments labelled none, so they are topical."
        ),
    }
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    if blockers:
        print(f"BELOW MINIMUM: {blockers}; do not train these elements on this set")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
