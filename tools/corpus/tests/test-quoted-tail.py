#!/usr/bin/env python3
"""TDD harness for `--quoted-tail`, the reply-shaped fixture this generator cannot otherwise emit.

WHY IT EXISTS. `shortened` is a DISJUNCTION -- `Shortened(BodyText, body) || Shortened(QuotedText, quoted)`
-- so a reading of `flag + kept + total` cannot say which field was cut. The only size at which the
QUOTED disjunct fires UNCONDITIONALLY is above the fit's starting budget:

    `NimbleMessageState.cs:62-63` caps BOTH fields with the SAME budget
    `:228-236` `Truncate` returns the value unchanged when `value.Length <= maxCharacters`
    `:219-220` `Shortened` is a strict `<`
    `NimbleOptions.cs:229` `MaxBodyCharacters = 2_500`

    so the disjunct fires iff `QuotedText.Length > budget`,
    and unconditionally iff `QuotedText.Length > 2500`.

AND THE CORPUS ALREADY HAS ONE REPLY, WHICH IS WHY THE CRITERION IS A SIZE AND NOT A SHAPE. The committed
fixtures at `tests/fixtures/jev/` include `reply-in-thread.eml`, whose quoted tail is 162 characters --
above zero and far below the budget, so it fires only if a fit pass drives the budget under it. **This
option is for the unconditional case.**

WHAT THIS FILE DOES NOT ASSERT: that a real run raises the flag on the quoted field. That is the Host's
evaluation and is read back through a capture, never predicted here. What it asserts is checkable and
local: **the message has a quoted section, the splitter's own marker is present, and the section's size is
above the budget so that the disjunct is not conditional on a fit pass.**

Run from the repo root:
    PYTHONDONTWRITEBYTECODE=1 python3 tools/corpus/tests/test-quoted-tail.py
"""

from __future__ import annotations

import email
import email.policy
import json
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[3]
CORPUS = REPO / "tools" / "corpus" / "corpus.py"

# The splitter's own marker, taken verbatim from `QuotedHistory.cs:34-35`. A tail appended WITHOUT this
# line would be invisible to the split, so the fixture would carry a long body and no quoted field.
ATTRIBUTION = re.compile(r"^\s*on .{0,200}\bwrote:\s*$", re.M | re.I)

# `NimbleOptions.MaxBodyCharacters`'s default, which is the budget the disjunct is conditional on.
BUDGET = 2500

FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    if ok:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name}{(' :: ' + detail) if detail else ''}")
        FAILURES.append(name)


def generate(out: pathlib.Path, *extra: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, "-B", str(CORPUS), "generate", "--seed", "77", "--count", "2",
         "--profile", "benign", "--coverage", "full", "--out", str(out), *extra],
        cwd=str(REPO), capture_output=True, text=True,
    )


def body_of(raw: bytes) -> str:
    """The text/plain part WITH ITS LINE STRUCTURE, which is the object the splitter reads.

    NOT stripped, deliberately: every one of `QuotedHistory.Split`'s five markers is `^`-anchored with
    `Multiline`, so they are found on LINES. A helper that joined the lines -- as the body-shapes harness
    does for its alphabet checks -- would report every marker absent no matter what the fixture carried,
    which is a broken instrument agreeing with a hypothesis.
    """
    message = email.message_from_bytes(raw, policy=email.policy.default)
    if message.is_multipart():
        for part in message.walk():
            if part.get_content_type() == "text/plain":
                return str(part.get_content())
        raise AssertionError("no text/plain part")
    return str(message.get_content())


def declared(manifest: pathlib.Path) -> list[dict]:
    return json.loads(manifest.read_text(encoding="utf-8"))["messages"]


def test_a_tail_above_the_budget_is_emitted_with_the_splitter_s_marker(root: pathlib.Path) -> None:
    batch = root / "above"
    result = generate(batch, "--quoted-tail", "3000")
    check("generate --quoted-tail 3000 succeeds", result.returncode == 0, result.stderr[:300])
    messages = declared(batch / "manifest.json")

    body = body_of((batch / messages[0]["file"]).read_bytes())
    check("the message carries the splitter's own attribution marker", bool(ATTRIBUTION.search(body)),
          body[-90:])
    check("the manifest declares the tail length", messages[0].get("quotedTail") == 3000,
          str(messages[0].get("quotedTail")))

    match = ATTRIBUTION.search(body)
    tail = body[match.end():] if match else ""
    check(f"the quoted section exceeds the {BUDGET} budget, so the disjunct is NOT conditional",
          len(tail) > BUDGET, f"tail={len(tail)}")


def test_the_default_emits_no_tail(root: pathlib.Path) -> None:
    """CONTROL: without the flag nothing changes, so the tail is attributable to it."""
    batch = root / "off"
    result = generate(batch)
    check("generate with no --quoted-tail succeeds", result.returncode == 0, result.stderr[:200])
    messages = declared(batch / "manifest.json")
    body = body_of((batch / messages[0]["file"]).read_bytes())
    check("CONTROL: no attribution marker without the flag", not ATTRIBUTION.search(body), body[:80])
    check("CONTROL: and no quotedTail is declared", "quotedTail" not in messages[0],
          str(list(messages[0].keys()))[:150])


def test_a_tail_under_the_budget_is_reported_as_conditional(root: pathlib.Path) -> None:
    """The size IS the criterion, so a small tail has to be visibly small rather than refusable.

    162 is the committed fixture's own tail length, which fires only if a fit pass drives the budget
    under it. Emitting it is legal; claiming it is unconditional is not.
    """
    batch = root / "below"
    result = generate(batch, "--quoted-tail", "162")
    check("a tail below the budget is still emittable", result.returncode == 0, result.stderr[:300])
    messages = declared(batch / "manifest.json")
    check("and it is declared as the small size it is", messages[0].get("quotedTail") == 162,
          str(messages[0].get("quotedTail")))
    body = body_of((batch / messages[0]["file"]).read_bytes())
    match = ATTRIBUTION.search(body)
    tail = body[match.end():] if match else ""
    check("and its quoted section is under the budget, so it is CONDITIONAL",
          len(tail) <= BUDGET, f"tail={len(tail)}")


def main() -> int:
    if not CORPUS.exists():
        print(f"missing {CORPUS}", file=sys.stderr)
        return 1
    root = pathlib.Path(tempfile.mkdtemp(prefix="corpus-quoted-tail-"))
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
        print(f"FAILED: {len(FAILURES)} assertion(s): {FAILURES}")
        return len(FAILURES)
    print("all assertions held")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
