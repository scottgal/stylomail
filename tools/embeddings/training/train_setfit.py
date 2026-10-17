"""SetFit arm on the same splits as the kNN and the probe.

Same fit, calibration and held-out rows, the same element labels, and the same operating-point rule:
the threshold is chosen on the calibration split for a stated false-positive budget and applied
unchanged to held-out. The point is to see whether the probe is leaving signal on the table, so the
base sentence encoder is the same model the embeddings come from.

SetFit is run in its own environment, because it needs datasets and evaluate which the serving
environment does not. Its presence there does not change what the service ships.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib

import numpy as np

ELEMENTS = (
    "credential_request",
    "urgency_pressure",
    "threat",
    "impersonated_greeting",
    "attachment_lure",
)
BUDGET = 0.01


def text_key(row: dict) -> str:
    """Content identity: the basis of both the split and any stable identifier."""
    basis = " ".join(row["text"].split()).lower()
    return hashlib.sha256(basis.encode("utf-8")).hexdigest()


def calibration_set(rows: list[dict]) -> set[str]:
    """A stratified 20 percent of each element and label, stable by TEXT.

    Hashing the id was wrong and was caught by renaming exemplar ids: every id changed, so every row
    moved between fit and calibration and the metrics moved with them. Hashing the text fixes that.
    Stratifying by element and label fixes the other failure this had: a flat hash left an element
    with 23 positives holding only 2 in calibration, which the trainer rightly refused.
    """
    chosen: set[str] = set()
    for element in ELEMENTS:
        for label in (0, 1):
            group = sorted(
                (row for row in rows
                 if row["element"] == element and row["split"] == "train" and row["label"] == label),
                key=text_key,
            )
            for index, row in enumerate(group):
                if index % 5 == 0:
                    chosen.add(text_key(row))
    return chosen

def metrics(labels: np.ndarray, scores: np.ndarray, threshold: float, hard: np.ndarray) -> dict:
    predictions = (scores >= threshold).astype(int)
    tp = int(np.sum((labels == 1) & (predictions == 1)))
    fn = int(np.sum((labels == 1) & (predictions == 0)))
    fp = int(np.sum((labels == 0) & (predictions == 1)))
    tn = int(np.sum((labels == 0) & (predictions == 0)))
    hard_n = int(np.sum((labels == 0) & (hard == 1)))
    hard_fp = int(np.sum((labels == 0) & (hard == 1) & (predictions == 1)))
    return {
        "threshold": round(float(threshold), 6),
        "precision": None if tp + fp == 0 else round(tp / (tp + fp), 4),
        "recall": None if tp + fn == 0 else round(tp / (tp + fn), 4),
        "false_positive_rate": None if fp + tn == 0 else round(fp / (fp + tn), 4),
        "false_positive_rate_n": fp + tn,
        "hard_negative_n": hard_n,
        "hard_negative_false_positive_rate": None if hard_n == 0 else round(hard_fp / hard_n, 4),
        "positives": int(np.sum(labels == 1)),
    }


def threshold_for_budget(negatives: np.ndarray) -> tuple[float, bool, bool]:
    if len(negatives) == 0:
        return 1.0, False, False
    threshold = float(np.max(negatives)) + 1e-6
    return threshold, True, len(negatives) >= int(1.0 / BUDGET)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--labels", type=pathlib.Path, required=True)
    parser.add_argument("--model", default="google/embeddinggemma-2")
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--embedding-epochs", type=int, default=1)
    parser.add_argument("--classifier-epochs", type=int, default=10)
    parser.add_argument("--batch", type=int, default=8)
    args = parser.parse_args()

    import torch
    from datasets import Dataset
    from setfit import SetFitModel, Trainer, TrainingArguments

    rows = [json.loads(line) for line in args.labels.read_text(encoding="utf-8").splitlines() if line.strip()]
    calibration = calibration_set(rows)
    results = {}

    for element in ELEMENTS:
        subset = [row for row in rows if row["element"] == element]
        fit = [row for row in subset if row["split"] == "train" and text_key(row) not in calibration]
        calibration = [row for row in subset if row["split"] == "train" and text_key(row) in calibration]
        heldout = [row for row in subset if row["split"] == "heldout"]

        def dataset(subset_rows: list[dict]) -> Dataset:
            return Dataset.from_dict({
                "text": [row["text"] for row in subset_rows],
                "label": [int(row["label"]) for row in subset_rows],
            })

        print(f"{element}: fit {len(fit)} calibration {len(calibration)} heldout {len(heldout)}", flush=True)
        # Force float32 on the body. The checkpoint loads in bfloat16, SetFit's contrastive training
        # step runs in float32, and the backward pass fails with "Found dtype Float but expected
        # BFloat16" before the first step completes. model_kwargs={"torch_dtype": ...} is accepted
        # and ignored by setfit 1.2 (measured: the body still reported bfloat16), while body.float()
        # converts it and reports float32. The serving path keeps bfloat16; this arm is training only.
        model = SetFitModel.from_pretrained(args.model, labels=["benign", "element"])
        model.model_body = model.model_body.float()
        if model.model_body[0].auto_model.dtype != torch.float32:
            raise SystemExit("REFUSING: the SetFit body did not convert to float32")

        trainer = Trainer(
            model=model,
            train_dataset=dataset(fit),
            eval_dataset=dataset(calibration),
            # SetFit takes a pair here (embedding epochs, classifier epochs), not a scalar. A scalar
            # fails inside the library on num_epochs[0], measured on setfit 1.2.0.
            args=TrainingArguments(
                batch_size=args.batch,
                num_epochs=(args.embedding_epochs, args.classifier_epochs),
            ),
            column_mapping={"text": "text", "label": "label"},
        )
        trainer.train()

        calibration_probabilities = np.asarray(
            [model.predict_proba([row["text"]])[0][1] for row in calibration]
        )
        calibration_labels = np.asarray([row["label"] for row in calibration], dtype=int)
        threshold, budget_met, resolvable = threshold_for_budget(
            calibration_probabilities[calibration_labels == 0]
        )

        heldout_probabilities = np.asarray([model.predict_proba([row["text"]])[0][1] for row in heldout])
        heldout_labels = np.asarray([row["label"] for row in heldout], dtype=int)
        heldout_hard = np.asarray([1 if row.get("hard_negative") else 0 for row in heldout], dtype=int)
        metrics_block = metrics(heldout_labels, heldout_probabilities, threshold, heldout_hard)
        metrics_block["fpr_budget"] = BUDGET
        metrics_block["fpr_budget_met"] = budget_met
        metrics_block["fpr_budget_resolvable"] = resolvable
        metrics_block["scorer"] = f"setfit/{args.model.split('/')[-1]}"
        results[element] = metrics_block
        print(element, metrics_block, flush=True)

    args.output.write_text(json.dumps({
        "scorer": f"setfit/{args.model}",
        "elements": results,
        "note": "Same splits and threshold rule as train_elements.py. Threshold on calibration, held-out read once.",
    }, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
