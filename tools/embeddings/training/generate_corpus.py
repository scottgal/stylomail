"""Generate labelled conversations with the local teacher models, and verify the labels.

Two jobs in one instrument:

* ``--mode benchmark`` runs five generations per candidate model and reports latency, JSON validity
  and label-fidelity (a second model is asked whether the labelled turn really contains the element).
  The model is then chosen on those numbers rather than on preference.
* ``--mode pilot`` (or ``--mode run``) generates conversations for the nine types, verifies every
  labelled turn with a different model, discards disagreeing rows, and writes a manifest that can
  regenerate any conversation exactly from its seed.

Provenance per conversation: generator model tag, prompt template hash, seed, conversation id, type,
turn, labels, licence project-owned. No PII: the prompt forbids real addresses and the output is
redacted before it is written.

Run it ON the inference host so generated content never leaves the machine:

    python generate_corpus.py --mode benchmark --output tone/../generation-benchmark.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import random
import re
import time
import urllib.request

OLLAMA = __import__("os").environ.get("EMB_OLLAMA", "http://127.0.0.1:11434")
ELEMENTS = (
    "credential_request",
    "urgency_pressure",
    "threat",
    "impersonated_greeting",
    "attachment_lure",
)
NEGATIVE_TYPES = (
    "provider_security_notice",
    "transactional_receipt",
    "invoice_reminder",
    "urgent_legitimate_thread",
)
TYPES = ELEMENTS + NEGATIVE_TYPES
CANDIDATE_MODELS = ("gemma4:e4b", "gemma4:26b", "qwen3.8:latest")

TOPICS = ("payroll", "parcel delivery", "cloud storage", "travel booking", "building access",
          "tax refund", "subscription renewal", "medical appointment", "conference registration",
          "insurance claim")
PERSONAS = ("bank", "employer", "vendor", "colleague", "landlord", "insurer", "travel agent")
FORMALITY = ("formal", "neutral", "informal")
CHANNELS = ("email thread", "chat")
OBFUSCATION = ("none", "typos", "spacing", "homoglyph")

PROMPT_TEMPLATE = """You write realistic email or chat conversations for a security research corpus.

Write a {turns}-turn {channel} conversation about {topic} where the sender persona is a {persona}.
Tone: {formality}. Obfuscation to apply: {obfuscation}.

If the type is an element type, exactly one turn must contain the element "{element}" and that turn
carries the label. If the type is a hard negative ("{negative}"), no turn contains any of these
elements: {elements}. The conversation must look plausible and benign.

Return ONLY JSON with this shape:
{{"turns":[{{"role":"sender|recipient","text":"..."}}],"element_turn":<index or null>}}

Rules: 2 to {max_turns} turns, each turn 20 to 120 words. No real names, no real email addresses,
no real phone numbers, no URLs that resolve to a real domain.
"""

VERIFY_TEMPLATE = """You are checking a labelled corpus. Does turn {turn} contain the element
"{element}"? Element meanings: credential_request asks the reader to send or confirm credentials;
urgency_pressure imposes a short deadline or threat of loss for not acting; threat threatens harm,
exposure or legal action; impersonated_greeting claims to be from a company or authority the sender is
not; attachment_lure asks the reader to open an attachment or enable macros.

Answer ONLY JSON: {{"contains": true|false, "why": "short reason"}}

Turn:
{turn_text}
"""


def prompt_hash(template: str) -> str:
    return hashlib.sha256(template.encode("utf-8")).hexdigest()[:16]


def call(model: str, prompt: str, seed: int, timeout: int = 300) -> tuple[str, float]:
    body = json.dumps({
        "model": model,
        "prompt": prompt,
        "stream": False,
        "format": "json",
        "options": {"temperature": 0.8, "seed": seed, "num_predict": 900},
    }).encode("utf-8")
    request = urllib.request.Request(OLLAMA + "/api/generate", data=body,
                                     headers={"Content-Type": "application/json"})
    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=timeout) as response:
        payload = json.loads(response.read())
    return payload.get("response", ""), (time.perf_counter() - started) * 1000.0


def redact(text: str) -> str:
    text = re.sub(r"\b[\w.+-]+@[\w-]+\.[\w.]+\b", "[email]", text)
    text = re.sub(r"https?://\S+", "[url]", text)
    text = re.sub(r"\b(?:\+?\d[\d\s().-]{7,}\d)\b", "[number]", text)
    return text


def parse_turns(raw: str) -> list[dict] | None:
    try:
        document = json.loads(raw)
    except json.JSONDecodeError:
        return None
    turns = document.get("turns")
    if not isinstance(turns, list) or not (2 <= len(turns) <= 5):
        return None
    cleaned = []
    for turn in turns:
        text = str(turn.get("text", "")).strip()
        if len(text.split()) < 10:
            return None
        cleaned.append({"role": str(turn.get("role", "sender")), "text": redact(text)})
    return cleaned


def verify(model: str, element: str, turn_text: str, turn_index: int, seed: int) -> bool:
    prompt = VERIFY_TEMPLATE.format(element=element, turn=turn_index, turn_text=turn_text)
    raw, _ = call(model, prompt, seed)
    try:
        document = json.loads(raw)
    except json.JSONDecodeError:
        return False
    return bool(document.get("contains"))


def build_prompt(kind: str, rng: random.Random) -> tuple[str, dict]:
    element = kind if kind in ELEMENTS else "none"
    negative = kind if kind in NEGATIVE_TYPES else "none"
    controls = {
        "topic": rng.choice(TOPICS),
        "persona": rng.choice(PERSONAS),
        "formality": rng.choice(FORMALITY),
        "channel": rng.choice(CHANNELS),
        "obfuscation": rng.choice(OBFUSCATION),
        "turns": rng.randint(2, 4),
    }
    prompt = PROMPT_TEMPLATE.format(
        element=element,
        negative=negative,
        elements=", ".join(ELEMENTS),
        max_turns=5,
        **controls,
    )
    return prompt, controls


def benchmark(output: pathlib.Path, generations: int, seed: int) -> int:
    rng = random.Random(seed)
    results = {}
    for model in CANDIDATE_MODELS:
        latencies, valid, verified, attempts = [], 0, 0, 0
        for _ in range(generations):
            attempt_seed = rng.randrange(1, 2**31)
            prompt, _controls = build_prompt(ELEMENTS[_ % len(ELEMENTS)], rng)
            raw, latency = call(model, prompt, attempt_seed)
            latencies.append(latency)
            attempts += 1
            turns = parse_turns(raw)
            if turns is None:
                continue
            valid += 1
            element = ELEMENTS[_ % len(ELEMENTS)]
            if verify("gemma4:26b" if model != "gemma4:26b" else "qwen3.8:latest",
                      element, turns[-1]["text"], len(turns) - 1, attempt_seed):
                verified += 1
        results[model] = {
            "generations": attempts,
            "json_valid": valid,
            "label_verified": verified,
            "label_pass_rate": round(verified / attempts, 3) if attempts else None,
            "latency_ms_median": round(sorted(latencies)[len(latencies) // 2], 1) if latencies else None,
        }
        print(model, results[model], flush=True)
    output.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {output}")
    return 0


def generate(output: pathlib.Path, manifest_path: pathlib.Path, model: str, per_type: int, seed: int) -> int:
    rng = random.Random(seed)
    rows = []
    discarded = 0
    types_done = {}
    for kind in TYPES:
        for index in range(per_type):
            conversation_seed = rng.randrange(1, 2**31)
            prompt, controls = build_prompt(kind, random.Random(conversation_seed))
            raw, _latency = call(model, prompt, conversation_seed)
            turns = parse_turns(raw)
            if turns is None:
                discarded += 1
                continue
            element = kind if kind in ELEMENTS else None
            turn_index = len(turns) - 1 if element else None
            if element:
                verifier = "gemma4:26b" if model != "gemma4:26b" else "qwen3.8:latest"
                if not verify(verifier, element, turns[turn_index]["text"], turn_index, conversation_seed):
                    discarded += 1
                    continue
            conversation_id = f"gen:{kind}:{hashlib.sha256(str(conversation_seed).encode()).hexdigest()[:10]}"
            split = "heldout" if index % 5 == 4 else "train"
            for turn_number, turn in enumerate(turns):
                rows.append({
                    "id": f"{conversation_id}:{turn_number}",
                    "conversation_id": conversation_id,
                    "type": kind,
                    "turn": turn_number,
                    "role": turn["role"],
                    "text": turn["text"],
                    "labels": [element] if (element and turn_number == turn_index) else [],
                    "split": split,
                    "generator_model": model,
                    "prompt_hash": prompt_hash(PROMPT_TEMPLATE),
                    "seed": conversation_seed,
                    "controls": controls,
                    "licence": "project-owned",
                })
            types_done[kind] = types_done.get(kind, 0) + 1
    output.write_text("\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")
    manifest = {
        "generator_model": model,
        "verifier_model": "gemma4:26b" if model != "gemma4:26b" else "qwen3.8:latest",
        "prompt_template_hash": prompt_hash(PROMPT_TEMPLATE),
        "verify_template_hash": prompt_hash(VERIFY_TEMPLATE),
        "conversations_per_type": types_done,
        "rows": len(rows),
        "discarded_on_parse_or_verification": discarded,
        "seed": seed,
        "licence": "project-owned",
        "split_by": "conversation id, so all turns of one conversation stay on one side",
    }
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("benchmark", "pilot", "run"), required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--manifest", type=pathlib.Path)
    parser.add_argument("--model", default=None)
    parser.add_argument("--generations", type=int, default=5)
    parser.add_argument("--per-type", type=int, default=None)
    parser.add_argument("--seed", type=int, default=20261018)
    args = parser.parse_args()

    if args.mode == "benchmark":
        return benchmark(args.output, args.generations, args.seed)

    model = args.model or "gemma4:26b"
    per_type = args.per_type or (2 if args.mode == "pilot" else 50)
    manifest = args.manifest or args.output.with_suffix(".manifest.json")
    return generate(args.output, manifest, model, per_type, args.seed)


if __name__ == "__main__":
    raise SystemExit(main())
