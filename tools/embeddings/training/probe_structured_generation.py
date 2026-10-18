"""Does gemma4:26b fail this prompt because of the template or because of the output mode?

Five of five invalid JSON at the plain `format: "json"` setting is a big enough failure to test before
dismissing the model. This probes the same prompt with Ollama structured outputs (a JSON schema in the
format field) and a larger token budget, and records whether the schema constrains the answer into
valid JSON. Run it alone on the GPU: one call, measured VRAM before and after.
"""

from __future__ import annotations

import json
import os
import pathlib
import time
import urllib.request

OLLAMA = os.environ.get("EMB_OLLAMA", "http://127.0.0.1:11434")

SCHEMA = {
    "type": "object",
    "properties": {
        "turns": {
            "type": "array",
            "minItems": 2,
            "maxItems": 5,
            "items": {
                "type": "object",
                "properties": {
                    "role": {"type": "string", "enum": ["sender", "recipient"]},
                    "text": {"type": "string"},
                },
                "required": ["role", "text"],
            },
        },
        "element_turn": {"type": ["integer", "null"]},
    },
    "required": ["turns", "element_turn"],
}

PROMPT = """Write a 3-turn email thread about a payroll notice from an employer.
Exactly one turn must contain the element "credential_request" and that turn carries the label. The
conversation must look plausible.
Return JSON only, with turns (role, text) and element_turn.
"""


def call(model: str, format_value, num_predict: int) -> tuple[str, float]:
    body = json.dumps({
        "model": model,
        "prompt": PROMPT,
        "stream": False,
        "format": format_value,
        "options": {"temperature": 0.2, "seed": 7, "num_predict": num_predict},
    }).encode()
    request = urllib.request.Request(OLLAMA + "/api/generate", data=body,
                                     headers={"Content-Type": "application/json"})
    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=600) as response:
        raw = json.loads(response.read()).get("response", "")
    return raw, (time.perf_counter() - started) * 1000.0


def main() -> int:
    results = {}
    for label, format_value, budget in (
        ("plain_json", "json", 900),
        ("schema_json", SCHEMA, 2000),
    ):
        raw, latency = call("gemma4:26b", format_value, budget)
        try:
            document = json.loads(raw)
            valid = isinstance(document.get("turns"), list) and len(document["turns"]) >= 2
        except json.JSONDecodeError:
            valid = False
        results[label] = {"valid": valid, "latency_ms": round(latency, 1), "chars": len(raw)}
        print(label, results[label], flush=True)
    pathlib.Path("/mnt/c/Users/scott/structured-probe.json").write_text(json.dumps(results, indent=2) + "\n")
    print("wrote structured-probe.json")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
