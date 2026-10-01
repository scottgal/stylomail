#!/usr/bin/env python3
"""TDD harness for the size axis of design rev 2 (sections 3.2 and 3.7).

Design: the desktop-harness design rev 2. That doc is an UNTRACKED lane artifact
(`.styloagent/scratch/...` when the lane's scratch is present, so NOT available in a plain
checkout); the constraint this file depends on is restated below so the file stands alone.

Section 3.2's hard constraint, which is the assertion that carries this file: **large must be carried
by the attachment and the HTML part, never by the text body**, because the text body is what the model
is asked about and the adapter's body budget is `NimbleOptions.MaxBodyCharacters` 2500, which this
corpus now uses as its own limit rather than the 2000 it asserted before that was measured. A fixture grown through the body
is a corpus defect that surfaces as a provider refusal.

Section 3.2 also forbids the tempting shortcut of adding a part that does not already exist: a size
variant that silently attaches a file would move `has_attachments` as a side effect of length, and the
design says a coverage flag must be its own fixture rather than a side effect of another axis.

Run from the repo root:
    PYTHONDONTWRITEBYTECODE=1 python3 tools/corpus/tests/test-size-axis.py
"""

from __future__ import annotations

import email
import email.policy
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[3]
CORPUS = REPO / "tools" / "corpus" / "corpus.py"

# The adapter's body budget, READ FROM THE CODE rather than copied into this file.
#
# A local `TURN_LIMIT = 2000` lived here and went stale the moment the real limit moved to 2500
# (`NimbleOptions.MaxBodyCharacters`, `NimbleOptions.cs:229`): the assertion still PASSED, because a
# stricter bound is satisfied too, so this file kept measuring a boundary the implementation no longer
# had. That is the silent-staleness class, and importing the constant is what stops it recurring.
def _corpus_constant(name: str):
    import importlib.util
    import sys as _sys

    spec = importlib.util.spec_from_file_location("corpus_under_test", CORPUS)
    module = importlib.util.module_from_spec(spec)
    # REGISTERED BEFORE IT IS EXECUTED, and this line is load-bearing rather than tidy: `corpus.py`
    # defines `@dataclass` types under `from __future__ import annotations`, and `dataclasses` resolves
    # a string annotation by looking its module up in `sys.modules`. Executing an unregistered module
    # raises `AttributeError: 'NoneType' object has no attribute '__dict__'` from inside the stdlib,
    # which reads as a defect in the import rather than in the registration.
    _sys.modules["corpus_under_test"] = module
    spec.loader.exec_module(module)
    return getattr(module, name)


TURN_LIMIT = _corpus_constant("TURN_LIMIT")

FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    if ok:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name}{(' :: ' + detail) if detail else ''}")
        FAILURES.append(name)


def generate(out: pathlib.Path, profile: str, *extra: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [
            sys.executable, "-B", str(CORPUS), "generate",
            "--seed", "77", "--count", "6", "--profile", profile,
            "--coverage", "full", "--out", str(out), *extra,
        ],
        cwd=str(REPO), capture_output=True, text=True,
    )


def manifest_of(out: pathlib.Path) -> dict:
    return json.loads((out / "manifest.json").read_text(encoding="utf-8"))


def part_bytes(raw: bytes) -> tuple[int, int]:
    """(html bytes, attachment bytes) as actually present in the message."""
    msg = email.message_from_bytes(raw, policy=email.policy.default)
    html = 0
    att = 0
    for part in msg.walk():
        ctype = part.get_content_type()
        if part.get_content_maintype() == "multipart":
            continue
        payload = part.get_payload(decode=True) or b""
        if ctype == "text/html":
            html += len(payload)
        elif ctype == "application/pdf":
            att += len(payload)
    return html, att


def test_unknown_size_mix_is_refused(root: pathlib.Path) -> None:
    out = root / "bogus"
    result = generate(out, "phishing", "--size-mix", "enormous")
    check("unknown --size-mix exits 2", result.returncode == 2, f"rc={result.returncode}")
    check("unknown --size-mix writes no batch", not (out / "manifest.json").exists())


def test_default_matches_small(root: pathlib.Path) -> None:
    a, b = root / "small-default", root / "small-explicit"
    ra = generate(a, "mixed")
    rb = generate(b, "mixed", "--size-mix", "small")
    check("no-flag run succeeds", ra.returncode == 0, ra.stderr.strip()[:200])
    if ra.returncode or rb.returncode:
        check("explicit small run succeeds", rb.returncode == 0, rb.stderr.strip()[:200])
        return
    same = all(
        (a / f"{i:03d}.eml").read_bytes() == (b / f"{i:03d}.eml").read_bytes() for i in range(6)
    )
    check("omitted --size-mix is byte-identical to small", same)


def test_large_rides_the_html_and_never_the_body(root: pathlib.Path) -> None:
    small, large = root / "sz-small", root / "sz-large"
    generate(small, "phishing", "--size-mix", "small")
    result = generate(large, "phishing", "--size-mix", "large")
    check("large run succeeds", result.returncode == 0, result.stderr.strip()[:200])
    if result.returncode:
        return

    msgs = manifest_of(large)["messages"]
    over = [m["index"] for m in msgs if m["turnCharacters"] > TURN_LIMIT]
    check("no message's turn reaches the truncation limit", not over, f"indices {over}")

    grew_html = 0
    grew_att = 0
    for i in range(6):
        big = (large / f"{i:03d}.eml").read_bytes()
        small_raw = (small / f"{i:03d}.eml").read_bytes()
        bh, ba = part_bytes(big)
        sh, sa = part_bytes(small_raw)
        if bh > sh:
            grew_html += 1
        if ba > sa:
            grew_att += 1
    check("the html part grew in every message", grew_html == 6, f"grew={grew_html}")
    check("the attachment grew in every message", grew_att == 6, f"grew={grew_att}")


def test_a_message_with_no_carrier_is_refused_not_faked(root: pathlib.Path) -> None:
    """`pair` is text-only: growing it would have to grow the body, which section 3.2 forbids.

    The honest outcome is a refusal naming the index. The dishonest ones, both of which this asserts
    against, are a small message whose manifest says `large`, and a silently added attachment.
    """
    out = root / "large-textonly"
    result = generate(out, "pair", "--size-mix", "large")
    check("large on a text-only profile exits 2", result.returncode == 2, f"rc={result.returncode}")
    check("large on a text-only profile writes no manifest", not (out / "manifest.json").exists())
    check("the refusal names the message index", "index 0" in result.stderr, result.stderr.strip()[:200])

    # AND THE CASE WHERE THE TEXT-ONLY MESSAGE IS NOT FIRST, which is the one that discriminates
    # between a pre-flight refusal and an abort part-way through the loop. `mixed` index 3 is the
    # text-only one (measured), so a mid-loop refusal writes 000..002 and then stops.
    #
    # The first version of this assertion named "writes no batch" and tested only for
    # `manifest.json`, so it passed while the refusal left orphan .eml files behind: the assertion was
    # narrower than the sentence it carried, and only the late-refusal case exposes it. Anything
    # globbing a batch directory finds those files in a directory that is not a batch.
    late = root / "large-mixed"
    result = generate(late, "mixed", "--size-mix", "large")
    check("a late refusal exits 2", result.returncode == 2, f"rc={result.returncode}")
    check("the late refusal names the offending index", "index 3" in result.stderr,
          result.stderr.strip()[:200])
    leftovers = sorted(p.name for p in late.glob("*.eml")) if late.exists() else []
    check("a refused batch leaves NO partial .eml behind", not leftovers, str(leftovers))


def test_mixed_draws_only_what_a_message_can_carry(root: pathlib.Path) -> None:
    out = root / "sz-mixed"
    result = generate(out, "mixed", "--size-mix", "mixed")
    check("mixed size run succeeds", result.returncode == 0, result.stderr.strip()[:200])
    if result.returncode:
        return

    msgs = manifest_of(out)["messages"]
    check("every message records `size`", all("size" in m for m in msgs),
          str([m.get("size") for m in msgs]))
    check("drawn sizes are all known", {m.get("size") for m in msgs} <= {"small", "medium", "large"},
          str({m.get("size") for m in msgs}))

    # mixed index 3 is the text-only one (measured: 003 has no html and no attachment).
    textonly = msgs[3]
    check("the text-only message draws `small`", textonly.get("size") == "small",
          f"drew {textonly.get('size')!r}")
    check("`mixed` reaches more than one size across a batch with carriers",
          len({m.get("size") for m in msgs}) > 1, str({m.get("size") for m in msgs}))


def test_the_axis_is_deterministic(root: pathlib.Path) -> None:
    a, b = root / "sz-det-a", root / "sz-det-b"
    ra = generate(a, "phishing", "--size-mix", "mixed")
    rb = generate(b, "phishing", "--size-mix", "mixed")
    if ra.returncode or rb.returncode:
        check("both determinism runs succeed", False, (ra.stderr or rb.stderr).strip()[:200])
        return
    check("two runs give byte-identical .eml",
          all((a / f"{i:03d}.eml").read_bytes() == (b / f"{i:03d}.eml").read_bytes() for i in range(6)))
    check("two runs give byte-identical manifest.json",
          (a / "manifest.json").read_bytes() == (b / "manifest.json").read_bytes())


def main() -> int:
    if not CORPUS.exists():
        print(f"missing {CORPUS}", file=sys.stderr)
        return 1
    root = pathlib.Path(tempfile.mkdtemp(prefix="corpus-size-axis-"))
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
