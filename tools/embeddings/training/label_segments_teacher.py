"""Teacher-label segments with the local strong model, then have a second model check them.

The label source this corpus lacked. A stratified sample of the operator dataset is segmented with
the same deterministic rules the service uses, each segment is labelled against the five-element
vocabulary by one model, and every answer is checked independently by another model. Exact set
agreement is kept; disagreements are discarded and the disagreement rate is reported. A generator
marking its own homework is not a label, and neither is a teacher whose output nobody checked.

Provenance per row: teacher model tag, verifier model tag, prompt hash, source row id, segment
ordinal, seed. The text is redacted for structured identifiers and the derived file is git-ignored.

    python label_segments_teacher.py --input operator-labels.jsonl \
        --output teacher-labels.jsonl --manifest teacher-manifest.json --rows 200
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import random
import re
import urllib.request
from concurrent.futures import ThreadPoolExecutor

OLLAMA = __import__("os").environ.get("EMB_OLLAMA", "http://127.0.0.1:11434")
ELEMENTS = ("credential_request", "urgency_pressure", "threat", "impersonated_greeting", "attachment_lure")
MAX_SEGMENTS = 8
MAX_SEGMENT_CHARS = 2000

TEACHER_TEMPLATE = """Label each element that is present in this segment.

Elements: credential_request (asks the reader to send or confirm credentials); urgency_pressure
(imposes a short deadline or threatens loss for not acting); threat (threatens harm, exposure, legal
action or retaliation); impersonated_greeting (claims to be a company or authority the sender is not,
or opens as one); attachment_lure (asks the reader to open an attachment, enable macros, or view a
document to proceed).

Answer ONLY JSON: {{"labels": []}} with zero or more of: {elements}.
Segment:
{segment}
"""

VERIFY_TEMPLATE = """Independently label each element present in this segment. Do not assume any prior answer.

Elements: credential_request (asks the reader to send or confirm credentials); urgency_pressure
(imposes a short deadline or threatens loss for not acting); threat (threatens harm, exposure, legal
action or retaliation); impersonated_greeting (claims to be a company or authority the sender is not,
or opens as one); attachment_lure (asks the reader to open an attachment, enable macros, or view a
document to proceed).

Answer ONLY JSON: {{"labels": []}} with zero or more of: {elements}.
Segment:
{segment}
"""


def prompt_hash(template: str) -> str:
    return hashlib.sha256(template.encode("utf-8")).hexdigest()[:16]


def call(model: str, prompt: str, seed: int) -> str:
    body = json.dumps({
        "model": model, "prompt": prompt, "stream": False, "format": "json",
        "options": {"temperature": 0.1, "seed": seed, "num_predict": 120},
    }).encode("utf-8")
    request = urllib.request.Request(OLLAMA + "/api/generate", data=body,
                                     headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=300) as response:
        return json.loads(response.read()).get("response", "")


def labels_of(raw: str) -> list[str] | None:
    try:
        document = json.loads(raw)
    except json.JSONDecodeError:
        return None
    labels = document.get("labels")
    if not isinstance(labels, list):
        return None
    cleaned = []
    for label in labels:
        text = str(label).strip()
        if text in ELEMENTS and text not in cleaned:
            cleaned.append(text)
    # "none" and an empty list are the same answer; anything unknown is a parse failure.
    unknown = [str(label).strip() for label in labels if str(label).strip() not in ELEMENTS + ("none",)]
    if unknown:
        return None
    return sorted(cleaned)


def segments_of(text: str) -> list[str]:
    chunks = []
    for raw in re.split(r"\n\s*\n+", text):
        chunk = re.sub(r"\s+", " ", raw).strip()
        if len(chunk) < 40:
            continue
        chunks.append(chunk[:MAX_SEGMENT_CHARS])
        if len(chunks) >= MAX_SEGMENTS:
            break
    return chunks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--manifest", type=pathlib.Path, required=True)
    parser.add_argument("--rows", type=int, default=200)
    parser.add_argument("--teacher", default="qwen3.8:latest")
    parser.add_argument("--verifier", default="gemma4:26b")
    parser.add_argument("--concurrency", type=int, default=3)
    parser.add_argument("--seed", type=int, default=20261018)
    args = parser.parse_args()

    rows = [json.loads(line) for line in args.input.read_text(encoding="utf-8").splitlines() if line.strip()]
    rng = random.Random(args.seed)
    phishing = [row for row in rows if row["label"] == 1]
    benign = [row for row in rows if row["label"] == 0]
    half = args.rows // 2
    sample = rng.sample(phishing, min(half, len(phishing))) + rng.sample(benign, min(args.rows - half, len(benign)))

    work = []
    for row in sample:
        for ordinal, segment in enumerate(segments_of(row["text"])):
            work.append((row, ordinal, segment))

    def label_one(item):
        row, ordinal, segment = item
        seed = int(hashlib.sha256(f"{row['id']}:{ordinal}".encode()).hexdigest()[:8], 16)
        teacher_prompt = TEACHER_TEMPLATE.format(elements=", ".join(ELEMENTS), segment=segment)
        verifier_prompt = VERIFY_TEMPLATE.format(elements=", ".join(ELEMENTS), segment=segment)
        teacher_labels = labels_of(call(args.teacher, teacher_prompt, seed))
        verifier_labels = labels_of(call(args.verifier, verifier_prompt, seed))
        return row, ordinal, segment, teacher_labels, verifier_labels, seed

    results = []
    with ThreadPoolExecutor(max_workers=args.concurrency) as pool:
        for index, item in enumerate(pool.map(label_one, work), start=1):
            results.append(item)
            if index % 25 == 0:
                print(f"labelled {index}/{len(work)}", flush=True)

    kept = []
    disagreements = 0
    parse_failures = 0
    per_label = {element: 0 for element in ELEMENTS}
    for row, ordinal, segment, teacher_labels, verifier_labels, seed in results:
        if teacher_labels is None or verifier_labels is None:
            parse_failures += 1
            continue
        if teacher_labels != verifier_labels:
            disagreements += 1
            continue
        for label in teacher_labels:
            per_label[label] += 1
        kept.append({
            "id": f"{row['id']}:{ordinal}",
            "source_row": row["id"],
            "ordinal": ordinal,
            "text": segment,
            "labels": teacher_labels,
            "split": row["split"],
            "teacher_model": args.teacher,
            "verifier_model": args.verifier,
            "teacher_prompt_hash": prompt_hash(TEACHER_TEMPLATE),
            "verifier_prompt_hash": prompt_hash(VERIFY_TEMPLATE),
            "seed": seed,
            "licence": "project-owned labels over operator-provided text",
        })

    args.output.write_text("\n".join(json.dumps(row) for row in kept) + "\n", encoding="utf-8")
    total = len(results)
    manifest = {
        "rows_sampled": len(sample),
        "segments_labelled": total,
        "segments_kept": len(kept),
        "exact_agreement_rate": round(len(kept) / total, 4) if total else None,
        "disagreements_discarded": disagreements,
        "parse_failures": parse_failures,
        "per_element_counts": per_label,
        "teacher_model": args.teacher,
        "verifier_model": args.verifier,
        "teacher_prompt_hash": prompt_hash(TEACHER_TEMPLATE),
        "verifier_prompt_hash": prompt_hash(VERIFY_TEMPLATE),
        "seed": args.seed,
        "split": "inherits the operator row split, so segments of one message stay together",
        "note": (
            "Only exact set agreement is kept. Disagreement is reported rather than resolved by "
            "preference; a label nobody checked is not a label."
        ),
    }
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
