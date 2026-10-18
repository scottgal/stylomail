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

Built by `ingest_corpora.py` from the Nazario phishing corpus, the Apache SpamAssassin ham and spam
archives, then trained by `train_elements.py`. 1,300 rows: 60 positives per element (Nazario plus the
spam archive, every element whose cue fires may claim a segment) and 200 ham negatives per element,
of which 120 to 180 are hard negatives selected by subject. The train split is divided by a stable
text digest into fit and calibration, stratified per element and label; calibration chooses the
operating point and held-out is read once.

**What the labels mean.** A corpus message is malicious or benign by the corpus's own label; the cue
and topic rules only select which element a segment is evidence about. This is weak supervision, and
a held-out number here is a statement about the rule as much as about the model.

**One operating scorer per element**, chosen by a stated rule: lowest false-positive rate among
scorers whose recall is at least 0.85, ties to the higher PR-AUC. When no scorer clears the floor the
rule falls back to the highest recall and the artefact records `cleared_recall_floor: false`. Both
scorers remain in the artefact and in the response as a reference series; consumers read
`operating_scorers` from `/metadata` and must not present the two numbers as one score.

| element | operating | floor cleared | P | R | FPR | n neg | hard neg | PR-AUC |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| credential_request | linear_probe | yes | 0.96 | 0.93 | 0.01 | 70 | 65 | 0.99 |
| urgency_pressure | linear_probe | yes | 1.00 | 0.89 | 0.00 | 57 | 48 | 0.99 |
| threat | linear_probe | no | 0.80 | 0.57 | 0.03 | 65 | 58 | 0.79 |
| impersonated_greeting | prototype_knn | yes | 0.96 | 0.96 | 0.02 | 61 | 39 | 0.99 |
| attachment_lure | linear_probe | no | 0.89 | 0.84 | 0.04 | 51 | 46 | 0.90 |

Reference series at the same thresholds:

| element | kNN P | kNN R | kNN FPR | kNN PR-AUC | probe P | probe R | probe FPR | probe PR-AUC |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| credential_request | 0.97 | 1.00 | 0.01 | 0.98 | 0.96 | 0.93 | 0.01 | 0.99 |
| urgency_pressure | 0.78 | 0.95 | 0.09 | 0.98 | 1.00 | 0.89 | 0.00 | 0.99 |
| threat | 1.00 | 0.43 | 0.00 | 0.83 | 0.80 | 0.57 | 0.03 | 0.79 |
| impersonated_greeting | 0.96 | 0.96 | 0.02 | 0.99 | 0.86 | 1.00 | 0.07 | 1.00 |
| attachment_lure | 0.93 | 0.74 | 0.02 | 0.92 | 0.89 | 0.84 | 0.04 | 0.90 |

`threat` is the element the harder negative set hurt most: neither scorer reaches 0.85 recall, so
its operating scorer is the fallback arm. `attachment_lure` improved materially once its positives
grew from 23 to 60 with the spam archive (PR-AUC 0.82 to 0.92 for kNN) but still does not clear the
floor. The one percent budget remains unmet and unresolvable at these sample sizes.

**The one percent budget is NOT met, and it still cannot be resolved.** Held-out negatives number 51
to 70 per element, and hard negatives 39 to 65, but resolving a one percent rate needs at least 100
negatives, so every row is still marked `fpr_budget_resolvable: false`. Reaching 100 is possible from
the available corpora by lowering the hard-negative selection threshold; that would be data volume,
not evidence, so it is not done silently. Resolving a
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

`ToneVocabulary` (`goemotions/1`, 28 labels) and `ToneSeries` are on disk so the consumer can bind a
sibling seam. No tone model is loaded by the service and `/classify` does not emit tone. Tone stays a
separate series with its own `tone_model_tag` and never enters an element score.

Both candidates were measured on the A4000 with `training/benchmark_tone.py` over 20 corpus segments
and a 200 word probe (artifact `training/tone-metrics.json`):

| model | labels | load s | latency ms median | top >= 0.50 | active labels | mean top |
| --- | --- | --- | --- | --- | --- | --- |
| SamLowe/roberta-base-go_emotions | 28 | 1.53 | 6.3 (6.1 to 10.9) | 1.00 | 2 | 0.84 |
| j-hartmann/emotion-english-distilroberta-base | 7 | 10.65 | 5.4 (4.8 to 5.5) | 0.95 | 3 | 0.69 |

Chosen: `tone_model_tag = SamLowe/roberta-base-go_emotions` (`ToneModels.GoEmotions`). The latency
difference is under a millisecond and the vocabulary is four times wider; load time is seven times
shorter. The "active labels" column is the honest caveat: on this sample most segments are neutral or
gratitude, so a 28-label vocabulary does not mean 28 labels are exercised. Label coverage on real
traffic is a property of the corpus, not of the model, and only two to three labels appear here.

## Generated conversations

`generate_corpus.py` writes labelled conversations for the five element types and four hard-negative
types (provider security notice, transactional receipt, invoice reminder, urgent legitimate thread).
Each conversation is 2 to 5 turns, the element is confined to one turn, and labels are per
(conversation, turn). Diversity controls (topic, persona, formality, length, channel, obfuscation) are
sampled and recorded beside the seed, so any conversation can be regenerated exactly. Every labelled
turn is checked by a second model; disagreements are discarded and the pass rate is reported.

The model was chosen on measured numbers on the A4000, five generations each:

| model | JSON valid | label verified | latency median |
| --- | --- | --- | --- |
| gemma4:e4b | 5/5 | 2/5 (0.40) | 12,243 ms |
| gemma4:26b | 0/5 | 0/5 | 31,460 ms |
| qwen3.8:latest | 1/5 | 1/5 (0.20) | 134,542 ms |

Chosen: **gemma4:e4b**. It is the only candidate that reliably produced the required JSON, it is the
fastest, and it had the highest verified rate. qwen3.8 is excluded on this host: 17.7 GB on a 15.4 GB
card is spilling, and 134 s per conversation is not a teacher. gemma4:26b's five JSON failures are
being tested against Ollama structured outputs before the model is dismissed.

**The verification rate is the pilot's result, not an obstacle.** Three of five generations placed the
element where an independent verifier disagreed, so most generated rows will be discarded by design.
The number that matters is the verified yield per type, and only that number justifies scaling.

The first benchmark pass verified the last turn instead of the turn carrying `element_turn`. That pass
rate was withdrawn; the instrument now reads and range checks the index and verifies that turn.

**Operational gotcha, measured.** A generation can hang with an established socket to Ollama, no
model loaded and VRAM idle (763 MiB on a 15,352 MiB card). That is a stuck runner, not a slow model.
Restart Ollama on the host and re-run; the pilot then proceeds. The restart must bind all interfaces
(`OLLAMA_HOST=0.0.0.0:11434`) because WSL reaches the Windows service by its LAN address, not by
localhost.

## The operator dataset, as a generation-artifact probe

`ingest_operator_dataset.py` reads only `subject`, `email_body` and `label` from the operator CSV.
Every precomputed score in that file is ignored as label leakage. Provenance per row is
`kaggle:phishing_email_detection_dataset_v2`, licence recorded as operator-confirmed, generator
suspected. Text is redacted for structured identifiers and the derived JSONL is git-ignored; only
counts and metrics are committed.

50,000 rows survived ingestion with zero duplicate text digests, split 34,802 train / 15,198 held out.
`baseline_operator.py` fits word 1-2 gram TF-IDF with logistic regression on text alone:

| baseline | ROC-AUC | PR-AUC | precision | recall | FPR | Brier |
| --- | --- | --- | --- | --- | --- | --- |
| TF-IDF + logistic, text only | 0.9921 | 0.9936 | 1.00 | 0.9817 | 0.00 | 0.0075 |
| cue counting (9 words) | 0.7122 | 0.5906 | 1.00 | 0.0172 | 0.00 | 0.2454 |

**That near-perfect result is the finding, and it is about the generator.** A bag of words separates
the classes almost completely on body text, with phrases like "immediate action", "expires today" and
"verify your" carrying the separation. The dataset is therefore used for scale, benign volume and
teacher-labelling raw text, never as evidence of real-world detection quality and never as the
hard-negative set.

## Running the service on the host

The service is started by `tools/embeddings/tunnel.sh up`, which runs it inside an ssh session and
forwards the port. That is not a convenience: this host tears down processes started in a WSL session
once the last client disconnects, which was measured against `nohup`, a lingering `systemd --user`
unit, and a detached tmux server. The tunnel session is the supervisor, so "tunnel up" and "service
up" cannot disagree. A stale tunnel that answers 000 while the service is healthy means the tunnel,
not the service, needs restarting.
