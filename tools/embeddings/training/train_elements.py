"""Train the linear probe and evaluate both scorers on held-out labelled segments.

This script is the only thing allowed to write a promotable artefact. It refuses when the held-out
split cannot support the claim: a metric computed on the training rows, or on a held-out split with
no positives or no negatives, is not a measurement and is not written.

    python train_elements.py \
        --labels seed-labels.jsonl \
        --endpoint http://127.0.0.1:11441 \
        --token-file ~/.config/stylomail/embedding-token \
        --output ../service/artefacts/elements-seed-1.json

The endpoint is the service through the tunnel: the artefact must carry vectors from the same model,
dimension and prefix version the service will score with, and computing them anywhere else is how a
space mismatch gets in.
"""

from __future__ import annotations

import argparse
import datetime
import json
import math
import pathlib
import urllib.error
import urllib.request

import numpy as np

VOCABULARY = (
    "credential_request",
    "urgency_pressure",
    "threat",
    "impersonated_greeting",
    "attachment_lure",
)
VOCABULARY_VERSION = "elements/1"
ARTEFACT_VERSION = "seed-1"


def call(endpoint: str, path: str, token: str, payload: dict | None = None) -> dict:
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(endpoint.rstrip("/") + path, data=data)
    request.add_header("Authorization", "Bearer " + token)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as error:
        raise SystemExit(f"REFUSING: {path} answered {error.code}: {error.read()[:200]!r}")


def load_rows(path: pathlib.Path) -> list[dict]:
    rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    for row in rows:
        if row["element"] not in VOCABULARY:
            raise SystemExit(f"REFUSING: row {row.get('id')} names unknown element {row.get('element')!r}")
        if row["label"] not in (0, 1):
            raise SystemExit(f"REFUSING: row {row.get('id')} has label {row.get('label')!r}")
        if row["split"] not in ("train", "heldout"):
            raise SystemExit(f"REFUSING: row {row.get('id')} has split {row.get('split')!r}")
    return rows


def embed_all(endpoint: str, token: str, texts: list[str], batch: int = 16) -> np.ndarray:
    vectors = []
    for start in range(0, len(texts), batch):
        chunk = texts[start:start + batch]
        body = call(endpoint, "/embed", token, {"segments": chunk})
        if body.get("dimension") != 256:
            raise SystemExit(f"REFUSING: service returned dimension {body.get('dimension')}")
        vectors.extend(body["vectors"])
    return np.asarray(vectors, dtype=np.float32)


def average_precision(labels: np.ndarray, scores: np.ndarray) -> float | None:
    """Area under the precision-recall curve, computed by the step rule (no interpolation)."""
    labels = labels.astype(int)
    if labels.sum() == 0:
        return None
    order = np.argsort(-scores, kind="mergesort")
    ranked = labels[order]
    tp = np.cumsum(ranked)
    precision = tp / np.arange(1, len(ranked) + 1)
    return round(float(np.sum(precision[ranked == 1]) / labels.sum()), 4)


def brier(labels: np.ndarray, probabilities: np.ndarray) -> float | None:
    if len(labels) == 0:
        return None
    return round(float(np.mean((probabilities - labels.astype(float)) ** 2)), 4)


def reliability(labels: np.ndarray, probabilities: np.ndarray, bins: int = 5) -> list[dict]:
    """Observed frequency against mean predicted probability, per bin."""
    labels = labels.astype(int)
    edges = np.linspace(0.0, 1.0, bins + 1)
    output = []
    for index in range(bins):
        lower, upper = edges[index], edges[index + 1]
        mask = (probabilities >= lower) & (probabilities < upper if index < bins - 1 else probabilities <= upper)
        if not mask.any():
            output.append({"lower": round(float(lower), 2), "upper": round(float(upper), 2), "n": 0})
            continue
        output.append({
            "lower": round(float(lower), 2),
            "upper": round(float(upper), 2),
            "n": int(mask.sum()),
            "mean_predicted": round(float(probabilities[mask].mean()), 4),
            "observed": round(float(labels[mask].mean()), 4),
        })
    return output


def metrics(labels: np.ndarray, scores: np.ndarray, threshold: float) -> dict:
    labels = labels.astype(int)
    predictions = (scores >= threshold).astype(int)
    tp = int(np.sum((labels == 1) & (predictions == 1)))
    fn = int(np.sum((labels == 1) & (predictions == 0)))
    fp = int(np.sum((labels == 0) & (predictions == 1)))
    tn = int(np.sum((labels == 0) & (predictions == 0)))
    precision = tp / (tp + fp) if tp + fp else None
    recall = tp / (tp + fn) if tp + fn else None
    fpr = fp / (fp + tn) if fp + tn else None
    return {
        "threshold": round(float(threshold), 6),
        "tp": tp, "fp": fp, "tn": tn, "fn": fn,
        "positives": int(np.sum(labels == 1)),
        "negatives": int(np.sum(labels == 0)),
        "precision": None if precision is None else round(precision, 4),
        "recall": None if recall is None else round(recall, 4),
        "false_positive_rate": None if fpr is None else round(fpr, 4),
        "pr_auc": average_precision(labels, scores),
        "brier": brier(labels, scores) if 0.0 <= float(np.min(scores)) and float(np.max(scores)) <= 1.0 else None,
    }


def best_threshold(labels: np.ndarray, scores: np.ndarray) -> float:
    """The threshold that maximises F1 on the TRAIN split, chosen there and never on held-out."""
    labels = labels.astype(int)
    candidates = np.unique(scores)
    best, best_f1 = 0.0, -1.0
    for candidate in candidates:
        predictions = (scores >= candidate).astype(int)
        tp = int(np.sum((labels == 1) & (predictions == 1)))
        fp = int(np.sum((labels == 0) & (predictions == 1)))
        fn = int(np.sum((labels == 1) & (predictions == 0)))
        if tp == 0:
            continue
        precision = tp / (tp + fp) if tp + fp else 0.0
        recall = tp / (tp + fn) if tp + fn else 0.0
        f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
        if f1 > best_f1:
            best, best_f1 = float(candidate), f1
    return best


def fit_probe(vectors: np.ndarray, labels: np.ndarray, dimension: int) -> tuple[np.ndarray, float]:
    """L2 regularised logistic regression, fitted in float64 by plain gradient descent.

    Deliberately not scikit-learn. On this data sklearn's lbfgs line search emitted invalid value
    warnings and still returned finite weights, which leaves a reader unable to tell whether the
    numbers came from a clean solve. Forty lines of float64 gradient descent are auditable and
    reproduce exactly, and the artefact stores the weights so the service needs no library at all.
    """
    x = np.asarray(vectors, dtype=np.float64)
    y = np.asarray(labels, dtype=np.float64)
    count = x.shape[0]

    # Balance the classes so ten positives are not drowned by a majority of negatives.
    positives = float(np.sum(y == 1.0))
    negatives = float(count - positives)
    weights_per_row = np.where(y == 1.0, count / (2.0 * positives), count / (2.0 * negatives)) if positives and negatives else np.ones(count)

    w = np.zeros(dimension, dtype=np.float64)
    b = float(np.log((positives + 1.0) / (negatives + 1.0)))
    l2 = 0.01
    learning_rate = 1.0
    # The BLAS on this machine emits divide-by-zero, overflow and invalid-value warnings from these
    # matmuls while every input and every output is finite (measured on a 12 by 256 matrix of unit
    # vectors). The warnings are an artifact of the accelerated library rather than of the data, so
    # they are silenced here and replaced with an explicit finiteness check on the values that
    # matter: a real NaN still refuses the artefact below.
    with np.errstate(all="ignore"):
        for _ in range(4000):
            logits = x @ w + b
            probabilities = 1.0 / (1.0 + np.exp(-np.clip(logits, -60.0, 60.0)))
            residual = (probabilities - y) * weights_per_row / count
            gradient_w = x.T @ residual + l2 * w
            gradient_b = float(np.sum(residual))
            if not (np.all(np.isfinite(logits)) and np.all(np.isfinite(gradient_w))
                    and np.isfinite(gradient_b)):
                raise SystemExit("REFUSING: non-finite value during the probe fit")
            w -= learning_rate * gradient_w
            b -= learning_rate * gradient_b

    if not np.all(np.isfinite(w)) or not np.isfinite(b):
        raise SystemExit("REFUSING: the probe did not converge to finite weights")
    return w.astype(np.float32), b


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--labels", type=pathlib.Path, required=True)
    parser.add_argument("--endpoint", default="http://127.0.0.1:11441")
    parser.add_argument("--token-file", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--threshold", type=float, default=0.5)
    parser.add_argument("--version", default=ARTEFACT_VERSION)
    args = parser.parse_args()

    token = args.token_file.read_text(encoding="utf-8").strip()
    if len(token) < 16:
        raise SystemExit("REFUSING: token file is not a token")

    metadata = call(args.endpoint, "/metadata", token)
    dimension = metadata["dimension"]
    prefix_version = metadata["prefix_version"]
    model_tag = metadata["model_tag"]

    rows = load_rows(args.labels)

    # Refuse a split that cannot support the number it would print.
    problems = []
    for element in VOCABULARY:
        for split in ("train", "heldout"):
            for label, name in ((1, "positive"), (0, "negative")):
                count = sum(1 for row in rows
                            if row["element"] == element and row["split"] == split and row["label"] == label)
                if count < (2 if split == "train" else 1):
                    problems.append(f"{element}/{split}/{name} has {count}")
    if problems:
        raise SystemExit("REFUSING: the split cannot support held-out metrics: " + "; ".join(problems))

    texts = [row["text"] for row in rows]
    matrix = embed_all(args.endpoint, token, texts, args.batch)
    for row, vector in zip(rows, matrix):
        row["vector"] = vector

    artefact_rows = []
    probe_models = {}
    knn_thresholds = {}
    metrics_block = {}

    for element in VOCABULARY:
        train = [row for row in rows if row["element"] == element and row["split"] == "train"]
        heldout = [row for row in rows if row["element"] == element and row["split"] == "heldout"]
        positive = [row for row in train if row["label"] == 1]
        negative = [row for row in train if row["label"] == 0]
        if not positive or not negative:
            raise SystemExit(f"REFUSING: {element} has no training exemplars on one side")

        positive_matrix = np.stack([row["vector"] for row in positive]).astype(np.float32)
        negative_matrix = np.stack([row["vector"] for row in negative]).astype(np.float32)
        positive_matrix /= np.linalg.norm(positive_matrix, axis=1, keepdims=True)
        negative_matrix /= np.linalg.norm(negative_matrix, axis=1, keepdims=True)

        labels = np.asarray([row["label"] for row in heldout], dtype=int)
        train_labels = np.asarray([row["label"] for row in train], dtype=int)

        def knn_scores(rows: list[dict]) -> np.ndarray:
            scores = []
            for row in rows:
                unit = row["vector"] / (np.linalg.norm(row["vector"]) or 1.0)
                scores.append(float(np.max(positive_matrix @ unit) - np.max(negative_matrix @ unit)))
            return np.asarray(scores)

        # The kNN threshold is chosen on the TRAIN split and then applied unchanged to held-out.
        # Choosing it on held-out would be a number about the choice, not about the classifier.
        knn_threshold = best_threshold(train_labels, knn_scores(train))
        heldout_knn = knn_scores(heldout)

        train_matrix = np.stack([row["vector"] for row in train])
        weights, bias = fit_probe(train_matrix, train_labels, dimension)
        heldout_matrix = np.stack([row["vector"] for row in heldout])
        # Same BLAS artifact as in the fit: warnings on finite inputs. Silence the warning and check
        # the values, so a real non-finite logit still refuses the artefact.
        with np.errstate(all="ignore"):
            logits = heldout_matrix @ weights + bias
        if not np.all(np.isfinite(logits)):
            raise SystemExit("REFUSING: non-finite logits on the held-out split")
        probabilities = 1.0 / (1.0 + np.exp(-np.clip(logits, -60.0, 60.0)))

        knn_metrics = metrics(labels, heldout_knn, knn_threshold)
        knn_metrics["threshold_source"] = "max F1 on the train split"
        knn_metrics["margin_positive_median"] = round(float(np.median(heldout_knn[labels == 1])), 4)
        knn_metrics["margin_negative_median"] = round(float(np.median(heldout_knn[labels == 0])), 4)
        knn_metrics["margin_min"] = round(float(np.min(heldout_knn)), 4)
        knn_metrics["margin_max"] = round(float(np.max(heldout_knn)), 4)

        probe_metrics = metrics(labels, probabilities, args.threshold)
        probe_metrics["threshold_source"] = "fixed at the artefact threshold"
        probe_metrics["calibration"] = reliability(labels, probabilities)

        metrics_block[element] = {"prototype_knn": knn_metrics, "linear_probe": probe_metrics}
        probe_models[element] = {
            "weights": [round(float(value), 6) for value in weights],
            "bias": round(bias, 6),
            "threshold": args.threshold,
        }
        knn_thresholds[element] = round(float(knn_threshold), 6)

        for row in rows:
            if row["element"] != element:
                continue
            artefact_rows.append({
                "id": row["id"],
                "element": element,
                "label": row["label"],
                "split": row["split"],
                "source": row["source"],
                "licence": row["licence"],
                "vector": [round(float(value), 6) for value in row["vector"]],
            })

    document = {
        "artefact_version": args.version,
        "vocabulary_version": VOCABULARY_VERSION,
        "model_tag": model_tag,
        "prefix_version": prefix_version,
        "dimension": dimension,
        "created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "knn_version": f"prototype_knn/{args.version}",
        "probe_version": f"linear_probe/{args.version}",
        "seed_ids": [row["id"] for row in rows if row["split"] == "train"],
        "heldout_ids": [row["id"] for row in rows if row["split"] == "heldout"],
        "probe": {"models": probe_models, "threshold": args.threshold},
        "knn_thresholds": knn_thresholds,
        "metrics": metrics_block,
        "notes": (
            "Authored seed labels, not corpus-derived. Text is in training/seed-labels.jsonl; this "
            "artefact stores vectors and identifiers only. Held-out metrics are measured on rows the "
            "probe and the kNN never saw."
        ),
        "exemplars": artefact_rows,
    }

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")

    print(f"{'element':<24}{'scorer':<16}{'P':>7}{'R':>7}{'FPR':>7}{'PR-AUC':>8}{'thr':>8}{'pos':>5}{'neg':>5}")
    for element, block in metrics_block.items():
        for scorer, values in block.items():
            print(
                f"{element:<24}{scorer:<16}"
                f"{_fmt(values['precision']):>7}{_fmt(values['recall']):>7}{_fmt(values['false_positive_rate']):>7}"
                f"{_fmt(values['pr_auc']):>8}{values['threshold']:>8.3f}"
                f"{values['positives']:>5}{values['negatives']:>5}"
            )
    print(f"wrote {args.output} ({len(artefact_rows)} exemplars, {len(rows)} labelled rows)")
    return 0


def _fmt(value: float | None) -> str:
    return "n/a" if value is None else f"{value:.2f}"


if __name__ == "__main__":
    raise SystemExit(main())

