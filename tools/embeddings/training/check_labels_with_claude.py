"""Check a sample of teacher labels with the headless Claude CLI as a different model lineage.

This is the independent check the same-model second prompt cannot provide. It is deliberately small:
20 labelled segments, and only those leave the machine. The script records that boundary, the model
that answered, the prompt hash and the version of the CLI, so the transport is auditable.

Disagreement is the output. Rows where the two models agree are counted; rows where they differ are
listed label by label, because a 0.96 agreement between two prompts of one model was internal
consistency, not correctness.

    python check_labels_with_claude.py --labels teacher-labels-r2.jsonl --out claude-check.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
import shutil
import subprocess

ELEMENTS = ("credential_request", "urgency_pressure", "threat")

DEFINITIONS = """credential_request: asks the reader to send, enter or confirm credentials. POSITIVE: "reply
with your password and PIN today". NEGATIVE: "your password was reset successfully".
urgency_pressure: imposes a short deadline or threatens loss for not acting. POSITIVE: "you have two
hours or the account is closed". NEGATIVE: "please send the report by Friday".
threat: threatens harm, exposure, legal action or retaliation. POSITIVE: "we will publish your files
unless you pay". NEGATIVE: "unpaid invoices may incur a late fee".
credential_request is PROVISIONAL in elements/2: the teacher missed rows an independent check found,
so label it only when the segment itself asks for a credential."""

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
        if isinstance(entry, str):
            name = entry.strip()
            quote = None
        elif isinstance(entry, dict):
            name = str(entry.get("element", "")).strip()
            quote = entry.get("quote")
        else:
            return None
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
    parser.add_argument("--rows", type=int, default=20)
    args = parser.parse_args()

    claude = shutil.which("claude")
    if claude is None:
        raise SystemExit("REFUSING: no claude CLI on PATH")
    version = subprocess.run([claude, "--version"], capture_output=True, text=True).stdout.strip()

    rows = [json.loads(line) for line in args.labels.read_text(encoding="utf-8").splitlines() if line.strip()][: args.rows]
    results = []
    for row in rows:
        prompt = TEMPLATE.format(definitions=DEFINITIONS, exclusions=EXCLUSIONS, segment=row["text"])
        completed = subprocess.run(
            [claude, "-p", prompt, "--model", args.model, "--output-format", "text"],
            capture_output=True, text=True, timeout=180)
        claude_labels = parse(completed.stdout, row["text"])
        teacher_labels = sorted(row.get("labels") or [])
        results.append({
            "id": row["id"],
            "teacher": teacher_labels,
            "claude": claude_labels,
            "agree": claude_labels == teacher_labels,
            "teacher_only": sorted(set(teacher_labels) - set(claude_labels or [])),
            "claude_only": sorted(set(claude_labels or []) - set(teacher_labels)),
            "claude_raw_tail": completed.stdout[-200:] if claude_labels is None else None,
            "text": row["text"][:200],
        })

    agreements = sum(1 for row in results if row["agree"])
    document = {
        "rows": len(results),
        "agreement": round(agreements / len(results), 4) if results else None,
        "transport": "headless Claude CLI, 20 sample segments only; no other corpus text left the machine",
        "claude_cli_version": version,
        "claude_model": args.model,
        "prompt_hash": hashlib.sha256(TEMPLATE.encode("utf-8")).hexdigest()[:16],
        "results": results,
        "note": "Disagreement is the output. A label that both models share can still be wrong.",
    }
    args.out.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in document.items() if k != "results"}, indent=2))
    for row in results:
        if not row["agree"]:
            print("DISAGREE", row["id"], "teacher", row["teacher"], "claude", row["claude"])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
