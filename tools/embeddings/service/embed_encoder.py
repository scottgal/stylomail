"""The model side of the embedding service: load once, encode under a lock, apply the prefix here.

Three properties in this file are safety properties rather than tuning, and each is asserted
rather than trusted:

1. **Never float16.** The model card is explicit that activations exceed float16's range, and
   the failure mode is NaN or silently degraded vectors rather than an exception. The dtype is
   bfloat16 on a GPU that reports support for it, float32 everywhere else, and any other dtype
   is a refusal to start.
2. **The prefix is applied here.** Callers send segments, not prompts. Mixing a prefixed and an
   unprefixed vector in one comparison produces plausible wrong scores with no error, so the
   prefix is not a caller's parameter.
3. **Truncate first, then renormalise.** Slicing a unit vector does not preserve its length, and
   a shortened vector that is not renormalised ranks plausibly and wrongly. The encoder therefore
   truncates through the model's own Matryoshka path, then normalises explicitly, then verifies
   the norm and the finiteness of every value before returning anything.
"""

from __future__ import annotations

import math
import threading
import time
from dataclasses import dataclass

from embed_config import Config


class EncoderUnavailable(RuntimeError):
    """The model could not be loaded. The name is the reason a caller reports."""


class VectorContractError(RuntimeError):
    """The model produced something that is not a usable vector, so it is not returned."""

    def __init__(self, reason: str, detail: str = "") -> None:
        super().__init__(reason if not detail else f"{reason}: {detail}")
        self.reason = reason


@dataclass(frozen=True)
class EncodeResult:
    vectors: list[list[float]]
    truncated: list[bool]
    elapsed_ms: float


class EmbeddingEncoder:
    def __init__(self, config: Config) -> None:
        self._config = config
        self._model = None
        self._torch = None
        self._dtype_name = "unknown"
        self._revision: str | None = None
        self._device = "unknown"
        self._lock = threading.Lock()
        self._max_tokens_applied = 0

    @property
    def loaded(self) -> bool:
        return self._model is not None

    @property
    def dtype_name(self) -> str:
        return self._dtype_name

    @property
    def device(self) -> str:
        return self._device

    @property
    def revision(self) -> str | None:
        return self._revision

    @property
    def max_tokens_applied(self) -> int:
        return self._max_tokens_applied

    def load(self) -> None:
        """Load the model. Raises EncoderUnavailable with a named reason on failure."""
        try:
            import torch
            from sentence_transformers import SentenceTransformer
        except Exception as exc:  # noqa: BLE001 - the reason is reported, not swallowed
            raise EncoderUnavailable(f"import_failed:{type(exc).__name__}") from exc

        cuda = bool(torch.cuda.is_available())
        if cuda and torch.cuda.is_bf16_supported():
            dtype = torch.bfloat16
            self._dtype_name = "bfloat16"
        else:
            dtype = torch.float32
            self._dtype_name = "float32"

        # Belt and braces: the branch above cannot produce float16, and a later edit must not
        # be able to either without failing loudly here.
        if dtype is torch.float16:
            raise EncoderUnavailable("float16_refused")

        try:
            model = SentenceTransformer(
                self._config.model,
                model_kwargs={"torch_dtype": dtype},
                config_kwargs={"vision_config": None, "audio_config": None},
                device="cuda" if cuda else "cpu",
            )
        except Exception as exc:  # noqa: BLE001
            raise EncoderUnavailable(f"model_load_failed:{type(exc).__name__}") from exc

        model.eval()
        # sentence-transformers truncates at model.max_seq_length, which defaults to whatever
        # the checkpoint declares. Setting it explicitly makes the bound this service reports
        # the bound it applies, rather than a number read off a card.
        model.max_seq_length = min(self._config.max_tokens, 8192)
        self._max_tokens_applied = int(model.max_seq_length)

        card = getattr(model, "config", None)
        self._revision = getattr(card, "_commit_hash", None) or getattr(
            getattr(model, "tokenizer", None), "init_kwargs", {}
        ).get("_commit_hash")

        self._torch = torch
        self._model = model
        self._device = str(getattr(model, "device", "unknown"))

    def metadata(self) -> dict[str, object]:
        return {
            "model_tag": self._config.model,
            "model_revision": self._revision,
            "dimension": self._config.dimension,
            "native_dimension": self._config.native_dimension,
            "prefix": self._config.prefix,
            "prefix_version": self._config.prefix_version,
            "dtype": self._dtype_name,
            "device": self._device,
            "normalized": True,
            "truncation_dimensions": [768, 512, 256, 128],
            "max_tokens": self._max_tokens_applied or self._config.max_tokens,
            "max_segments": self._config.max_segments,
            "max_segment_chars": self._config.max_segment_chars,
            "max_total_chars": self._config.max_total_chars,
            "contract": "embeddinggemma-2/text-only/v1",
        }

    def encode(self, segments: list[str]) -> EncodeResult:
        if self._model is None or self._torch is None:
            raise EncoderUnavailable("model_not_loaded")
        if not segments:
            raise VectorContractError("empty_batch")

        prefixed = [self._config.prefix + segment for segment in segments]
        tokenizer = self._model.tokenizer
        truncated: list[bool] = []
        for text in prefixed:
            ids = tokenizer(text, add_special_tokens=True)["input_ids"]
            truncated.append(len(ids) > self._max_tokens_applied)

        started = time.perf_counter()
        # One GPU, one model: concurrent encodes contend for the same memory and interleave
        # badly rather than in parallel, so they are serialised here instead of on the device.
        with self._lock, self._torch.inference_mode():
            matrix = self._model.encode(
                prefixed,
                batch_size=self._config.batch_size,
                truncate_dim=self._config.dimension,
                normalize_embeddings=False,
                convert_to_numpy=True,
                show_progress_bar=False,
            )
        elapsed_ms = (time.perf_counter() - started) * 1000.0

        rows = matrix.tolist()
        if len(rows) != len(segments):
            raise VectorContractError("batch_count_mismatch", f"{len(rows)} != {len(segments)}")

        vectors: list[list[float]] = []
        for row in rows:
            if len(row) != self._config.dimension:
                raise VectorContractError(
                    "dimension_mismatch", f"{len(row)} != {self._config.dimension}"
                )
            norm = math.sqrt(sum(value * value for value in row))
            if not math.isfinite(norm) or norm <= 0.0:
                raise VectorContractError("non_finite_vector", "a component is not finite")
            unit = [value / norm for value in row]
            check = math.sqrt(sum(value * value for value in unit))
            if not math.isfinite(check) or abs(check - 1.0) > 1e-5:
                raise VectorContractError("not_normalised", f"norm {check:.8f}")
            vectors.append(unit)

        return EncodeResult(vectors=vectors, truncated=truncated, elapsed_ms=elapsed_ms)
