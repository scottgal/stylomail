"""Ingest an operator-provided document-level dataset for scale, never for hard negatives.

Three rules shape this file:

1. TEXT ONLY. The allow-list below is the only thing read from the CSV. Every precomputed column in
   that file (phishing_probability, urgency_score, suspicious_keyword_count, grammar_score,
   sender_reputation and the rest) is label leakage: it was derived from the label or from the same
   generator, and a model trained on it would report a beautiful meaningless metric.
2. PROVENANCE PER ROW. source is "operator-provided", licence unknown, generator suspected. It is
   not Nazario, not CEAS, and not real provider mail.
3. NO RAW ROWS IN THE REPO. The derived JSONL lives beside the corpora cache and is git-ignored; only
   counts and metrics are committed.

The dataset is document-level, so it cannot label elements. It is used for document-level baseline
work and benign volume, while the curated real-provider rows stay the FPR set.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import pathlib
import re

ALLOWED_COLUMNS = ("subject", "email_body", "label")
SOURCE = "operator-provided-dataset-v2"
LICENCE = "unknown; generator suspected (synthetic characteristics reported by overview-)"


def redact(text: str) -> str:
    text = re.sub(r"\b[\w.+-]+@[\w-]+\.[\w.]+\b", "[email]", text)
    text = re.sub(r"https?://\S+", "[url]", text)
    text = re.sub(r"\b(?:\+?\d[\d\s().-]{7,}\d)\b", "[number]", text)
    return text


def normalise(text: str) -> str:
    return re.sub(r"[^a-z0-9 ]", "", text.lower())


def digest(text: str) -> str:
    return hashlib.sha256(normalise(text).encode("utf-8")).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--manifest", type=pathlib.Path, required=True)
    parser.add_argument("--min-chars", type=int, default=60)
    args = parser.parse_args()

    seen: set[str] = set()
    rows = []
    read = 0
    dropped_empty = 0
    dropped_duplicate = 0
    labels = {"Phishing": 0, "Legitimate": 0}

    with args.input.open(newline="", encoding="utf-8", errors="replace") as handle:
        reader = csv.DictReader(handle)
        missing = [column for column in ALLOWED_COLUMNS if column not in (reader.fieldnames or [])]
        if missing:
            raise SystemExit(f"REFUSING: input lacks {missing}")
        for record in reader:
            read += 1
            label_raw = (record.get("label") or "").strip()
            if label_raw not in labels:
                continue
            text = f"{record.get('subject') or ''}\n\n{record.get('email_body') or ''}".strip()
            text = re.sub(r"\s+", " ", text)
            text = redact(text)
            if len(text) < args.min_chars:
                dropped_empty += 1
                continue
            key = digest(text)
            if key in seen:
                dropped_duplicate += 1
                continue
            seen.add(key)
            labels[label_raw] += 1
            split = "heldout" if int(key[:2], 16) % 10 < 3 else "train"
            rows.append({
                "id": f"op:{key[:16]}",
                "label": 1 if label_raw == "Phishing" else 0,
                "split": split,
                "source": SOURCE,
                "licence": LICENCE,
                "text_digest": key,
                "text": text,
            })

    args.output.write_text("\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")
    manifest = {
        "source_file": str(args.input),
        "source": SOURCE,
        "licence": LICENCE,
        "columns_used": list(ALLOWED_COLUMNS),
        "columns_ignored_because_they_are_label_leakage": sorted(
            set(csv.DictReader(args.input.open(encoding="utf-8", errors="replace")).fieldnames or [])
            - set(ALLOWED_COLUMNS)
        ),
        "rows_read": read,
        "rows_kept": len(rows),
        "dropped_empty_or_short": dropped_empty,
        "dropped_duplicate": dropped_duplicate,
        "labels": labels,
        "split": {
            "train": sum(1 for row in rows if row["split"] == "train"),
            "heldout": sum(1 for row in rows if row["split"] == "heldout"),
        },
        "note": (
            "Document-level only. Cannot label elements. Used for scale and benign volume; curated "
            "real-provider rows remain the hard-negative and FPR set."
        ),
    }
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
