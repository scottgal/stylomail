"""A deliberately plain text-only baseline on the operator dataset.

The question is not "how good is this model". It is "does body text alone separate this set", because
a near-perfect answer from a bag of words on synthetic mail is evidence of generation artifacts, not
of detection quality. So this runs two baselines:

* word 1-2 gram TF-IDF with logistic regression, text only;
* a cue-count baseline with a handful of words a template generator would repeat.

Both are reported with n. If the TF-IDF AUC is near 1.0, the finding is recorded as an artifact
signal, and the dataset is not used to claim real-world quality.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re

import numpy as np
from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import average_precision_score, brier_score_loss, roc_auc_score

CUES = ("click", "verify", "password", "urgent", "account", "confirm", "suspended", "invoice", "attached")


def metrics(labels: np.ndarray, probabilities: np.ndarray) -> dict:
    predictions = (probabilities >= 0.5).astype(int)
    tp = int(np.sum((labels == 1) & (predictions == 1)))
    fn = int(np.sum((labels == 1) & (predictions == 0)))
    fp = int(np.sum((labels == 0) & (predictions == 1)))
    tn = int(np.sum((labels == 0) & (predictions == 0)))
    return {
        "roc_auc": round(float(roc_auc_score(labels, probabilities)), 4),
        "pr_auc": round(float(average_precision_score(labels, probabilities)), 4),
        "brier": round(float(brier_score_loss(labels, probabilities)), 4),
        "precision": round(tp / (tp + fp), 4) if tp + fp else None,
        "recall": round(tp / (tp + fn), 4) if tp + fn else None,
        "false_positive_rate": round(fp / (fp + tn), 4) if fp + tn else None,
        "positives": int(np.sum(labels == 1)),
        "negatives": int(np.sum(labels == 0)),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--labels", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()

    rows = [json.loads(line) for line in args.labels.read_text(encoding="utf-8").splitlines() if line.strip()]
    train = [row for row in rows if row["split"] == "train"]
    heldout = [row for row in rows if row["split"] == "heldout"]
    y_train = np.asarray([row["label"] for row in train])
    y_held = np.asarray([row["label"] for row in heldout])
    if len(set(y_held.tolist())) != 2:
        raise SystemExit("REFUSING: held-out split lacks one class")

    vectorizer = TfidfVectorizer(ngram_range=(1, 2), min_df=2, max_features=60000, sublinear_tf=True)
    x_train = vectorizer.fit_transform(row["text"] for row in train)
    x_held = vectorizer.transform(row["text"] for row in heldout)
    model = LogisticRegression(C=1.0, max_iter=2000, class_weight="balanced")
    model.fit(x_train, y_train)
    probabilities = model.predict_proba(x_held)[:, 1]

    names = np.asarray(vectorizer.get_feature_names_out())
    coefficients = model.coef_.reshape(-1)
    top_phishing = names[np.argsort(coefficients)[-15:][::-1]].tolist()
    top_legitimate = names[np.argsort(coefficients)[:15]].tolist()

    def cue_score(text: str) -> float:
        lowered = text.lower()
        return sum(1 for cue in CUES if cue in lowered) / len(CUES)

    cue_probabilities = np.asarray([cue_score(row["text"]) for row in heldout])

    result = {
        "rows": {"train": len(train), "heldout": len(heldout)},
        "tfidf_logistic": metrics(y_held, probabilities),
        "cue_count_baseline": metrics(y_held, cue_probabilities),
        "top_phishing_terms": top_phishing,
        "top_legitimate_terms": top_legitimate,
        "interpretation": (
            "A near-perfect text-only result on this dataset is an artifact signal. The generator's "
            "templates are separable by bag of words, so this measures the generator, not phishing "
            "detection. It is not comparable with the corpus-based element numbers."
        ),
        "provenance": "operator-provided dataset, licence unknown, generator suspected",
    }
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
