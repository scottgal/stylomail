# Laya as a local Jev: a spike, and what it found

Status: **spike, run 2026-09-25, not integrated and not committed.** The operator pointed at
[rupeshs/laya-openvino](https://github.com/rupeshs/laya-openvino) and asked whether it could run
locally. It can, and the reason is more interesting than "it runs".

## What Laya is

An OpenVINO-backed derivative of Laya, a **decision model** by Convai Innovations. It takes a state and
a set of typed questions and returns typed answers. The OpenVINO fork converts a checkpoint to an
Intermediate Representation once, then runs the forward pass on OpenVINO rather than torch.

Only the English checkpoint (`convaiinnovations/laya`, ModernBERT-large) has been exported. The
multilingual and typed-decision checkpoints have not.

## The finding: the schema is already Jev's

This is the part that matters. Laya's question schema, read from `laya/presets.py`:

```python
"is_phishing": {"type": "noul", "instructions": "...", "criteria": {"true": "phishing, scam, or fraud", "false": "a legitimate email"}}
"is_urgent":   {"type": "noul", "instructions": "Does `message` communicate time pressure or a deadline?"}
"frustration": {"type": "score", "instructions": "...", "criteria": ["calm and neutral", "concerned but civil", ...]}
"intent":      {"type": "choice", "instructions": "...", "criteria": {"refund": "...", "technical_help": "..."}}
```

Spec §6 records StyloMail's verified provider contract as `noul` (yes/no to a probability), `choice`
(one of a set) and `score` (a position on ordered levels). **They are the same three types with the
same names and the same fields.**

So this is not a model that needs adapting to Jev's shape. **It is a local implementation of the
interface StyloMail already speaks**, which is exactly what the spec anticipated:

> Cloud Jev is optional, configured behind a provider interface that **can later take a compatible
> local classifier**. A local Jev release is not assumed.

The package also ships presets for `is_phishing`, `is_spam`, `urgency`, `sensitive_data`,
`prompt_injection` and `jailbreak`, so a security question set exists before we write one.

## What upstream Laya is, and it is not a small thing

From [laya.convaiinnovations.com](https://laya.convaiinnovations.com/): a non-autoregressive decision
model family that "does not generate text, and gives lightning-fast probability predictions over
structured schemas". Its three primitives are **choice**, **score** and **noul**, described in the same
terms as spec §6.

Two statements on that site matter to us.

**It is positioned against Jev.** Their comparison table lists Jev as a "Closed proprietary API" and
Laya as "100% open-source **Apache 2.0** weights", "$0.00 (self-hosted)" against "$0.042 (metered
API)". So this is not an incidental schema resemblance: **it is an open-source implementation of the
interface our adapter calls, published as an alternative to it.** That is a strategic fact about our
semantic layer, not a detail of one library.

**The context window is a real constraint for us.** The English checkpoint is ModernBERT-large at
**512 tokens**; the multilingual one is mmBERT at **1024, extendable to 8k**; the typed-decisions one
is 1024. **My probe reported `input_tokens: 541`, which is already over the English checkpoint's
512**, so that run was very likely truncated without saying so. Email bodies routinely exceed 512
tokens, so the checkpoint choice is a design decision rather than a detail.

### Other caveats from the vendor, which we would inherit

- **~0.35 zero-shot**, described as near random, until fine-tuned.
- **Accuracy degrades above 20 choice options** (0.425 on a 77-label set).
- **Temperature calibration is needed** for the probabilities to mean anything: a fitted scalar
  reportedly cuts expected calibration error from 0.466 to **0.081**. Nothing in the spike calibrated
  anything, so the numbers above are raw model outputs.
- Latency claims are **32.8 ms P50 single and 72.3 ms for ten batched**, which is 7.2 ms per question,
  on hardware they do not name. My 104 ms per dimension is on CPU-only Apple Silicon, so the two are
  not contradictory, but ours is the one a local deployment would get on a Mac.

**The typed-decisions checkpoint is the interesting one for us**: 0.766 accuracy over 2,000 decisions,
with security alerts named as a use area. It has not been exported to OpenVINO, so the fork cannot run
it yet.

## What was measured

Five of StyloMail's own dimensions, expressed in Laya's schema, asked over the corpus's
credential-request message (`tests/fixtures/jev/credential-request.eml`):

| Dimension | `noul` |
| --- | --- |
| `credential_request` | 0.9996 |
| `link_lure` | 0.9326 |
| `transactional_character` | 0.5058 |
| `payment_redirection` | 0.2873 |
| `urgency_pressure` | 0.0677 |

**The answers are sensible for the question asked.** A credential-phishing message scores 0.9996 on
credential request and 0.93 on link lure, and low on payment redirection, which it does not do. One
sample is a demonstration that it works, not a claim about accuracy.

## Three differences from the hosted provider

**It reports `confidence` on a `noul`.** Spec §6 records the hosted provider returning `null` on every
Noul, and there is a test asserting exactly that. Laya returns a value, so that assertion is
provider-specific and would have to become provider-dependent rather than universal.

**Each answer carries `action.act_probability`**, which the hosted contract does not have.

**It needs a façade.** There is no HTTP server in the package: `system_one(state, questions)` is a
Python method on `OVAgent`. StyloMail is .NET and Laya is Python, so integration is a sidecar either
way: a small service implementing `/v1/systemone` in the shapes spec §6 records, with
`JevOptions.Endpoint` pointed at it. That endpoint is configurable, which it was not until the
credential lane fixed `BuildJevOptions` to read configuration.

## Platform, and the limits of this machine

OpenVINO **2026.4.0** installs natively on macOS arm64 and supports **CPU inference only**. The GPU
plugins are Intel-only, so on Apple Silicon there is no acceleration to be had.

- Requires **Python 3.10 or newer**; 3.11 was used, deliberately, because 3.14 predates the wheels.
- The pre-converted int8 model is **405 MB** and `OVAgent` needs neither torch nor the original
  checkpoint once it exists, which makes this cheap to run: no torch, no transformers, no GPU.
- The README claims **98.9% top-answer agreement** between the int8 export and torch. That is Laya
  against *itself*, not against TypeSafe, and it is the vendor's figure rather than ours.

## Latency, measured on this machine

| What | Time |
| --- | --- |
| Five dimensions, first call in a process | **0.522 s** |
| One dimension, warm | **0.118 to 0.183 s** |

**And the comparison is not flattering.** Spec §6.0 records the hosted provider at a **warm median of
253 ms for eleven dimensions**, which is roughly 23 ms per dimension. Laya is about **104 ms per
dimension**, so around four times slower per question on this hardware.

That is not a verdict: the hosted figure is a warm in-process call to a service running on somebody
else's hardware, and this is CPU-only inference on Apple Silicon where OpenVINO's GPU plugins do not
exist. But it is the number that decides whether a local classifier is worth having, and it is
knowable rather than a guess.

The README's own figures come from a 12th-gen Intel CPU, where the claim is 48 ms per request. It is
worth asking whether that machine would beat the hosted service, because that is the configuration a
deployment would actually choose.

## What is not established

- **Agreement with the hosted provider** on the same inputs. Nothing here compares the two, and until
  something does, "compatible local classifier" is a schema match rather than a behavioural one.
- **Whether it can be run from .NET** without a Python sidecar. It cannot, as far as this spike went.
- **Whether the int8 quantization costs accuracy in our question set**, as opposed to the README's
  98.9% top-answer agreement measured against its own torch export on its own presets.

## Where it lives

`.styloagent/spikes/laya/`, untracked, about 1.5 GB with the venv and the model. Nothing in the
repository depends on it and no source was changed.

## What I would do next, in order

1. **Measure latency** on this machine and record it with the platform, because a local classifier
   whose per-message cost is unknown cannot be compared with a hosted one.
2. **Compare answers with the hosted provider** over the corpus, dimension by dimension. That is the
   measurement that turns a schema match into a candidate, and the corpus exists for exactly this.
3. **Only then** write the façade, because the façade is easy and the agreement question is the one
   that decides whether it is worth having.
