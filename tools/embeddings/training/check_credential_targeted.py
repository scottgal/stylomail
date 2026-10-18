"""Targeted independent check for credential_request, with a pre-registered criterion.

The 60-row check left credential_request's boundary unstable: 3 teacher-only labels in one sample and
3 claude-only labels in the earlier one. This sample is aimed at that element specifically and its
pass criterion was fixed before the run:

  PASS only if (i) exact agreement on the credential_request union is at least 0.85, AND
              (ii) |teacher_only - claude_only| <= 3.
  Failing either, credential_request leaves the vocabulary.

Sampling frame and rule are recorded in the output. Only the sampled segments leave the machine.
Wilson 95 percent intervals are reported beside each proportion so the reader can see the power.

    python check_credential_targeted.py --labels teacher-labels-r5.jsonl --out credential-check.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import pathlib
import re
import shutil
import subprocess

ELEMENT = "credential_request"
ELEMENTS = ("credential_request", "urgency_pressure", "threat")
CUES = re.compile(
    r"\b(password|passwd|credentials?|login|log in|sign in|signin|verify|verification|unlock|"
    r"account number|online banking|pin|two factor|2fa)\b", re.IGNORECASE)

DEFINITIONS = """credential_request: asks the reader to send, enter or confirm credentials. POSITIVE: "reply
with your password and PIN today". NEGATIVE: "your password was reset successfully".
urgency_pressure: imposes a short deadline or threatens loss for not acting. POSITIVE: "you have two
hours or the account is closed". NEGATIVE: "please send the report by Friday".
threat: threatens harm, exposure, legal action or retaliation. POSITIVE: "we will publish your files
unless you pay". NEGATIVE: "unpaid invoices may incur a late fee"."""

EXCLUSIONS = """A security notice that reports an action already taken is benign. A document that is
merely attached and described as available is benign. A generic greeting or signature is not
impersonation. An element fires only if the segment itself contains the act, not a polite template
around it. Prefer none."""

TEMPLATE = """Label each element present in this segment.

{definitions}

{exclusions}

For every label, quote the exact phrase from the segment that justifies it. A label without a
verbatim quote is not a label. Return ONLY JSON: {{"labels":[{{"element":"<name>","quote":"<exact phrase>"}}]}}
An empty list is correct for benign text.

Segment:
{segment}
"""


def wilson(successes: int, total: int, z: float = 1.96) -> tuple[float, float]:
    if total == 0:
        return (0.0, 0.0)
    p = successes / total
    denominator = 1 + z * z / total
    centre = (p + z * z / (2 * total)) / denominator
    margin = z * math.sqrt(p * (1 - p) / total + z * z / (4 * total * total)) / denominator
    return (round(max(0.0, centre - margin), 4), round(min(1.0, centre + margin), 4))


def parse(raw: str, segment: str) -> list[str] | None:
    match = re.search(r"\{.*\}", raw, re.DOTALL)
    if not match:
        return None
    try:
        document = json.loads(match.group(0))
    except json.JSONDecodeError:
        return None
    labels = document.get("labels")
    if labels is None:
        return None
    if isinstance(labels, str):
        labels = [] if labels.strip().lower() in ("none", "") else [labels]
    if not isinstance(labels, list):
        return None
    haystack = " ".join(segment.lower().split())
    cleaned = []
    for entry in labels:
        name = entry.get("element") if isinstance(entry, dict) else entry
        quote = entry.get("quote") if isinstance(entry, dict) else None
        name = str(name or "").strip()
        if name.lower() in ("none", ""):
            continue
        if name not in ELEMENTS:
            return None
        if not isinstance(quote, str) or " ".join(quote.lower().split()) not in haystack:
            return None
        if name not in cleaned:
            cleaned.append(name)
    return sorted(cleaned)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--labels", type=pathlib.Path, required=True)
    parser.add_argument("--out", type=pathlib.Path, required=True)
    parser.add_argument("--model", default="sonnet")
    parser.add_argument("--rows", type=int, default=150)
    parser.add_argument("--half", type=int, default=75)
    args = parser.parse_args()

    claude = shutil.which("claude")
    if claude is None:
        raise SystemExit("REFUSING: no claude CLI on PATH")
    version = subprocess.run([claude, "--version"], capture_output=True, text=True).stdout.strip()

    rows = [json.loads(line) for line in args.labels.read_text(encoding="utf-8").splitlines() if line.strip()]
    positives = [row for row in rows if ELEMENT in (row.get("labels") or [])]
    negatives = [row for row in rows if ELEMENT not in (row.get("labels") or []) and CUES.search(row["text"])]
    sample = positives[: args.half] + negatives[: args.rows - min(len(positives), args.half)]
    frame = {
        "pool_rows": len(rows),
        "credential_positives_available": len(positives),
        "credential_cued_negatives_available": len(negatives),
        "selected_positives": min(len(positives), args.half),
        "selected_negatives": len(sample) - min(len(positives), args.half),
        "selection_rule": (
            "all teacher-labelled credential_request positives up to 75, then hard negatives whose text "
            "contains a credential cue but which the teacher did not label credential_request, up to 150"
        ),
    }

    results = []
    for row in sample:
        prompt = TEMPLATE.format(definitions=DEFINITIONS, exclusions=EXCLUSIONS, segment=row["text"])
        completed = subprocess.run(
            [claude, "-p", prompt, "--model", args.model, "--output-format", "text"],
            capture_output=True, text=True, timeout=180)
        claude_labels = parse(completed.stdout, row["text"])
        teacher_labels = sorted(row.get("labels") or [])
        teacher = ELEMENT in teacher_labels
        found = claude_labels is not None and ELEMENT in claude_labels
        results.append({
            "id": row["id"],
            "teacher": teacher,
            "claude": found,
            "claude_parsed": claude_labels is not None,
            "teacher_all": teacher_labels,
            "claude_all": claude_labels,
        })

    both = sum(1 for row in results if row["teacher"] and row["claude"])
    teacher_only = sum(1 for row in results if row["teacher"] and not row["claude"])
    claude_only = sum(1 for row in results if row["claude"] and not row["teacher"])
    neither = len(results) - both - teacher_only - claude_only
    union = both + teacher_only + claude_only
    agreement = (both + neither) / len(results) if results else 0.0
    agreement_union = both / union if union else 0.0
    low, high = wilson(both + neither, len(results))

    criterion = {
        "agreement_on_union_at_least": 0.85,
        "absolute_directional_difference_at_most": 3,
    }
    passed = agreement_union >= 0.85 and abs(teacher_only - claude_only) <= 3

    document = {
        "element": ELEMENT,
        "criterion": criterion,
        "passed": passed,
        "rows_checked": len(results),
        "sampling_frame": frame,
        "counts": {"both": both, "teacher_only": teacher_only, "claude_only": claude_only, "neither": neither},
        "union": union,
        "agreement_all_rows": round(agreement, 4),
        "agreement_on_union": round(agreement_union, 4),
        "agreement_all_rows_wilson95": [low, high],
        "transport": f"headless Claude CLI, {len(results)} sample segments only; no other corpus text left the machine",
        "claude_cli_version": version,
        "claude_model": args.model,
        "prompt_hash": hashlib.sha256(TEMPLATE.encode("utf-8")).hexdigest()[:16],
        "results": results,
    }
    args.out.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in document.items() if k != "results"}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
