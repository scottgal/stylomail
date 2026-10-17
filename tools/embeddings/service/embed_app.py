"""HTTP surface of the local embedding service.

Contract, in one place:

* ``GET /health`` is unauthenticated and says only whether the model is loaded. It carries no
  model state and no content, so a supervisor can poll it without a credential.
* ``GET /metadata`` and ``POST /embed`` require ``Authorization: Bearer <token>`` where the
  token comes from the configured file. A missing, malformed or wrong token is 401 and is
  never distinguished from another wrong token.
* ``POST /embed`` takes a bounded list of segments and returns one unit-length vector per
  segment at the configured dimension, with the model tag, prefix version and dtype that
  produced them.
* Content never reaches a log line. What is logged is a request id, the segment count, the
  total character count, how many segments were truncated, the latency and the status.
"""

from __future__ import annotations

import hmac
import logging
import sys
import time
import uuid
from typing import Any, Optional

import uvicorn
from fastapi import FastAPI, Header, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from starlette.concurrency import run_in_threadpool

from embed_config import Config, ConfigError
from embed_encoder import EmbeddingEncoder, EncoderUnavailable, VectorContractError

MAX_BODY_BYTES = 1_048_576

_log = logging.getLogger("stylomail.embed")


class EmbedRequest(BaseModel):
    """Only segments are accepted. ``extra="forbid"`` is the reason the prefix cannot be a field."""

    model_config = ConfigDict(extra="forbid")

    segments: list[str] = Field(min_length=1)


def create_app(config: Config, encoder: EmbeddingEncoder) -> FastAPI:
    app = FastAPI(title="stylomail-embeddings", version="1", docs_url=None, redoc_url=None)

    @app.middleware("http")
    async def bound_request(request: Request, call_next):
        declared = request.headers.get("content-length")
        if declared is not None:
            try:
                if int(declared) > MAX_BODY_BYTES:
                    return _failure(413, "request_body_too_large")
            except ValueError:
                return _failure(400, "invalid_content_length")
        return await call_next(request)

    def authorize(authorization: str | None) -> JSONResponse | None:
        if not authorization or not authorization.lower().startswith("bearer "):
            return _unauthorized()
        provided = authorization[7:].strip().encode("utf-8")
        try:
            expected = config.read_token()
        except ConfigError as exc:
            # A missing or over-permissive token file is a deployment fault, not a bad request.
            _log.error("token_unavailable reason=%s", exc.__class__.__name__)
            return _failure(503, "token_unavailable")
        if not hmac.compare_digest(provided, expected):
            return _unauthorized()
        return None

    @app.get("/health")
    async def health() -> JSONResponse:
        if encoder.loaded:
            return JSONResponse({"status": "ready"})
        return JSONResponse({"status": "loading", "reason": "model_not_loaded"}, status_code=503)

    @app.get("/metadata")
    async def metadata(authorization: Optional[str] = Header(default=None)) -> Any:
        denied = authorize(authorization)
        if denied is not None:
            return denied
        payload = encoder.metadata()
        payload["request_id"] = uuid.uuid4().hex
        return JSONResponse(payload)

    @app.post("/embed")
    async def embed(
        body: EmbedRequest, authorization: Optional[str] = Header(default=None)
    ) -> Any:
        denied = authorize(authorization)
        if denied is not None:
            return denied

        request_id = uuid.uuid4().hex
        segments = body.segments
        chars = sum(len(segment) for segment in segments)

        if len(segments) > config.max_segments:
            return _failure(413, "too_many_segments", request_id, limit=config.max_segments)
        if chars > config.max_total_chars:
            return _failure(413, "batch_too_large", request_id, limit=config.max_total_chars)
        for index, segment in enumerate(segments):
            if not segment.strip():
                return _failure(422, "empty_segment", request_id, index=index)
            if len(segment) > config.max_segment_chars:
                return _failure(
                    413, "segment_too_long", request_id, index=index, limit=config.max_segment_chars
                )

        started = time.perf_counter()
        try:
            result = await run_in_threadpool(encoder.encode, segments)
        except EncoderUnavailable as exc:
            _log.warning(
                "embed unavailable request_id=%s segments=%d chars=%d reason=%s",
                request_id, len(segments), chars, exc,
            )
            return _failure(503, str(exc), request_id)
        except VectorContractError as exc:
            _log.error(
                "embed contract_violation request_id=%s segments=%d chars=%d reason=%s",
                request_id, len(segments), chars, exc.reason,
            )
            return _failure(500, exc.reason, request_id)

        total_ms = (time.perf_counter() - started) * 1000.0
        _log.info(
            "embed ok request_id=%s segments=%d chars=%d truncated=%d "
            "encode_ms=%.1f request_ms=%.1f dimension=%d dtype=%s",
            request_id, len(segments), chars, sum(1 for t in result.truncated if t),
            result.elapsed_ms, total_ms, config.dimension, encoder.dtype_name,
        )

        return JSONResponse(
            {
                "request_id": request_id,
                "model_tag": config.model,
                "dimension": config.dimension,
                "prefix_version": config.prefix_version,
                "dtype": encoder.dtype_name,
                "normalized": True,
                "vectors": result.vectors,
                "truncated": result.truncated,
                "encode_ms": round(result.elapsed_ms, 3),
            }
        )

    return app


def _unauthorized() -> JSONResponse:
    return JSONResponse(
        {"reason": "unauthorized"}, status_code=401, headers={"WWW-Authenticate": "Bearer"}
    )


def _failure(status: int, reason: str, request_id: str | None = None, **extra: Any) -> JSONResponse:
    payload: dict[str, Any] = {"reason": reason}
    if request_id is not None:
        payload["request_id"] = request_id
    payload.update(extra)
    return JSONResponse(payload, status_code=status)


def main() -> int:
    logging.basicConfig(
        stream=sys.stderr,
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )
    try:
        config = Config.from_env()
        config.read_token()  # fail now, not on the first caller
    except ConfigError as exc:
        print(f"REFUSING: {exc}", file=sys.stderr)
        return 2

    encoder = EmbeddingEncoder(config)
    try:
        encoder.load()
    except EncoderUnavailable as exc:
        print(f"REFUSING: model load failed: {exc}", file=sys.stderr)
        return 3

    _log.info(
        "start bind=%s:%d model=%s dimension=%d dtype=%s device=%s prefix_version=%s "
        "max_tokens=%d max_segments=%d",
        config.host, config.port, config.model, config.dimension, encoder.dtype_name,
        encoder.device, config.prefix_version, encoder.max_tokens_applied, config.max_segments,
    )

    app = create_app(config, encoder)
    uvicorn.run(app, host=config.host, port=config.port, access_log=False, log_config=None)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
