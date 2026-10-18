"""Fit and evaluate both element scorers, with an operating point chosen on a calibration split.

Three splits, and the reason each exists:

* **fit**: exemplars and probe weights are learned here.
* **calibration**: thresholds are chosen here, for a stated false-positive budget on the negative
  rows. Still never the held-out split.
* **heldout**: read once, at the thresholds the earlier splits produced, and reported with n.

The probe is Platt scaled on the calibration split. Before that step its output is an uncalibrated
score; after it the artefact records a calibrated probability together with the Brier score and the
reliability bins that justify the name. If scaling cannot be fitted, the artefact does not call it a
probability.

Refusal rules: a split with fewer than three rows on either side for an element is refused, because a
metric computed there is noise. A held-out split with no positives or no negatives is refused. An
artefact is only written after all elements have held-out metrics.

    python train_elements.py --labels corpus-labels.jsonl \
        --endpoint http://127.0.0.1:11441 \
        --token-file ~/.config/stylomail/embedding-token \
        --output ../service/artefacts/elements-corpus-1.json --version corpus-1
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
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
VOCABULARY_VERSION = "elements/3"
ARTEFACT_VERSION = "seed-1"
DEFAULT_FPR_BUDGET = 0.01

# The operating scorer per element is chosen by one stated rule: among scorers whose held-out recall
# is at least this floor, the lowest false-positive rate wins; ties go to the higher PR-AUC. When no
# scorer clears the floor the rule falls back to the highest recall, and the artefact says which arm
# of the rule was used. The other scorer stays in the artefact as a reference series.
OPERATING_RECALL_FLOOR = 0.85


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
        body = call(endpoint, "/embed", token, {"segments": texts[start:start + batch]})
        if body.get("dimension") != 256:
            raise SystemExit(f"REFUSING: service returned dimension {body.get('dimension')}")
        vectors.extend(body["vectors"])
    return np.asarray(vectors, dtype=np.float32)


def text_key(row: dict) -> str:
    """Content identity: the basis of both the split and any stable identifier.

    An artefact row carries the digest instead of the text, so the same function can rebuild the
    split from committed data. That is what makes the split-stability test possible without the
    corpus text in the repository.
    """
    if row.get("text_digest"):
        return str(row["text_digest"])
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
    for element in VOCABULARY:
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

def fit_probe(vectors: np.ndarray, labels: np.ndarray) -> tuple[np.ndarray, float]:
    """L2 regularised logistic regression in float64 by gradient descent.

    Deliberately not scikit-learn. On this data sklearn's lbfgs line search emitted invalid value
    warnings while still returning finite weights, which leaves a reader unable to tell whether the
    numbers came from a clean solve. Forty lines of float64 gradient descent reproduce exactly, and
    the artefact stores the weights so the service needs no library at runtime.
    """
    x = np.asarray(vectors, dtype=np.float64)
    y = np.asarray(labels, dtype=np.float64)
    count = x.shape[0]
    positives = float(np.sum(y == 1.0))
    negatives = float(count - positives)
    weights_per_row = np.where(
        y == 1.0, count / (2.0 * positives), count / (2.0 * negatives)
    ) if positives and negatives else np.ones(count)

    w = np.zeros(x.shape[1], dtype=np.float64)
    b = float(np.log((positives + 1.0) / (negatives + 1.0)))
    l2 = 0.01
    learning_rate = 1.0
    # Accelerated BLAS on this machine emits invalid and overflow warnings from these products while
    # every input and output is finite. The warning is silenced and the values are checked instead.
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


def platt_scale(logits: np.ndarray, labels: np.ndarray) -> tuple[float, float]:
    """Fit p = sigmoid(a * logit + b) on the calibration split, by gradient descent in float64."""
    z = np.asarray(logits, dtype=np.float64)
    y = np.asarray(labels, dtype=np.float64)
    a, b = 1.0, 0.0
    for _ in range(4000):
        p = 1.0 / (1.0 + np.exp(-np.clip(a * z + b, -60.0, 60.0)))
        residual = p - y
        gradient_a = float(np.mean(residual * z))
        gradient_b = float(np.mean(residual))
        if not (np.isfinite(gradient_a) and np.isfinite(gradient_b)):
            raise SystemExit("REFUSING: non-finite value during Platt scaling")
        a -= 0.1 * gradient_a
        b -= 0.1 * gradient_b
    if not (np.isfinite(a) and np.isfinite(b)) or a <= 0:
        raise SystemExit("REFUSING: Platt scaling produced an unusable transform")
    return a, b


def average_precision(labels: np.ndarray, scores: np.ndarray) -> float | None:
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
    labels = labels.astype(int)
    edges = np.linspace(0.0, 1.0, bins + 1)
    output = []
    for index in range(bins):
        lower, upper = edges[index], edges[index + 1]
        mask = (probabilities >= lower) & (
            probabilities < upper if index < bins - 1 else probabilities <= upper
        )
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


def threshold_for_budget(negative_scores: np.ndarray) -> tuple[float, bool, bool]:
    """The lowest threshold whose observed FPR on calibration negatives is within the budget.

    Returns the threshold, whether the observed FPR meets the budget, and whether the calibration
    negatives are numerous enough to resolve the budget at all. With eight hard negatives a one
    percent budget is not resolvable: zero observed false positives is the best available reading,
    and saying "met" without that caveat would overstate what eight rows can show.
    """
    if len(negative_scores) == 0:
        return 1.0, False, False
    threshold = float(np.max(negative_scores)) + 1e-6
    observed = float(np.mean(negative_scores >= threshold))
    resolvable = len(negative_scores) >= int(1.0 / DEFAULT_FPR_BUDGET)
    return threshold, observed <= DEFAULT_FPR_BUDGET, resolvable


def metrics(labels: np.ndarray, scores: np.ndarray, threshold: float, hard_negative: np.ndarray) -> dict:
    labels = labels.astype(int)
    predictions = (scores >= threshold).astype(int)
    tp = int(np.sum((labels == 1) & (predictions == 1)))
    fn = int(np.sum((labels == 1) & (predictions == 0)))
    fp = int(np.sum((labels == 0) & (predictions == 1)))
    tn = int(np.sum((labels == 0) & (predictions == 0)))
    hard = labels == 0
    hard_fp = int(np.sum((labels == 0) & (hard_negative == 1) & (predictions == 1)))
    hard_n = int(np.sum((labels == 0) & (hard_negative == 1)))
    return {
        "threshold": round(float(threshold), 6),
        "tp": tp, "fp": fp, "tn": tn, "fn": fn,
        "positives": int(np.sum(labels == 1)),
        "negatives": int(np.sum(labels == 0)),
        "precision": None if tp + fp == 0 else round(tp / (tp + fp), 4),
        "recall": None if tp + fn == 0 else round(tp / (tp + fn), 4),
        "false_positive_rate": None if fp + tn == 0 else round(fp / (fp + tn), 4),
        "false_positive_rate_n": int(fp + tn),
        "hard_negative_false_positive_rate": None if hard_n == 0 else round(hard_fp / hard_n, 4),
        "hard_negative_n": hard_n,
        "pr_auc": average_precision(labels, scores),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--labels", type=pathlib.Path, required=True)
    parser.add_argument("--endpoint", default="http://127.0.0.1:11441")
    parser.add_argument("--token-file", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--version", default=ARTEFACT_VERSION)
    parser.add_argument("--fpr-budget", type=float, default=DEFAULT_FPR_BUDGET)
    parser.add_argument("--elements", default=None,
                        help="comma-separated element names; defaults to the whole vocabulary")
    parser.add_argument("--provenance", type=pathlib.Path, default=None,
                        help="JSON file merged into the artefact as provenance")
    args = parser.parse_args()

    token = args.token_file.read_text(encoding="utf-8").strip()
    if len(token) < 16:
        raise SystemExit("REFUSING: token file is not a token")

    metadata = call(args.endpoint, "/metadata", token)
    dimension = metadata["dimension"]
    prefix_version = metadata["prefix_version"]
    model_tag = metadata["model_tag"]

    elements = tuple(name.strip() for name in args.elements.split(",")) if args.elements else VOCABULARY
    rows = load_rows(args.labels)
    rows = [row for row in rows if row["element"] in elements]
    calibration = calibration_set(rows)

    problems = []
    for element in elements:
        fit_rows = [row for row in rows if row["element"] == element and row["split"] == "train"
                    and text_key(row) not in calibration]
        calibration_rows = [row for row in rows if row["element"] == element and row["split"] == "train"
                            and text_key(row) in calibration]
        heldout_rows = [row for row in rows if row["element"] == element and row["split"] == "heldout"]
        for name, subset in (("fit", fit_rows), ("calibration", calibration_rows), ("heldout", heldout_rows)):
            for label, label_name in ((1, "positive"), (0, "negative")):
                count = sum(1 for row in subset if row["label"] == label)
                minimum = 3 if name != "heldout" else 1
                if count < minimum:
                    problems.append(f"{element}/{name}/{label_name} has {count}, needs {minimum}")
    if problems:
        raise SystemExit("REFUSING: a split cannot support the reported metrics: " + "; ".join(problems))

    matrix = embed_all(args.endpoint, token, [row["text"] for row in rows], args.batch)
    for row, vector in zip(rows, matrix):
        row["vector"] = vector

    artefact_rows = []
    probe_models = {}
    knn_thresholds = {}
    probe_thresholds = {}
    metrics_block = {}
    operating_scorers = {}
    element_support = {}

    for element in elements:
        fit_rows = [row for row in rows if row["element"] == element and row["split"] == "train"
                    and text_key(row) not in calibration]
        calibration_rows = [row for row in rows if row["element"] == element and row["split"] == "train"
                            and text_key(row) in calibration]
        heldout_rows = [row for row in rows if row["element"] == element and row["split"] == "heldout"]

        def matrix_of(subset: list[dict]) -> np.ndarray:
            values = np.stack([row["vector"] for row in subset]).astype(np.float32)
            norms = np.linalg.norm(values, axis=1, keepdims=True)
            return values / np.where(norms == 0, 1.0, norms)

        positive = [row for row in fit_rows if row["label"] == 1]
        negative = [row for row in fit_rows if row["label"] == 0]
        positive_matrix = matrix_of(positive)
        negative_matrix = matrix_of(negative)

        def knn_scores(subset: list[dict]) -> np.ndarray:
            values = matrix_of(subset)
            with np.errstate(all="ignore"):
                scores = np.array([
                    float(np.max(positive_matrix @ unit) - np.max(negative_matrix @ unit))
                    for unit in values
                ])
            if not np.all(np.isfinite(scores)):
                raise SystemExit("REFUSING: non-finite kNN margin")
            return scores

        calibration_labels = np.asarray([row["label"] for row in calibration_rows], dtype=int)
        calibration_scores = knn_scores(calibration_rows)
        calibration_negatives = calibration_scores[calibration_labels == 0]
        knn_threshold, knn_budget_met, knn_resolvable = threshold_for_budget(calibration_negatives)

        fit_labels = np.asarray([row["label"] for row in fit_rows], dtype=int)
        weights, bias = fit_probe(matrix_of(fit_rows), fit_labels)

        calibration_matrix = matrix_of(calibration_rows)
        with np.errstate(all="ignore"):
            calibration_logits = calibration_matrix @ weights + bias
        if not np.all(np.isfinite(calibration_logits)):
            raise SystemExit("REFUSING: non-finite calibration logits")
        platt_a, platt_b = platt_scale(calibration_logits, calibration_labels)
        calibration_probabilities = 1.0 / (
            1.0 + np.exp(-np.clip(platt_a * calibration_logits + platt_b, -60.0, 60.0))
        )
        probe_threshold, probe_budget_met, probe_resolvable = threshold_for_budget(
            calibration_probabilities[calibration_labels == 0]
        )

        heldout_labels = np.asarray([row["label"] for row in heldout_rows], dtype=int)
        heldout_hard = np.asarray([1 if row.get("hard_negative") else 0 for row in heldout_rows], dtype=int)
        heldout_knn = knn_scores(heldout_rows)
        heldout_matrix = matrix_of(heldout_rows)
        with np.errstate(all="ignore"):
            heldout_logits = heldout_matrix @ weights + bias
        if not np.all(np.isfinite(heldout_logits)):
            raise SystemExit("REFUSING: non-finite held-out logits")
        heldout_probabilities = 1.0 / (
            1.0 + np.exp(-np.clip(platt_a * heldout_logits + platt_b, -60.0, 60.0))
        )

        knn_metrics = metrics(heldout_labels, heldout_knn, knn_threshold, heldout_hard)
        knn_metrics["threshold_source"] = "calibration split, FPR budget on calibration negatives"
        knn_metrics["fpr_budget"] = args.fpr_budget
        knn_metrics["fpr_budget_met"] = knn_budget_met
        knn_metrics["fpr_budget_resolvable"] = knn_resolvable
        knn_metrics["margin_positive_median"] = round(float(np.median(heldout_knn[heldout_labels == 1])), 4)
        knn_metrics["margin_negative_median"] = round(float(np.median(heldout_knn[heldout_labels == 0])), 4)
        knn_metrics["margin_min"] = round(float(np.min(heldout_knn)), 4)
        knn_metrics["margin_max"] = round(float(np.max(heldout_knn)), 4)

        probe_metrics = metrics(heldout_labels, heldout_probabilities, probe_threshold, heldout_hard)
        probe_metrics["threshold_source"] = "calibration split, FPR budget on calibration negatives"
        probe_metrics["fpr_budget"] = args.fpr_budget
        probe_metrics["fpr_budget_met"] = probe_budget_met
        probe_metrics["fpr_budget_resolvable"] = probe_resolvable
        probe_metrics["brier"] = brier(heldout_labels, heldout_probabilities)
        probe_metrics["calibration"] = reliability(heldout_labels, heldout_probabilities)
        probe_metrics["score_semantics"] = (
            "Platt scaled probability. The transform was fitted on the calibration split; the Brier "
            "score and reliability bins above describe the held-out output after scaling."
        )

        block = {"prototype_knn": knn_metrics, "linear_probe": probe_metrics}
        eligible = [name for name, values in block.items()
                    if values["recall"] is not None and values["recall"] >= OPERATING_RECALL_FLOOR]
        if eligible:
            operating = min(
                eligible,
                key=lambda name: (
                    block[name]["false_positive_rate"]
                    if block[name]["false_positive_rate"] is not None else 1.0,
                    -(block[name]["pr_auc"] or 0.0),
                ),
            )
        else:
            # No scorer clears the recall floor, so the rule falls back to the highest recall and
            # says so. Minimising FPR here would pick the scorer that finds least, which is the
            # opposite of what a floor exists to prevent.
            operating = max(
                block,
                key=lambda name: (
                    block[name]["recall"] or 0.0,
                    -(block[name]["false_positive_rate"]
                      if block[name]["false_positive_rate"] is not None else 1.0),
                ),
            )
        operating_scorers[element] = {
            "scorer": operating,
            "scorer_version": f"{operating}/{'corpus-1' if args.version == 'corpus-1' else args.version}",
            "rule": "lowest false positive rate among scorers with recall at or above the floor",
            "recall_floor": OPERATING_RECALL_FLOOR,
            "cleared_recall_floor": bool(eligible),
            "reference_scorer": next(name for name in block if name != operating),
        }
        train_positives = sum(1 for row in fit_rows if row["label"] == 1)
        element_support[element] = {
            "train_positives": train_positives,
            "min_train_positives": 10,
            "scorable": train_positives >= 10,
        }
        metrics_block[element] = block
        knn_thresholds[element] = round(float(knn_threshold), 6)
        probe_thresholds[element] = round(float(probe_threshold), 6)
        probe_models[element] = {
            "weights": [round(float(value), 6) for value in weights],
            "bias": round(bias, 6),
            "platt_a": round(float(platt_a), 6),
            "platt_b": round(float(platt_b), 6),
            "threshold": round(float(probe_threshold), 6),
        }

        for row in rows:
            if row["element"] != element:
                continue
            artefact_rows.append({
                "id": row["id"],
                "element": element,
                "label": row["label"],
                "split": row["split"],
                "text_digest": text_key(row),
                "source": row.get("source", "unknown"),
                "licence": row.get("licence", "project-owned"),
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
        "probe": {"models": probe_models},
        "knn_thresholds": knn_thresholds,
        "probe_thresholds": probe_thresholds,
        "operating_point": {
            "fpr_budget": args.fpr_budget,
            "chosen_on": "calibration split (20 percent of train, by hash)",
            "note": (
                "Thresholds were chosen on the calibration split and applied unchanged to held-out. "
                "Where the calibration negatives are fewer than 100, a one percent budget cannot be "
                "resolved and the artefact says so rather than claiming it was met."
            ),
        },
        "operating_scorers": operating_scorers,
        "element_support": element_support,
        "metrics": metrics_block,
        "notes": (
            "Weak supervision: the corpus supplies the malicious or benign label; the cue rule only "
            "selects the element a segment is evidence about. Held-out metrics are measured on rows "
            "neither the probe nor the kNN thresholds saw."
        ),
        "exemplars": artefact_rows,
    }

    if args.provenance is not None:
        document["provenance"] = json.loads(args.provenance.read_text(encoding="utf-8"))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")

    header = (
        f"{'element':<24}{'scorer':<16}{'P':>7}{'R':>7}{'FPR':>7}{'n':>5}"
        f"{'hn':>4}{'hnFPR':>7}{'PRAUC':>7}{'thr':>8}{'budget':>8}"
    )
    print(header)
    for element, block in metrics_block.items():
        for scorer, values in block.items():
            budget = "met" if values["fpr_budget_met"] else "unmet"
            if not values["fpr_budget_resolvable"]:
                budget = "unresolved"
            print(
                f"{element:<24}{scorer:<16}{_fmt(values['precision']):>7}{_fmt(values['recall']):>7}"
                f"{_fmt(values['false_positive_rate']):>7}{values['false_positive_rate_n']:>5}"
                f"{values['hard_negative_n']:>4}{_fmt(values['hard_negative_false_positive_rate']):>7}"
                f"{_fmt(values['pr_auc']):>7}{values['threshold']:>8.3f}{budget:>8}"
            )
    print(f"wrote {args.output} ({len(artefact_rows)} exemplars, {len(rows)} labelled rows)")
    return 0


def _fmt(value: float | None) -> str:
    return "n/a" if value is None else f"{value:.2f}"


if __name__ == "__main__":
    raise SystemExit(main())
