"""Generate labelled conversations with the local teacher, plan first and verify every label.

Revision 2 of the prompt, from the pilot's measured failures:

* the model plans the conversation first (persona, goal, the turn where the ask lands, the cue it
  will use), then writes the turns against that plan;
* each element prompt carries two few-shot examples whose labelled turn is shown;
* the element turn must contain the stated cue, checked mechanically before any model verifier runs,
  so placement is verified rather than inferred;
* BENIGN conversations are verified too: a second prompt asks whether any turn contains any element,
  and a "yes" discards the row. The pilot's "8 of 8 verified" for benign types was not verification
  at all, because the first version only verified element types. That claim is withdrawn.

Verification provenance is explicit. gemma4:26b is excluded after returning empty responses in both
JSON modes, so a same-model check is labelled "second prompt (same model)", never "independent
verifier", and the manifest says which one ran.

Modes:
  benchmark  five generations per candidate model, latency and label pass rate
  benign     scale the four hard-negative types, each verified to contain no element
  elements   element types under the revised prompt, per-type verified yield
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import random
import re
import time
import urllib.request

OLLAMA = os.environ.get("EMB_OLLAMA", "http://127.0.0.1:11434")
ELEMENTS = ("credential_request", "urgency_pressure", "threat", "impersonated_greeting", "attachment_lure")
NEGATIVE_TYPES = ("provider_security_notice", "transactional_receipt", "invoice_reminder", "urgent_legitimate_thread")
CANDIDATE_MODELS = ("gemma4:e4b", "gemma4:26b", "qwen3.8:latest")
GENERATOR = "gemma4:e4b"

TOPICS = ("payroll", "parcel delivery", "cloud storage", "travel booking", "building access",
          "tax refund", "subscription renewal", "medical appointment", "conference registration")
PERSONAS = ("bank", "employer", "vendor", "colleague", "landlord", "insurer", "travel agent")
FORMALITY = ("formal", "neutral", "informal")
CHANNELS = ("email thread", "chat")
OBFUSCATION = ("none", "typos", "spacing", "homoglyph")

FEW_SHOTS = {
    "credential_request": (
        "Example labelled turn: \"To keep your mailbox active, sign in with your work email and "
        "password on the verification page we linked before Friday.\"",
        "Example labelled turn: \"Reply with your account number and online banking PIN so the "
        "security update can be completed today.\"",
    ),
    "urgency_pressure": (
        "Example labelled turn: \"You have two hours to respond or the account is permanently closed.\"",
        "Example labelled turn: \"Final notice: payment must arrive today or the service is suspended.\"",
    ),
    "threat": (
        "Example labelled turn: \"We have copies of your files and will publish them to your contacts "
        "unless payment arrives.\"",
        "Example labelled turn: \"Failure to pay will result in legal action against you this week.\"",
    ),
    "impersonated_greeting": (
        "Example labelled turn: \"Dear Customer, this is Microsoft Support and your licence has expired; "
        "call the number below.\"",
        "Example labelled turn: \"Hello, I am the CFO and I need you to process this payment privately.\"",
    ),
    "attachment_lure": (
        "Example labelled turn: \"Open the attached invoice and enable macros so the payment details "
        "become visible.\"",
        "Example labelled turn: \"The attached statement will not display until you disable antivirus "
        "protection and reopen it.\"",
    ),
}

PLAN_TEMPLATE = """You write realistic email or chat conversations for a security research corpus.

Plan first, then write. {task}

Controls, already sampled: topic {topic}; sender persona {persona}; tone {formality}; channel
{channel}; obfuscation {obfuscation}.

{examples}

Return ONLY JSON:
{{"plan":{{"persona":"...","goal":"...","element_turn":<index or null>,"cue":"<exact phrase or null>"}},
 "turns":[{{"role":"sender|recipient","text":"..."}}],
 "element_turn":<index or null>,
 "cue":"<the exact phrase from that turn, or null>"}}

Rules: 2 to 5 turns, each 20 to 120 words. If an element is required, the phrase in "cue" MUST appear
verbatim in the turn at "element_turn", and that is the only turn carrying the element. No real names,
no real addresses, no resolving URLs.
"""

ELEMENT_VERIFY_TEMPLATE = """Does this turn contain the element "{element}"?
Element meanings: credential_request asks the reader to send or confirm credentials; urgency_pressure
imposes a short deadline or threatens loss for not acting; threat threatens harm, exposure, legal
action or retaliation; impersonated_greeting claims to be a company or authority the sender is not;
attachment_lure asks the reader to open an attachment, enable macros or view a document.

The writer claims the cue "{cue}" is present. Answer ONLY JSON:
{{"contains": true|false, "cue_present": true|false, "why":"short reason"}}

Turn:
{turn_text}
"""

BENIGN_VERIFY_TEMPLATE = """Does ANY turn below contain one of these elements?
{elements}
credential_request asks for credentials; urgency_pressure imposes a short deadline or loss; threat
threatens harm or legal action; impersonated_greeting claims a false identity or authority;
attachment_lure asks to open an attachment or enable macros.

Answer ONLY JSON: {{"contains": true|false, "which":["..."]}}

Turns:
{turns}
"""


def prompt_hash(template: str) -> str:
    return hashlib.sha256(template.encode("utf-8")).hexdigest()[:16]


def call(model: str, prompt: str, seed: int, budget: int = 1200) -> tuple[str, float]:
    body = json.dumps({
        "model": model, "prompt": prompt, "stream": False, "format": "json",
        "options": {"temperature": 0.8, "seed": seed, "num_predict": budget},
    }).encode("utf-8")
    request = urllib.request.Request(OLLAMA + "/api/generate", data=body,
                                     headers={"Content-Type": "application/json"})
    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=600) as response:
        payload = json.loads(response.read())
    return payload.get("response", ""), (time.perf_counter() - started) * 1000.0


def redact(text: str) -> str:
    text = re.sub(r"\b[\w.+-]+@[\w-]+\.[\w.]+\b", "[email]", text)
    text = re.sub(r"https?://\S+", "[url]", text)
    text = re.sub(r"\b(?:\+?\d[\d\s().-]{7,}\d)\b", "[number]", text)
    return text


def parse_document(raw: str, element: str | None) -> dict | None:
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
    index = document.get("element_turn")
    cue = document.get("cue")
    if element:
        if not isinstance(index, int) or not (0 <= index < len(cleaned)):
            return None
        if not isinstance(cue, str) or len(cue.strip()) < 4:
            return None
        # Mechanical placement check before any model is asked: the claimed cue must be in the turn.
        if cue.strip().lower() not in cleaned[index]["text"].lower():
            return None
    else:
        index, cue = None, None
    return {"turns": cleaned, "element_turn": index, "cue": cue, "plan": document.get("plan")}


def verify_element(model: str, element: str, cue: str, turn_text: str, seed: int) -> bool:
    prompt = ELEMENT_VERIFY_TEMPLATE.format(element=element, cue=cue, turn_text=turn_text)
    raw, _ = call(model, prompt, seed, budget=200)
    try:
        document = json.loads(raw)
    except json.JSONDecodeError:
        return False
    return bool(document.get("contains")) and bool(document.get("cue_present"))


def verify_benign(model: str, turns: list[dict], seed: int) -> bool:
    joined = "\n".join(f"{turn['role']}: {turn['text']}" for turn in turns)
    prompt = BENIGN_VERIFY_TEMPLATE.format(elements=", ".join(ELEMENTS), turns=joined)
    raw, _ = call(model, prompt, seed, budget=200)
    try:
        document = json.loads(raw)
    except json.JSONDecodeError:
        return False
    return not bool(document.get("contains"))


def build_prompt(kind: str, controls: dict) -> str:
    if kind in ELEMENTS:
        task = ('If the type is an element type, exactly one turn must contain the element '
                f'"{kind}" and that turn carries the label.')
        examples = "\n".join(FEW_SHOTS[kind])
    else:
        task = ('This is a hard negative: no turn may contain any of these elements: '
                f'{", ".join(ELEMENTS)}. It must look plausible and benign.')
        examples = "Example: a real provider security notice that reports an action already taken and asks for nothing."
    return PLAN_TEMPLATE.format(task=task, examples=examples, **controls)


def benchmark(output: pathlib.Path, generations: int, seed: int) -> int:
    rng = random.Random(seed)
    results = {}
    for model in CANDIDATE_MODELS:
        latencies, valid, verified = [], 0, 0
        for index in range(generations):
            kind = ELEMENTS[index % len(ELEMENTS)]
            controls = {"topic": rng.choice(TOPICS), "persona": rng.choice(PERSONAS),
                        "formality": rng.choice(FORMALITY), "channel": rng.choice(CHANNELS),
                        "obfuscation": rng.choice(OBFUSCATION)}
            attempt_seed = rng.randrange(1, 2**31)
            raw, latency = call(model, build_prompt(kind, controls), attempt_seed)
            latencies.append(latency)
            parsed = parse_document(raw, kind)
            if parsed is None:
                continue
            valid += 1
            if verify_element(GENERATOR, kind, parsed["cue"], parsed["turns"][parsed["element_turn"]]["text"], attempt_seed):
                verified += 1
        results[model] = {"generations": generations, "json_valid": valid, "label_verified": verified,
                          "label_pass_rate": round(verified / generations, 3),
                          "latency_ms_median": round(sorted(latencies)[len(latencies) // 2], 1)}
        print(model, results[model], flush=True)
    output.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {output}")
    return 0


def generate(output: pathlib.Path, manifest_path: pathlib.Path, kinds: tuple[str, ...], per_type: int,
             seed: int, model: str) -> int:
    rng = random.Random(seed)
    rows, discarded = [], 0
    attempts: dict[str, int] = {}
    verified: dict[str, int] = {}
    verifier_note = "second prompt (same model); 26b excluded for empty responses"

    for kind in kinds:
        attempts[kind] = 0
        verified[kind] = 0
        for index in range(per_type):
            attempts[kind] += 1
            conversation_seed = rng.randrange(1, 2**31)
            controls = {"topic": rng.choice(TOPICS), "persona": rng.choice(PERSONAS),
                        "formality": rng.choice(FORMALITY), "channel": rng.choice(CHANNELS),
                        "obfuscation": rng.choice(OBFUSCATION)}
            raw, _ = call(model, build_prompt(kind, controls), conversation_seed)
            element = kind if kind in ELEMENTS else None
            parsed = parse_document(raw, element)
            if parsed is None:
                discarded += 1
                continue
            if element:
                ok = verify_element(model, element, parsed["cue"],
                                    parsed["turns"][parsed["element_turn"]]["text"], conversation_seed)
            else:
                ok = verify_benign(model, parsed["turns"], conversation_seed)
            if not ok:
                discarded += 1
                continue
            verified[kind] += 1
            conversation_id = f"gen:{kind}:{hashlib.sha256(str(conversation_seed).encode()).hexdigest()[:10]}"
            split = "heldout" if index % 5 == 4 else "train"
            for number, turn in enumerate(parsed["turns"]):
                rows.append({
                    "id": f"{conversation_id}:{number}",
                    "conversation_id": conversation_id,
                    "type": kind,
                    "turn": number,
                    "role": turn["role"],
                    "text": turn["text"],
                    "labels": [element] if (element and number == parsed["element_turn"]) else [],
                    "cue": parsed["cue"] if element and number == parsed["element_turn"] else None,
                    "split": split,
                    "generator_model": model,
                    "verifier": verifier_note,
                    "prompt_hash": prompt_hash(PLAN_TEMPLATE),
                    "element_prompt_hash": prompt_hash(ELEMENT_VERIFY_TEMPLATE),
                    "benign_prompt_hash": prompt_hash(BENIGN_VERIFY_TEMPLATE),
                    "seed": conversation_seed,
                    "controls": controls,
                    "plan": parsed["plan"],
                    "licence": "project-owned",
                })

    output.write_text("\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")
    yields = {kind: round(verified[kind] / attempts[kind], 3) if attempts[kind] else None for kind in kinds}
    manifest = {
        "generator_model": model,
        "verifier": verifier_note,
        "prompt_hash": prompt_hash(PLAN_TEMPLATE),
        "attempts": attempts,
        "verified": verified,
        "verified_yield": yields,
        "verification_state": {
            "checked_clean": verified,
            "not_checked": {kind: 0 for kind in kinds},
            "note": "a row is written only after its check passes, so not_checked is zero by construction",
        },
        "rows": len(rows),
        "discarded": discarded,
        "seed": seed,
        "licence": "project-owned",
        "split_by": "conversation id",
        "benign_verification": "second prompt asks whether any turn contains any element; yes discards",
        "element_verification": "mechanical cue-in-turn check, then a second prompt asking presence",
    }
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("benchmark", "benign", "elements"), required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--manifest", type=pathlib.Path)
    parser.add_argument("--model", default=GENERATOR)
    parser.add_argument("--generations", type=int, default=5)
    parser.add_argument("--per-type", type=int, default=2)
    parser.add_argument("--seed", type=int, default=20261018)
    args = parser.parse_args()

    if args.mode == "benchmark":
        return benchmark(args.output, args.generations, args.seed)

    kinds = NEGATIVE_TYPES if args.mode == "benign" else ELEMENTS
    manifest = args.manifest or args.output.with_suffix(".manifest.json")
    return generate(args.output, manifest, kinds, args.per_type, args.seed, args.model)


if __name__ == "__main__":
    raise SystemExit(main())
