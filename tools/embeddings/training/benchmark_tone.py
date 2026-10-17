"""Measure the two GoEmotions candidates on the inference GPU: latency and label coverage.

Tone is a sibling series with its own tag. This script only decides which model earns that tag; it
does not touch element scoring and it does not blend tone into an element number.

Coverage is reported two ways because either alone is easy to game: the share of segments whose top
label clears 0.50, and the number of distinct labels that appear with a mean score above 0.10 across
the sample. A model that returns 28 labels with uniformly low scores has vocabulary coverage and no
signal; a model that always answers "neutral" has neither.

    python benchmark_tone.py --labels corpus-labels.jsonl --output tone-metrics.json
"""

from __future__ import annotations

import argparse
import json
import pathlib
import statistics
import time

MODELS = (
    "SamLowe/roberta-base-go_emotions",
    "j-hartmann/emotion-english-distilroberta-base",
)

PROBE = (
    "Please review the attached invoice and confirm whether the purchase order number on the second "
    "page matches the one recorded in your system, because the accounts team cannot release payment "
    "until the reference agrees and the delivery window is closing. "
) * 3


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--labels", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--sample", type=int, default=20)
    parser.add_argument("--runs", type=int, default=5)
    args = parser.parse_args()

    import torch
    from transformers import pipeline

    rows = [json.loads(line) for line in args.labels.read_text(encoding="utf-8").splitlines() if line.strip()]
    sample = [row["text"][:1200] for row in rows[: args.sample]]
    probe = " ".join(PROBE.split()[:200])

    results = {}
    for model_id in MODELS:
        started = time.time()
        classifier = pipeline("text-classification", model=model_id, top_k=None, device=0 if torch.cuda.is_available() else -1)
        load_seconds = round(time.time() - started, 2)

        classifier(probe[:200])
        latencies = []
        for _ in range(args.runs):
            begin = time.perf_counter()
            classifier(probe)
            latencies.append((time.perf_counter() - begin) * 1000.0)

        top_scores = []
        observed: dict[str, list[float]] = {}
        for text in sample:
            scores = classifier(text)
            for item in scores:
                observed.setdefault(item["label"], []).append(float(item["score"]))
            top_scores.append(max(float(item["score"]) for item in scores))

        coverage = sum(1 for score in top_scores if score >= 0.5) / len(top_scores)
        mean_scores = {label: statistics.mean(values) for label, values in observed.items()}
        active = sorted(label for label, mean in mean_scores.items() if mean > 0.10)
        results[model_id] = {
            "load_seconds": load_seconds,
            "labels": len(observed),
            "latency_ms_median": round(statistics.median(latencies), 1),
            "latency_ms_min": round(min(latencies), 1),
            "latency_ms_max": round(max(latencies), 1),
            "coverage_top_ge_0_5": round(coverage, 3),
            "active_labels": active,
            "active_label_count": len(active),
            "mean_top_score": round(statistics.mean(top_scores), 4),
        }
        print(model_id, results[model_id], flush=True)

    args.output.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
