"""Teacher-label real segments, with a quoted justification per label and an independent check.

Revision 2 of the teacher, from the spot-check's measured failure: templated benign mail was labelled
as an element because it contained a polite phrase ("Sincerely, PayPal Security", "the attached
document is ready for review"). Agreement between two prompts of one model was 0.96 and still wrong.

Four rules now apply, and they are enforced in code rather than trusted:

1. Narrow definitions with a positive and a negative example per element, and the rule that an element
   fires only if the segment itself contains the act, not a template around it.
2. Explicit exclusions: a security notice reporting an action already taken is benign; a document
   merely attached and described as available is benign; a generic greeting is not impersonation.
3. Quote justification: every label must carry the exact phrase from the segment that justifies it,
   and the phrase is verified verbatim before the label is kept. No quote, no label.
4. Prefer none. Multi-label is allowed, each label with its own quote.

A second prompt from the same model checks the labels, and the manifest labels that as a second
prompt, never as an independent verifier.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import random
import re
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

OLLAMA = os.environ.get("EMB_OLLAMA", "http://127.0.0.1:11434")
ELEMENTS = ("credential_request", "urgency_pressure", "threat")
MAX_SEGMENTS = 8
MAX_SEGMENT_CHARS = 6000

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

TEACHER_TEMPLATE = """Label each element that is present in this segment.

{elements}

{exclusions}

For EVERY label, quote the exact phrase from the segment that justifies it. A label without a verbatim
quote from the segment is not a label. Multi-label is allowed, each with its own quote.

Return ONLY JSON: {{"labels":[{{"element":"<name>","quote":"<exact phrase>"}}]}}
An empty list is the correct answer for benign text.

Segment:
{segment}
"""

VERIFY_TEMPLATE = """Independently label this segment. Do not assume any prior answer.

{elements}

{exclusions}

For every label, quote the exact phrase that justifies it. Return ONLY JSON:
{{"labels":[{{"element":"<name>","quote":"<exact phrase>"}}]}}

Segment:
{segment}
"""


def prompt_hash(template: str) -> str:
    return hashlib.sha256(template.encode("utf-8")).hexdigest()[:16]


def call(model: str, prompt: str, seed: int) -> str:
    body = json.dumps({
        "model": model, "prompt": prompt, "stream": False, "format": "json", "think": False,
        "options": {"temperature": 0.1, "seed": seed, "num_predict": 400},
    }).encode("utf-8")
    request = urllib.request.Request(OLLAMA + "/api/generate", data=body,
                                     headers={"Content-Type": "application/json"})
    last_error: Exception | None = None
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=300) as response:
                text = json.loads(response.read()).get("response", "")
            if text.strip():
                return text
            last_error = RuntimeError("empty response")
        except urllib.error.HTTPError as error:
            if error.code < 500 or attempt == 2:
                raise
            last_error = error
        time.sleep(2 * (attempt + 1))
    raise last_error if last_error else RuntimeError("teacher returned nothing")


def labels_of(raw: str, segment: str) -> list[str] | None:
    """Parse labels, requiring each quote to appear verbatim in the segment."""
    try:
        document = json.loads(raw)
    except json.JSONDecodeError:
        return None
    labels = document.get("labels")
    if labels is None:
        return None
    if isinstance(labels, str):
        labels = [] if labels.strip().lower() in ("none", "") else [labels]
    if not isinstance(labels, list):
        return None

    cleaned = []
    haystack = " ".join(segment.lower().split())
    for entry in labels:
        if isinstance(entry, str):
            name, quote = entry.strip(), None
        elif isinstance(entry, dict):
            name, quote = str(entry.get("element", "")).strip(), entry.get("quote")
        else:
            return None
        if name.lower() in ("none", ""):
            continue
        if name not in ELEMENTS:
            return None
        if not isinstance(quote, str) or len(quote.strip()) < 3:
            return None
        if " ".join(quote.lower().split()) not in haystack:
            return None
        if name not in cleaned:
            cleaned.append(name)
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
    parser.add_argument("--corpus", type=pathlib.Path, default=None)
    parser.add_argument("--seed-labels", type=pathlib.Path, default=None)
    parser.add_argument("--teacher", default="gemma4:e4b")
    parser.add_argument("--verifier", default="gemma4:e4b")
    parser.add_argument("--concurrency", type=int, default=3)
    parser.add_argument("--seed", type=int, default=20261018)
    parser.add_argument("--spotcheck-out", type=pathlib.Path, default=None)
    args = parser.parse_args()

    def read(path: pathlib.Path, source: str) -> list[dict]:
        output = []
        for line in path.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            row = json.loads(line)
            output.append({"id": f"{source}:{row['id']}", "text": row["text"],
                           "split": row.get("split", "train"), "source": source})
        return output

    rng = random.Random(args.seed)
    pools = {"operator": read(args.input, "operator")}
    if args.corpus is not None and args.corpus.exists():
        pools["corpus"] = read(args.corpus, "corpus")
    if args.seed_labels is not None and args.seed_labels.exists():
        pools["authored"] = read(args.seed_labels, "authored")

    per_source = max(1, args.rows // len(pools))
    sample = []
    for pool in pools.values():
        sample.extend(rng.sample(pool, min(per_source, len(pool))))

    work = []
    for row in sample:
        for ordinal, segment in enumerate(segments_of(row["text"])):
            work.append((row, ordinal, segment))

    elements_block = DEFINITIONS
    exclusions_block = EXCLUSIONS

    def label_one(item):
        row, ordinal, segment = item
        seed = int(hashlib.sha256(f"{row['id']}:{ordinal}".encode()).hexdigest()[:8], 16)
        teacher_prompt = TEACHER_TEMPLATE.format(
            elements=elements_block, exclusions=exclusions_block, segment=segment)
        verifier_prompt = VERIFY_TEMPLATE.format(
            elements=elements_block, exclusions=exclusions_block, segment=segment)
        teacher = labels_of(call(args.teacher, teacher_prompt, seed), segment)
        verifier = labels_of(call(args.verifier, verifier_prompt, seed), segment)
        return row, ordinal, segment, teacher, verifier, seed

    results = []
    with ThreadPoolExecutor(max_workers=args.concurrency) as pool:
        for index, item in enumerate(pool.map(label_one, work), start=1):
            results.append(item)
            if index % 50 == 0:
                print(f"labelled {index}/{len(work)}", flush=True)

    kept, disagreements, parse_failures = [], 0, 0
    per_label = {element: 0 for element in ELEMENTS}
    element_agreement = {element: {"both": 0, "teacher_only": 0, "verifier_only": 0} for element in ELEMENTS}

    for row, ordinal, segment, teacher, verifier, seed in results:
        if teacher is None or verifier is None:
            parse_failures += 1
            continue
        for element in ELEMENTS:
            in_teacher, in_verifier = element in teacher, element in verifier
            if in_teacher and in_verifier:
                element_agreement[element]["both"] += 1
            elif in_teacher:
                element_agreement[element]["teacher_only"] += 1
            elif in_verifier:
                element_agreement[element]["verifier_only"] += 1
        if teacher != verifier:
            disagreements += 1
            continue
        for label in teacher:
            per_label[label] += 1
        kept.append({
            "id": f"{row['id']}:{ordinal}", "source_row": row["id"], "source": row["source"],
            "ordinal": ordinal, "text": segment, "labels": teacher, "split": row["split"],
            "teacher_model": args.teacher, "verifier_model": args.verifier,
            "teacher_prompt_hash": prompt_hash(TEACHER_TEMPLATE),
            "verifier_prompt_hash": prompt_hash(VERIFY_TEMPLATE),
            "seed": seed, "licence": "project-owned labels over operator-provided text",
        })

    args.output.write_text("\n".join(json.dumps(row) for row in kept) + "\n", encoding="utf-8")

    spotcheck = None
    if args.spotcheck_out is not None:
        lines = ["id,source,split,labels,text"]
        for row in kept[:20]:
            text = row["text"].replace('"', "'")[:400]
            labels = "|".join(row["labels"]) if row["labels"] else "none"
            lines.append(f'{row["id"]},{row["source"]},{row["split"]},{labels},"{text}"')
        args.spotcheck_out.write_text("\n".join(lines) + "\n", encoding="utf-8")
        spotcheck = str(args.spotcheck_out)

    total = len(results)
    agreement = round(len(kept) / total, 4) if total else 0.0
    manifest = {
        "rows_sampled": len(sample),
        "sources": {source: len(pool) for source, pool in pools.items()},
        "segments_labelled": total,
        "segments_kept": len(kept),
        "exact_agreement_rate": agreement,
        "agreement_gate": 0.80,
        "agreement_passed": agreement >= 0.80,
        "seed_permitted": False,
        "disagreements_discarded": disagreements,
        "parse_failures": parse_failures,
        "per_element_counts": per_label,
        "per_element_agreement": element_agreement,
        "elements_with_no_positives": [e for e, c in per_label.items() if c == 0],
        "spotcheck_file": spotcheck,
        "teacher_model": args.teacher,
        "verifier_model": args.verifier,
        "verifier_role": "second prompt (same model), not an independent verifier",
        "teacher_prompt_hash": prompt_hash(TEACHER_TEMPLATE),
        "verifier_prompt_hash": prompt_hash(VERIFY_TEMPLATE),
        "seed": args.seed,
        "note": (
            "Every kept label carried a quote verified verbatim in the segment. Seeding is blocked "
            "until an independent-model check and a human spot-check both say the labels are right."
        ),
    }
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
