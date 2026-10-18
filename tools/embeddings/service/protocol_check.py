"""Protocol checks for the embedding service, run without a GPU or a model.

The encoder is stubbed, so what is exercised here is the HTTP contract, the auth rule, the
bounds, the vector validation and the promise that content does not reach a log. The model
itself is measured separately against the real service on the inference host.

Run:  python -m pytest tools/embeddings/service/protocol_check.py -q
"""

from __future__ import annotations

import logging
import math
import os
import pathlib
import stat

import json

import pytest
from fastapi.testclient import TestClient

from embed_app import create_app
from embed_classify import VOCABULARY
from embed_config import Config, ConfigError, SUPPORTED_DIMENSIONS
from embed_encoder import EncodeResult, VectorContractError

TOKEN = "0123456789abcdef0123456789abcdef"
SEGMENT = "Your account will be suspended unless you confirm your password today."


class StubEncoder:
    def __init__(self, dimension: int = 256, *, broken: str | None = None) -> None:
        self._dimension = dimension
        self._broken = broken
        self.loaded = True
        self.dtype_name = "bfloat16"

    def metadata(self) -> dict[str, object]:
        return {
            "model_tag": "google/embeddinggemma-2",
            "model_revision": "stub",
            "dimension": self._dimension,
            "native_dimension": 768,
            "prefix": "task: classification | query: ",
            "prefix_version": "classification/1",
            "dtype": self.dtype_name,
            "device": "stub",
            "normalized": True,
            "truncation_dimensions": list(SUPPORTED_DIMENSIONS),
        }

    def encode(self, segments: list[str]) -> EncodeResult:
        if self._broken == "non_finite":
            raise VectorContractError("non_finite_vector")
        if self._broken == "dimension":
            raise VectorContractError("dimension_mismatch")
        vectors = []
        for _segment in segments:
            raw = [1.0] * self._dimension
            norm = math.sqrt(self._dimension)
            vectors.append([value / norm for value in raw])
        return EncodeResult(vectors=vectors, truncated=[False] * len(segments), elapsed_ms=1.0)


def make_config(tmp_path: pathlib.Path, dimension: int = 256) -> Config:
    token_file = tmp_path / "token"
    token_file.write_text(TOKEN + "\n", encoding="utf-8")
    token_file.chmod(0o600)
    return Config(
        host="127.0.0.1",
        port=11440,
        token_path=token_file,
        model="google/embeddinggemma-2",
        dimension=dimension,
        prefix="task: classification | query: ",
        prefix_version="classification/1",
        max_segments=4,
        max_segment_chars=120,
        max_total_chars=400,
        max_tokens=8192,
        batch_size=8,
    )


def client(tmp_path: pathlib.Path, **kwargs) -> TestClient:
    dimension = kwargs.pop("dimension", 256)
    encoder = kwargs.pop("encoder", None) or StubEncoder(dimension)
    return TestClient(create_app(make_config(tmp_path, dimension), encoder))


def auth() -> dict[str, str]:
    return {"Authorization": f"Bearer {TOKEN}"}


def test_metadata_requires_a_token(tmp_path: pathlib.Path) -> None:
    response = client(tmp_path).get("/metadata")
    assert response.status_code == 401
    assert response.json()["reason"] == "unauthorized"


def test_metadata_rejects_a_wrong_token(tmp_path: pathlib.Path) -> None:
    response = client(tmp_path).get("/metadata", headers={"Authorization": "Bearer " + "f" * 32})
    assert response.status_code == 401


def test_metadata_reports_the_contract_fields(tmp_path: pathlib.Path) -> None:
    body = client(tmp_path).get("/metadata", headers=auth()).json()
    assert body["model_tag"] == "google/embeddinggemma-2"
    assert body["dimension"] == 256
    assert body["prefix_version"] == "classification/1"
    assert body["dtype"] == "bfloat16"
    assert body["normalized"] is True


def test_embed_returns_unit_vectors_at_the_configured_dimension(tmp_path: pathlib.Path) -> None:
    response = client(tmp_path).post("/embed", headers=auth(), json={"segments": [SEGMENT]})
    assert response.status_code == 200
    body = response.json()
    assert body["dimension"] == 256
    assert body["prefix_version"] == "classification/1"
    assert len(body["vectors"]) == 1
    vector = body["vectors"][0]
    assert len(vector) == 256
    assert abs(math.sqrt(sum(value * value for value in vector)) - 1.0) < 1e-5


def test_a_prefix_cannot_be_supplied_by_the_caller(tmp_path: pathlib.Path) -> None:
    response = client(tmp_path).post(
        "/embed",
        headers=auth(),
        json={"segments": [SEGMENT], "prefix": "task: search result | query: "},
    )
    assert response.status_code == 422


def test_empty_segment_list_is_rejected(tmp_path: pathlib.Path) -> None:
    response = client(tmp_path).post("/embed", headers=auth(), json={"segments": []})
    assert response.status_code == 422


def test_blank_segment_is_rejected(tmp_path: pathlib.Path) -> None:
    response = client(tmp_path).post("/embed", headers=auth(), json={"segments": ["   "]})
    assert response.status_code == 422
    assert response.json()["reason"] == "empty_segment"


def test_too_many_segments_is_rejected(tmp_path: pathlib.Path) -> None:
    response = client(tmp_path).post(
        "/embed", headers=auth(), json={"segments": ["x"] * 5}
    )
    assert response.status_code == 413
    assert response.json()["reason"] == "too_many_segments"


def test_oversized_segment_is_rejected(tmp_path: pathlib.Path) -> None:
    response = client(tmp_path).post(
        "/embed", headers=auth(), json={"segments": ["x" * 121]}
    )
    assert response.status_code == 413
    assert response.json()["reason"] == "segment_too_long"


def test_a_non_finite_vector_is_refused_rather_than_returned(tmp_path: pathlib.Path) -> None:
    broken = StubEncoder(256, broken="non_finite")
    response = client(tmp_path, encoder=broken).post(
        "/embed", headers=auth(), json={"segments": [SEGMENT]}
    )
    assert response.status_code == 500
    body = response.json()
    assert body["reason"] == "non_finite_vector"
    assert "vectors" not in body


def test_a_dimension_mismatch_is_refused_rather_than_returned(tmp_path: pathlib.Path) -> None:
    broken = StubEncoder(256, broken="dimension")
    response = client(tmp_path, encoder=broken).post(
        "/embed", headers=auth(), json={"segments": [SEGMENT]}
    )
    assert response.status_code == 500
    assert response.json()["reason"] == "dimension_mismatch"


class StubClassifier:
    """Enough of the classifier for the HTTP contract: one scorer, five elements, no model."""

    loaded = True

    def describe(self) -> dict[str, object]:
        return {
            "artefact_version": "seed-1",
            "vocabulary_version": "elements/2",
            "scorers": [{"scorer": "prototype_knn", "scorer_version": "prototype_knn/seed-1"}],
        }

    def classify(self, vectors, scorers):
        results = []
        for scorer in scorers:
            segments = [
                {
                    "ordinal": ordinal,
                    "elements": [
                        {
                            "element": element,
                            "availability": "available",
                            "score": 0.25,
                            "nearest_exemplar_id": f"seed-1:{element}:0001",
                            "reason": None,
                        }
                        for element in VOCABULARY
                    ],
                }
                for ordinal in range(len(vectors))
            ]
            results.append(
                {"scorer": scorer, "scorer_version": f"{scorer}/seed-1", "segments": segments}
            )
        return results


def test_classify_without_an_artefact_is_a_named_absence(tmp_path: pathlib.Path) -> None:
    response = client(tmp_path).post("/classify", headers=auth(), json={"segments": [SEGMENT]})
    assert response.status_code == 503
    assert response.json()["reason"] == "artefact_missing"


def test_classify_returns_one_point_per_element_per_segment(tmp_path: pathlib.Path) -> None:
    config = make_config(tmp_path)
    app = create_app(config, StubEncoder(256), StubClassifier())
    response = TestClient(app).post("/classify", headers=auth(), json={"segments": [SEGMENT]})
    assert response.status_code == 200
    body = response.json()
    assert body["vocabulary_version"] == "elements/2"
    assert body["dimension"] == 256
    points = body["scorers"][0]["segments"][0]["elements"]
    assert len(points) == len(VOCABULARY)
    assert all(point["availability"] == "available" for point in points)
    assert all(point["score"] == 0.25 for point in points)


def test_classify_refuses_an_unknown_scorer(tmp_path: pathlib.Path) -> None:
    config = make_config(tmp_path)
    app = create_app(config, StubEncoder(256), StubClassifier())
    response = TestClient(app).post(
        "/classify", headers=auth(), json={"segments": [SEGMENT], "scorers": ["magic"]}
    )
    assert response.status_code == 422
    assert response.json()["reason"] == "contract_violation"


def test_classify_requires_a_token(tmp_path: pathlib.Path) -> None:
    config = make_config(tmp_path)
    app = create_app(config, StubEncoder(256), StubClassifier())
    response = TestClient(app).post("/classify", json={"segments": [SEGMENT]})
    assert response.status_code == 401


def test_an_elements1_artefact_is_refused_under_elements2() -> None:
    from embed_classify import ArtefactError, ElementClassifier

    # The committed corpus-1 artefact is elements/1. Under elements/2 it must be refused rather than
    # served with two elements silently missing, which is the whole point of versioning the vocabulary.
    artefact = pathlib.Path(__file__).parent / "artefacts" / "elements-corpus-1.json"
    classifier = ElementClassifier(artefact, "google/embeddinggemma-2", 256, "classification/1")
    with pytest.raises(ArtefactError):
        classifier.load()


def test_an_unscorable_element_is_served_as_insufficient_exemplars(tmp_path: pathlib.Path) -> None:
    import numpy as np
    from embed_classify import ElementClassifier

    def vector(weight: float) -> list[float]:
        values = [weight] * 256
        norm = sum(value * value for value in values) ** 0.5
        return [value / norm for value in values]

    artefact = {
        "artefact_version": "test-1",
        "vocabulary_version": "elements/2",
        "model_tag": "google/embeddinggemma-2",
        "prefix_version": "classification/1",
        "dimension": 256,
        "knn_version": "prototype_knn/test-1",
        "probe_version": "linear_probe/test-1",
        "element_support": {
            "credential_request": {"train_positives": 12, "min_train_positives": 10, "scorable": True},
            "urgency_pressure": {"train_positives": 12, "min_train_positives": 10, "scorable": True},
            "threat": {"train_positives": 8, "min_train_positives": 10, "scorable": False},
        },
        "exemplars": [
            {"id": "ex:credential_request:1", "element": "credential_request", "label": 1,
             "split": "train", "vector": vector(1.0)},
            {"id": "ex:credential_request:2", "element": "credential_request", "label": 0,
             "split": "train", "vector": vector(-1.0)},
            {"id": "ex:urgency_pressure:1", "element": "urgency_pressure", "label": 1,
             "split": "train", "vector": vector(1.0)},
            {"id": "ex:urgency_pressure:2", "element": "urgency_pressure", "label": 0,
             "split": "train", "vector": vector(-1.0)},
            {"id": "ex:threat:1", "element": "threat", "label": 1, "split": "train", "vector": vector(1.0)},
        ],
    }
    path = tmp_path / "elements-test.json"
    path.write_text(json.dumps(artefact), encoding="utf-8")

    classifier = ElementClassifier(path, "google/embeddinggemma-2", 256, "classification/1")
    classifier.load()
    results = classifier.classify(np.asarray([np.ones(256, dtype=np.float32) / 16.0]), ("prototype_knn",))

    points = {point["element"]: point for point in results[0]["segments"][0]["elements"]}
    assert points["threat"]["availability"] == "not_produced"
    assert points["threat"]["reason"] == "insufficient_exemplars"
    assert points["threat"]["score"] is None
    assert points["threat"]["nearest_exemplar_id"] is None
    assert points["credential_request"]["availability"] == "available"


def test_health_is_unauthenticated_and_carries_no_content(tmp_path: pathlib.Path) -> None:
    response = client(tmp_path).get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ready"}


def test_content_never_reaches_a_log_line(tmp_path: pathlib.Path, caplog) -> None:
    caplog.set_level(logging.DEBUG)
    response = client(tmp_path).post("/embed", headers=auth(), json={"segments": [SEGMENT]})
    assert response.status_code == 200

    # The control first: the request did log something. Without this, an empty capture would
    # make the absence assertion below pass for the wrong reason.
    assert any("embed ok" in record.getMessage() for record in caplog.records)
    for record in caplog.records:
        assert "suspended" not in record.getMessage()


def test_a_group_readable_token_file_refuses_to_serve(tmp_path: pathlib.Path) -> None:
    config = make_config(tmp_path)
    config.token_path.chmod(stat.S_IRUSR | stat.S_IWUSR | stat.S_IRGRP)
    app = create_app(config, StubEncoder(256))
    response = TestClient(app).get("/metadata", headers=auth())
    assert response.status_code == 503
    assert response.json()["reason"] == "token_unavailable"


def test_a_non_loopback_bind_is_refused_at_configuration() -> None:
    with pytest.raises(ConfigError):
        Config.from_env({"EMB_HOST": "0.0.0.0"})


def test_an_unsupported_dimension_is_refused_at_configuration() -> None:
    with pytest.raises(ConfigError):
        Config.from_env({"EMB_DIM": "300"})


def test_defaults_match_the_configured_contract() -> None:
    config = Config.from_env({})
    assert config.dimension == 256
    assert config.prefix_version == "classification/1"
    assert config.prefix == "task: classification | query: "
    assert config.host == "127.0.0.1"


def test_token_must_not_be_short(tmp_path: pathlib.Path) -> None:
    config = make_config(tmp_path)
    config.token_path.write_text("short\n", encoding="utf-8")
    with pytest.raises(ConfigError):
        config.read_token()


if __name__ == "__main__":  # pragma: no cover
    os.execv("python", ["python", "-m", "pytest", __file__, "-q"])
