"""Element scoring over the embedding space: a prototype kNN and an optional linear probe.

Two scorers, two questions, and they are deliberately not blended:

* **prototype_knn** answers "is this segment closer to the labelled threatening exemplars than to
  the labelled benign and hard-negative ones". The number is a signed margin, so it has a case-based
  reason attached: the identifier of the nearest threatening exemplar.
* **linear_probe** answers "what probability does a logistic model fitted on the same labelled set
  assign". It has no case, because a linear model has no exemplars.

Both read the same artefact, which carries its own vectors, model tag, dimension and prefix version.
An artefact from a different space is refused rather than scored: comparing across spaces is a
defect that produces plausible numbers.
"""

from __future__ import annotations

import json
import math
import pathlib

import numpy as np

VOCABULARY = (
    "credential_request",
    "urgency_pressure",
    "threat",
    "impersonated_greeting",
    "attachment_lure",
)
VOCABULARY_VERSION = "elements/1"
PROTOTYPE_KNN = "prototype_knn"
LINEAR_PROBE = "linear_probe"
SCORERS = (PROTOTYPE_KNN, LINEAR_PROBE)


class ArtefactError(RuntimeError):
    """The artefact cannot be used. ``reason`` is the closed wire name a caller reports."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(reason if not detail else f"{reason}: {detail}")
        self.reason = reason


class ElementClassifier:
    def __init__(self, path: pathlib.Path, model_tag: str, dimension: int, prefix_version: str) -> None:
        self._path = path
        self._model_tag = model_tag
        self._dimension = dimension
        self._prefix_version = prefix_version
        self._loaded = False
        self._artefact_version = "unknown"
        self._vocabulary_version = VOCABULARY_VERSION
        self._positive: dict[str, tuple[list[str], np.ndarray]] = {}
        self._negative: dict[str, tuple[list[str], np.ndarray]] = {}
        self._probe: dict[str, dict[str, object]] = {}
        self._scorer_versions = {
            PROTOTYPE_KNN: "prototype_knn/unknown",
            LINEAR_PROBE: "linear_probe/unknown",
        }

    @property
    def loaded(self) -> bool:
        return self._loaded

    @property
    def vocabulary_version(self) -> str:
        return self._vocabulary_version

    @property
    def artefact_version(self) -> str:
        return self._artefact_version

    def scorer_version(self, scorer: str) -> str:
        return self._scorer_versions.get(scorer, f"{scorer}/unknown")

    def describe(self) -> dict[str, object]:
        return {
            "artefact_version": self._artefact_version,
            "vocabulary_version": self._vocabulary_version,
            "scorers": [
                {"scorer": scorer, "scorer_version": self.scorer_version(scorer)}
                for scorer in SCORERS
            ],
        }

    def load(self) -> None:
        try:
            document = json.loads(self._path.read_text(encoding="utf-8"))
        except FileNotFoundError as exc:
            raise ArtefactError("artefact_missing", str(self._path)) from exc
        except json.JSONDecodeError as exc:
            raise ArtefactError("artefact_missing", "not valid json") from exc

        if document.get("vocabulary_version") != VOCABULARY_VERSION:
            raise ArtefactError(
                "contract_violation",
                f"vocabulary {document.get('vocabulary_version')!r} is not {VOCABULARY_VERSION!r}",
            )
        for field, expected in (
            ("model_tag", self._model_tag),
            ("dimension", self._dimension),
            ("prefix_version", self._prefix_version),
        ):
            if document.get(field) != expected:
                raise ArtefactError(
                    "model_tag_mismatch" if field == "model_tag" else
                    "dimension_mismatch" if field == "dimension" else "prefix_version_mismatch",
                    f"{field} {document.get(field)!r} is not {expected!r}",
                )

        self._artefact_version = str(document.get("artefact_version", "unknown"))
        self._scorer_versions[PROTOTYPE_KNN] = str(
            document.get("knn_version", f"prototype_knn/{self._artefact_version}")
        )
        self._scorer_versions[LINEAR_PROBE] = str(
            document.get("probe_version", f"linear_probe/{self._artefact_version}")
        )

        for element in VOCABULARY:
            for label, target in ((1, self._positive), (0, self._negative)):
                identifiers = []
                vectors = []
                for exemplar in document.get("exemplars", []):
                    if exemplar.get("element") != element or exemplar.get("label") != label:
                        continue
                    if exemplar.get("split") != "train":
                        continue
                    vector = exemplar.get("vector")
                    if not isinstance(vector, list) or len(vector) != self._dimension:
                        raise ArtefactError("dimension_mismatch", "exemplar vector width")
                    identifiers.append(str(exemplar.get("id")))
                    vectors.append(vector)
                if vectors:
                    matrix = np.asarray(vectors, dtype=np.float32)
                    norms = np.linalg.norm(matrix, axis=1, keepdims=True)
                    target[element] = (identifiers, matrix / np.where(norms == 0, 1.0, norms))

        probe = document.get("probe") or {}
        for element, model in (probe.get("models") or {}).items():
            if element not in VOCABULARY:
                continue
            weights = np.asarray(model.get("weights", []), dtype=np.float32)
            if weights.shape != (self._dimension,):
                raise ArtefactError("dimension_mismatch", f"probe weights for {element}")
            self._probe[element] = {
                "weights": weights,
                "bias": float(model.get("bias", 0.0)),
                "threshold": float(model.get("threshold", 0.5)),
            }

        self._loaded = True

    def classify(self, vectors: np.ndarray, scorers: tuple[str, ...]) -> list[dict[str, object]]:
        if not self._loaded:
            raise ArtefactError("artefact_missing", "classifier not loaded")

        results = []
        for scorer in scorers:
            if scorer not in SCORERS:
                raise ArtefactError("contract_violation", f"unknown scorer {scorer!r}")
            segments = []
            for ordinal, vector in enumerate(vectors):
                unit = vector / (np.linalg.norm(vector) or 1.0)
                elements = []
                for element in VOCABULARY:
                    point = (
                        self._score_knn(element, unit)
                        if scorer == PROTOTYPE_KNN
                        else self._score_probe(element, unit)
                    )
                    # The scorer is on the point, not only on its container. A stored point that has
                    # to be read through its parent to learn what the number means is a point whose
                    # provenance can be lost in a copy.
                    point["scorer"] = scorer
                    elements.append(point)
                segments.append({"ordinal": ordinal, "elements": elements})
            results.append(
                {
                    "scorer": scorer,
                    "scorer_version": self.scorer_version(scorer),
                    "segments": segments,
                }
            )
        return results

    def _score_knn(self, element: str, unit: np.ndarray) -> dict[str, object]:
        positive = self._positive.get(element)
        negative = self._negative.get(element)
        if positive is None or negative is None:
            return _absent(element, "no_exemplars")

        positive_similarities = positive[1] @ unit
        negative_similarities = negative[1] @ unit
        best = int(np.argmax(positive_similarities))
        score = float(positive_similarities[best] - float(np.max(negative_similarities)))
        if not math.isfinite(score):
            return _absent(element, "contract_violation")
        return {
            "element": element,
            "availability": "available",
            "score": round(score, 6),
            "nearest_exemplar_id": positive[0][best],
            "reason": None,
        }

    def _score_probe(self, element: str, unit: np.ndarray) -> dict[str, object]:
        model = self._probe.get(element)
        if model is None:
            return _absent(element, "probe_not_trained")
        logit = float(model["weights"] @ unit) + float(model["bias"])
        probability = 1.0 / (1.0 + math.exp(-max(min(logit, 60.0), -60.0)))
        return {
            "element": element,
            "availability": "available",
            "score": round(probability, 6),
            "nearest_exemplar_id": None,
            "reason": None,
        }


def _absent(element: str, reason: str) -> dict[str, object]:
    return {
        "element": element,
        "availability": "not_produced",
        "score": None,
        "nearest_exemplar_id": None,
        "reason": reason,
    }
