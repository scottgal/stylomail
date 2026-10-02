#!/usr/bin/env python3
"""TDD harness for the `mailbox` profile (design rev 2, section 3.5 and section 7 item 3).

A batch whose point is a populated, varied ledger: 24 messages spread across the states the client
lists, each still declaring its facts.

THE ASSERTION THAT MATTERS MOST HERE IS A NEGATIVE ONE, and it is asserted with its population
control, because a check that never fires and a backlog of zero both print nothing. Section 3.5:

  "The word 'target' is load-bearing. Decision 27 forbids a planted fact from naming a tier, an
   action or an expected outcome, so the corpus cannot promise a Held row: it supplies the message
   and MEASURES what the message became. A harness selects rows by measured state, never by guessing
   an index."

So the manifest must carry NO field naming a state, a tier or an expected action, and the test asserts
that absence over a NON-EMPTY manifest. An absence over an empty one would be free.

Run from the repo root:
    PYTHONDONTWRITEBYTECODE=1 python3 tools/corpus/tests/test-mailbox-profile.py
"""

from __future__ import annotations

import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[3]
CORPUS = REPO / "tools" / "corpus" / "corpus.py"

MAILBOX_COUNT = 24

# The composition section 3.5 targets, stated as INTENT LABELS rather than as expected states.
# Measured from the builders themselves before this test was written, not assumed: benign is
# `ordinary correspondence`, phishing is `credential phishing`, quarantine is `payment redirection
# fraud` with `thresholdTargeted` true, and the envelope violation is `bulk unsolicited`.
EXPECTED_INTENTS = {
    "ordinary correspondence": 18,
    "credential phishing": 3,
    "payment redirection fraud": 1,
    "bulk unsolicited": 2,
}

STATE_NAMES = ("Allow", "Hold", "Quarantine", "RejectedAtIntake")
FORBIDDEN_KEYS = {
    "expectedState", "targetState", "state", "action", "expectedAction", "tier", "expectedTier",
    "outcome", "expectedOutcome",
}

FAILURES: list[str] = []
CHECKS: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    CHECKS.append(name)
    if ok:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name}{(' :: ' + detail) if detail else ''}")
        FAILURES.append(name)


def generate(out: pathlib.Path, *extra: str, count: int = MAILBOX_COUNT, profile: str = "mailbox"):
    return subprocess.run(
        [sys.executable, "-B", str(CORPUS), "generate", "--seed", "77", "--count", str(count),
         "--profile", profile, "--coverage", "full", "--out", str(out), *extra],
        cwd=str(REPO), capture_output=True, text=True,
    )


def walk_keys(node, path="") -> list[tuple[str, str]]:
    """Every (path, key) pair in a manifest, so a forbidden key is found at any depth."""
    found = []
    if isinstance(node, dict):
        for key, value in node.items():
            found.append((f"{path}.{key}" if path else key, key))
            found.extend(walk_keys(value, f"{path}.{key}" if path else key))
    elif isinstance(node, list):
        for i, value in enumerate(node):
            found.extend(walk_keys(value, f"{path}[{i}]"))
    return found


def test_mailbox_generates_a_populated_ledger(root: pathlib.Path) -> None:
    out = root / "mailbox"
    result = generate(out)
    check("mailbox run succeeds", result.returncode == 0, result.stderr.strip()[:200])
    if result.returncode:
        return
    data = json.loads((out / "manifest.json").read_text(encoding="utf-8"))
    check("24 messages are written", len(data["messages"]) == 24, str(len(data["messages"])))
    check("24 .eml files exist", len(list(out.glob("*.eml"))) == 24,
          str(len(list(out.glob("*.eml")))))
    check("every message declares at least one fact",
          all(m.get("planted") for m in data["messages"]))


def test_the_composition_matches_the_targets(root: pathlib.Path) -> None:
    out = root / "composition"
    if generate(out).returncode:
        check("composition run succeeds", False)
        return
    data = json.loads((out / "manifest.json").read_text(encoding="utf-8"))
    counts: dict[str, int] = {}
    for message in data["messages"]:
        label = message["intent"]["label"]
        counts[label] = counts.get(label, 0) + 1
    check("the intent spread is the one the design targets", counts == EXPECTED_INTENTS, str(counts))
    targeted = sum(1 for m in data["messages"] if m.get("thresholdTargeted"))
    check("the threshold-targeted message is present and singular", targeted == 1, str(targeted))


def test_no_message_promises_a_state(root: pathlib.Path) -> None:
    """Decision 27, asserted with its population control in the same run."""
    out = root / "decision27"
    if generate(out).returncode:
        check("decision-27 run succeeds", False)
        return
    data = json.loads((out / "manifest.json").read_text(encoding="utf-8"))

    # POPULATION CONTROL FIRST. The absence below is only evidence if the manifest is populated:
    # empty over an empty manifest would pass without testing anything.
    facts = sum(len(m.get("planted", [])) for m in data["messages"])
    check("CONTROL: the manifest is populated, so the absence below is not free",
          len(data["messages"]) == 24 and facts >= 24, f"messages={len(data['messages'])} facts={facts}")

    keys = walk_keys(data)
    offending = sorted({key for _, key in keys if key in FORBIDDEN_KEYS})
    check("no key anywhere names a state, action or tier", not offending, str(offending))

    # The intents are labels, and a label is a judgement rather than an outcome. Asserted so that a
    # later edit cannot quietly turn this field into the promise decision 27 forbids.
    bad_labels = sorted({
        m["intent"]["label"] for m in data["messages"]
        if m.get("intent", {}).get("label") in STATE_NAMES
    })
    check("no intent label is a state name", not bad_labels, str(bad_labels))


def test_mailbox_composes_with_the_axes(root: pathlib.Path) -> None:
    out = root / "axes"
    result = generate(out, "--encoding-mix", "mixed", "--size-mix", "mixed")
    check("mailbox with both axes succeeds", result.returncode == 0, result.stderr.strip()[:200])
    if result.returncode:
        return
    data = json.loads((out / "manifest.json").read_text(encoding="utf-8"))
    check("every message records encoding and size",
          all("encoding" in m and "size" in m for m in data["messages"]))
    check("mixed encoding reaches more than one value",
          len({m["encoding"] for m in data["messages"]}) > 1,
          str({m["encoding"] for m in data["messages"]}))
    check("mixed size reaches more than one value",
          len({m["size"] for m in data["messages"]}) > 1,
          str({m["size"] for m in data["messages"]}))


def test_a_short_mailbox_still_works(root: pathlib.Path) -> None:
    """A count that is not the recommended 24 must cycle rather than break."""
    out = root / "short"
    result = generate(out, count=6)
    check("mailbox at count=6 succeeds", result.returncode == 0, result.stderr.strip()[:200])
    if result.returncode:
        return
    data = json.loads((out / "manifest.json").read_text(encoding="utf-8"))
    check("count=6 writes 6 messages", len(data["messages"]) == 6, str(len(data["messages"])))


def test_mailbox_is_deterministic(root: pathlib.Path) -> None:
    a, b = root / "det-a", root / "det-b"
    ra = generate(a, "--encoding-mix", "mixed", "--size-mix", "mixed")
    rb = generate(b, "--encoding-mix", "mixed", "--size-mix", "mixed")
    if ra.returncode or rb.returncode:
        check("both determinism runs succeed", False, (ra.stderr or rb.stderr).strip()[:200])
        return
    check("two runs give byte-identical .eml",
          all((a / f"{i:03d}.eml").read_bytes() == (b / f"{i:03d}.eml").read_bytes()
              for i in range(MAILBOX_COUNT)))
    check("two runs give byte-identical manifest.json",
          (a / "manifest.json").read_bytes() == (b / "manifest.json").read_bytes())


def main() -> int:
    if not CORPUS.exists():
        print(f"missing {CORPUS}", file=sys.stderr)
        return 1
    root = pathlib.Path(tempfile.mkdtemp(prefix="corpus-mailbox-"))
    try:
        for name, fn in sorted(globals().items()):
            if name.startswith("test_") and callable(fn):
                print(f"\n{name}")
                try:
                    fn(root)
                except Exception as exc:
                    check(name, False, f"raised {type(exc).__name__}: {exc}")
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print()
    if FAILURES:
        print(f"{len(CHECKS)} assertion(s) run, {len(FAILURES)} FAILED: {FAILURES}")
        return len(FAILURES)
    print(f"{len(CHECKS)} assertion(s) run, all held")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
