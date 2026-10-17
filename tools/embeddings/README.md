# tools/embeddings

The local text embedding service and the provider that talks to it. This is the geometric half of
the element-level analysis in spec 9.10: an embedding gives one vector per bounded segment, so a
segment can be compared against a prototype set. It cannot say why, and it never decides anything.

## Layout

```
service/
  embed_config.py      environment configuration, loopback refusal, token file rules
  embed_encoder.py     model load, dtype selection, prefix, truncate-then-renormalise, vector checks
  embed_app.py         FastAPI surface: /health, /metadata, /embed
  protocol_check.py    HTTP contract tests with a stub encoder (no GPU, no model)
  measure.sh           latency and dimension, measured over HTTP on the host
  provision.sh         provision or verify ~/emb2 on the inference host, one writer, explicit reset
  deploy.sh            copy the service to the host
  run.sh               start the service, one instance, loopback only
  make-token.sh        create the bearer token file (0600, value never printed)
  requirements.txt     libraries; torch comes from the CUDA wheel index separately
PROVIDER.md            the interface access- and proxyapi- consume
```

The C# side is `src/StyloMail.Embeddings/` (provider, options, contracts, breaker) with
`tests/StyloMail.Embeddings.Tests/`.

## The three card constraints, and where each is enforced

| Constraint | Where |
| --- | --- |
| Never float16: activations exceed its range and it returns NaN or silently degraded vectors | `embed_encoder.load()` picks bfloat16 when the GPU reports support, float32 otherwise, and refuses any other dtype. The provider separately refuses a service that reports `float16`. |
| One task prefix, applied identically to segments and prototypes | Applied server side in `embed_encoder.encode()`. Callers send segments; the request model forbids extra fields, so a prefix cannot be supplied. The version travels in metadata and the provider refuses a different one. |
| Truncate, then renormalise | `encode(truncate_dim=...)` then an explicit L2 normalisation, then a norm check that refuses anything outside 1e-5. The provider re-checks the norm at 1e-4 before a vector reaches a caller. |

## Host and model

Inference host: `192.168.0.15`, Windows with WSL2 Ubuntu, RTX A4000 15 GB, key based SSH as `scott`.
The service runs inside WSL, not on Windows. Environment `~/emb2` is provisioned by
`service/provision.sh`.

Model `google/embeddinggemma-2`: 740M total, 270M text backbone with vision and audio encoders
disabled at load, 768 native dimensions, Matryoshka truncation to 512/256/128, 8192 token context,
Apache 2.0.

## Start, measure, deploy

On the host, in WSL:

```bash
bash ~/stylomail-embed/make-token.sh                 # once
nohup bash ~/stylomail-embed/run.sh >> ~/emb2-service.log 2>&1 &
bash ~/stylomail-embed/measure.sh
```

From the repo, on the machine that holds the checkout:

```bash
bash tools/embeddings/service/deploy.sh
```

The service binds `127.0.0.1:11440` only. It refuses any other `EMB_HOST`, so a configuration mistake
cannot expose message content on a LAN interface.

## HTTP contract

`GET /health` is unauthenticated and returns `{"status":"ready"}`.

`GET /metadata` and `POST /embed` require `Authorization: Bearer <token>` read from
`EMB_TOKEN_FILE` (default `~/.config/stylomail/embedding-token`, mode 0600; a group or other
readable file is refused). A wrong or missing token is 401 and is not distinguished further.

```json
POST /embed
{"segments": ["a bounded segment", "..."]}

200
{
  "request_id": "...",
  "model_tag": "google/embeddinggemma-2",
  "dimension": 256,
  "prefix_version": "classification/1",
  "dtype": "bfloat16",
  "normalized": true,
  "vectors": [[...]],
  "truncated": [false],
  "encode_ms": 0.0
}
```

Bounds: at most 32 segments, 6000 characters per segment, 32000 characters per batch, 8192 tokens
per segment (truncated by the tokenizer, and reported in `truncated`). Exceeding a bound is a named
413 or 422, never a silent trim.

Failures are named, never a vector: `unauthorized`, `token_unavailable`, `empty_segment`,
`segment_too_long`, `too_many_segments`, `batch_too_large`, `non_finite_vector`,
`dimension_mismatch`, `not_normalised`, `model_not_loaded`.

Content never reaches a log line. What is logged per request is the request id, segment count, total
characters, how many segments were truncated, encode and request latency, the dimension and the
dtype.

## Measurements

Measured on the A4000 through the HTTP surface with `service/measure.sh`, which is the instrument of
record. A figure without its arm is not a result.

Measured 17 Oct 2026 on the inference host, RTX A4000 15 GB, torch 2.6.0+cu124, bfloat16, through
`service/measure.sh` against the running service. The probe segment is 200 words / 1,264 characters.
`encode_ms` is the server's own figure around the model call; `wall_ms` includes HTTP and auth.

| What | Value | Arm |
| --- | --- | --- |
| Exact dimension returned | **256** in 10 of 10 runs | `measure.sh`, `/embed` response |
| Encode latency, 200 word segment | **median 25.1 ms**, min 22.8, max 30.0 | `measure.sh`, 10 runs after 1 warm call |
| Wall latency over HTTP | median 27.6 ms, min 25.3, max 32.8 | same run |
| Norm after truncation and renormalisation | **1.0** (min and max, 10 runs) | same run |
| Repeatability | max absolute component drift **0.0** over 10 runs | same run |
| Unauthorised request | 401, reason `unauthorized` | same run |
| Model load, weights cached | 4.73 s | `smoke-model.sh` |
| Native 768 dimensions | shape [768], latency 24.6 to 40.6 ms | `smoke-model.sh`, 5 runs |
| 256 dimensions | shape [256], latency 23.0 to 24.4 ms | same |

Two findings from those runs that change the code rather than decorate it:

1. **The checkpoint declares a nonsense `max_seq_length`** (about 1e36). The encoder sets
   `max_seq_length = min(EMB_MAX_TOKENS, 8192)` explicitly, so the bound the service reports is the
   bound it applies. Without that line the tokenizer would not truncate at the model's real context.
2. **The library's own `normalize_embeddings=True` does not land on 1.0 in bfloat16.** Measured norms
   were 0.99868 at 768 and 0.99942 at 256. The service therefore normalises explicitly in float64
   after truncation and asserts the result, which is why the served vectors measure 1.0 exactly.
   A caller that trusted the library's normalisation would be comparing vectors off the unit sphere
   by about 0.1 percent, which is exactly the plausible-but-wrong class the card warns about.

## Failures this service is built to make visible

1. **A truncated vector that was not renormalised** ranks plausibly and wrongly. Both sides check the
   norm, so this cannot pass silently.
2. **A comparison across two spaces** (different model, dimension or prefix version) is a defect, not
   drift. The provider refuses a service whose metadata disagrees with configuration.
3. **An outage drawn as a zero vector** would poison a centroid. Absence is `NotProduced` with a
   reason, and there is no code path that returns zeroes.
4. **A float16 fallback** would produce NaN or a silent quality loss. Both the encoder and the
   provider refuse it by name.
