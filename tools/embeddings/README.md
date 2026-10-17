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
  embed_classify.py    prototype kNN and optional linear probe over the same vectors
  artefacts/           the versioned prototype artefact (vectors and ids, no text)
  measure.sh           latency and dimension, measured over HTTP on the host
  capture-contract.sh  save real responses, replayed by the provider's contract tests
  provision.sh         provision or verify ~/emb2 on the inference host, one writer, explicit reset
  deploy.sh            copy the service to the host
  run.sh               start the service, one instance, loopback only
  make-token.sh        create the bearer token file (0600, value never printed)
  requirements.txt     libraries; torch comes from the CUDA wheel index separately
  window-launcher.ps1  Windows-side supervisor, because WSL kills host daemons when idle
training/
  seed-labels.jsonl    labelled seed segments with provenance and split
  train_elements.py    embeds, fits the probe, evaluates both scorers, writes the artefact
tunnel.sh              start the service and forward its port; the tunnel is the supervisor
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

## Element classification

`POST /classify` scores each bounded segment against the five elements in `elements/1` and returns,
for each requested scorer, one point per (segment ordinal, element). The client in
`src/StyloMail.Embeddings/` is the source of truth for the shape; `tools/embeddings/PROVIDER.md`
describes the interface a caller binds.

Two scorers, deliberately not blended:

* `prototype_knn` scores a signed margin: the cosine similarity of the nearest TRAIN threatening
  exemplar minus that of the nearest TRAIN benign or hard-negative exemplar, with the threatening
  exemplar's opaque identifier returned so every score has a case-based reason.
* `linear_probe` scores a logistic probability in [0, 1], trained on the same artefact by
  `training/train_elements.py` and stored in the artefact as weights and a bias, so the service
  needs no machine-learning library at runtime.

A point with no measurement is `not_produced` with a reason, never a zero: a zero margin is a real
point (the two nearest exemplars are equally close) and would be indistinguishable from "not scored"
if absence were drawn the same way.

### The seed artefact, and what it is not

`service/artefacts/elements-seed-1.json` is built from `training/seed-labels.jsonl`: 100
**authored** segments, 10 positive and 10 hard-negative per element, 60 used for training and 40
held out. It is deliberately labelled `source: authored-seed` and `licence: project-owned`. This is
not the Nazario, SpamAssassin, CEAS or Enron material the next iteration calls for: those corpora
have not been ingested, vetted or PII-stripped here, and writing their names beside authored rows
would be provenance theatre.

### Held-out metrics: corpus/1, weak supervision

Built by `ingest_corpora.py` from the Nazario phishing corpus and the Apache SpamAssassin ham
corpora, then trained by `train_elements.py`. 563 rows. The train split is split again by a stable
hash into fit (80 percent) and calibration (20 percent); the calibration split chooses the operating
point, and the held-out split is read once at that point.

**What the labels mean.** A Nazario message is malicious by the corpus's own label; the segment
chosen for an element is the one carrying that element's cues. A SpamAssassin ham message is benign;
a cue-bearing ham segment is a hard negative, and the rest are easy negatives. The corpus supplies
the class, the cue rule only selects which element a segment is evidence about. This is weak
supervision, and a held-out number here is a statement about the rule as much as about the model.

**Operating point and its budget.** Thresholds are chosen on the calibration split for a **one
percent false-positive budget on negatives**, and applied unchanged to held-out.

| element | scorer | P | R | FPR | n neg | hard neg | hard FPR | PR-AUC | threshold |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| credential_request | kNN | 1.00 | 0.91 | 0.00 | 22 | 4 | 0.00 | 0.98 | 0.008 |
| credential_request | probe | 1.00 | 0.77 | 0.00 | 22 | 4 | 0.00 | 1.00 | 0.682 |
| urgency_pressure | kNN | 1.00 | 0.85 | 0.00 | 15 | 12 | 0.00 | 0.99 | 0.029 |
| urgency_pressure | probe | 0.95 | 0.95 | 0.07 | 15 | 12 | 0.08 | 1.00 | 0.360 |
| threat | kNN | 0.94 | 0.89 | 0.04 | 28 | 27 | 0.00 | 0.99 | 0.014 |
| threat | probe | 0.94 | 0.89 | 0.04 | 28 | 27 | 0.00 | 0.99 | 0.607 |
| impersonated_greeting | kNN | 1.00 | 1.00 | 0.00 | 21 | 2 | 0.00 | 1.00 | 0.019 |
| impersonated_greeting | probe | 1.00 | 1.00 | 0.00 | 21 | 2 | 0.00 | 1.00 | 0.219 |
| attachment_lure | kNN | 0.83 | 0.62 | 0.07 | 14 | 12 | 0.08 | 0.91 | 0.023 |
| attachment_lure | probe | 0.75 | 0.75 | 0.14 | 14 | 12 | 0.08 | 0.90 | 0.475 |

The split is stratified by element and label and keyed by the TEXT digest, not the row id. Hashing
the id was wrong and was caught by renaming exemplar ids: the rename moved every row between fit and
calibration and the metrics moved with it. A flat text hash then left an element with 23 positives
holding only 2 in calibration, which the trainer refused; the stratified version fixes both.

**The one percent budget is NOT met, and at these sample sizes it cannot even be resolved.** The
calibration negatives number 15 to 28 per element, and the held-out hard negatives 2 to 27. Resolving a
one percent rate needs at least 100 negatives, so every row in this artefact is marked
`fpr_budget_resolvable: false`, and observed held-out FPRs reach 0.28 on all negatives and 1.00 on
the four hard negatives available for credential_request. The artefact says this rather than quoting
the budget as satisfied.

**kNN margins are ordered but the classes overlap.** On held-out, PR-AUC is 0.91 to 1.00 and the
budget-chosen thresholds are 0.008 to 0.029, which is why recall is high. kNN can keep its own
threshold; what it does not have yet is a negative set large enough to justify a tight operating
point. `attachment_lure` is the weak element (kNN precision 0.83 and recall 0.62, probe 0.75 and
0.75) and is reported as weak rather than averaged away.

**Exemplar identifiers are opaque and carry no version.** They are `ex:<element>:<text digest>`, so
the same text keeps the same id across retrains and renaming an artefact never makes an id read as a
provenance claim. Provenance is carried by `scorer_version` on every block and by the source and
licence columns of the training rows. The earlier `seed-1:` and `nazario:` prefixes were removed for
exactly that reason.

**The probe is now Platt scaled, and the name matches the evidence.** The transform is fitted on the
calibration split and stored in the artefact (`platt_a`, `platt_b`) so the served number and the
measured number are the same one. Held-out Brier after scaling is 0.02 to 0.10 (previously 0.10 to
0.15), and the reliability bins are consistent at the ends: the low bin predicts 0.002 to 0.044 and
observes 0.00, the high bin predicts 0.95 to 0.99 and observes 0.83 to 1.00. The middle bins are
sparse, with two or three rows, so the calibration claim is about the ends and says so.

Between rows and across tiles, the older 0.50-threshold table is superseded by this one; its numbers
were measured at a fixed 0.50 on an uncalibrated score and are not comparable with these.

### Tone

`ToneVocabulary` (`goemotions/1`, 28 labels) and `ToneSeries` are on disk in the C# project so the
consumer can bind a sibling seam. No tone model is loaded and `/classify` does not emit tone. The
model choice between `SamLowe/roberta-base-go_emotions` and `j-hartmann/...` is unmeasured here.

## Running the service on the host

The service is started by `tools/embeddings/tunnel.sh up`, which runs it inside an ssh session and
forwards the port. That is not a convenience: this host tears down processes started in a WSL session
once the last client disconnects, which was measured against `nohup`, a lingering `systemd --user`
unit, and a detached tmux server. The tunnel session is the supervisor, so "tunnel up" and "service
up" cannot disagree. A stale tunnel that answers 000 while the service is healthy means the tunnel,
not the service, needs restarting.
