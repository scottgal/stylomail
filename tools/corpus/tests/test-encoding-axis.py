#!/usr/bin/env python3
"""TDD harness for the encoding axis of design rev 2 (sections 3.4 and 3.7).

Design: the desktop-harness design rev 2. That doc is an UNTRACKED lane artifact
(`.styloagent/scratch/...` when the lane's scratch is present, so NOT available in a plain
checkout); the constraint this file depends on is restated below so the file stands alone.
2026-10-01 18:53. Section 3.4 defines `--encoding-mix plain|quoted-printable|rfc2047|mixed`; section
3.7 requires the manifest to record the drawn shape per message.

Run from the repo root:
    PYTHONDONTWRITEBYTECODE=1 python3 tools/corpus/tests/test-encoding-axis.py

`PYTHONDONTWRITEBYTECODE=1` is not optional: importing or running corpus.py from a script whose cwd
is `tools/corpus/` leaves a `__pycache__` there, which this lane has already had to remove twice.
Every subprocess below is run with `-B` for the same reason.

Exit 0 = every assertion held. Non-zero = the count of failures.
"""

from __future__ import annotations

import email
import email.header
import email.policy
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[3]
CORPUS = REPO / "tools" / "corpus" / "corpus.py"

FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    if ok:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name}{(' :: ' + detail) if detail else ''}")
        FAILURES.append(name)


def generate(out: pathlib.Path, *extra: str) -> subprocess.CompletedProcess:
    """Run `generate` with no Host and no model, so this is not a take."""
    return subprocess.run(
        [
            sys.executable, "-B", str(CORPUS), "generate",
            "--seed", "77", "--count", "6", "--profile", "phishing",
            "--coverage", "full", "--out", str(out), *extra,
        ],
        cwd=str(REPO), capture_output=True, text=True,
    )


def manifest_of(out: pathlib.Path) -> dict:
    return json.loads((out / "manifest.json").read_text(encoding="utf-8"))


def parts_of(raw: bytes):
    """Every leaf part of a message, decoded to text, plus the container headers."""
    msg = email.message_from_bytes(raw, policy=email.policy.default)
    leaves = []
    for part in msg.walk():
        if part.get_content_maintype() == "multipart":
            continue
        leaves.append(part)
    return msg, leaves


def test_unknown_encoding_mix_is_refused(root: pathlib.Path) -> None:
    """A refusal, not a default: an accidental encoding cannot be told from a planted one."""
    out = root / "bogus"
    result = generate(out, "--encoding-mix", "bogus")
    check("unknown --encoding-mix exits 2", result.returncode == 2,
          f"got rc={result.returncode} stderr={result.stderr.strip()[:200]}")
    check("unknown --encoding-mix writes no batch", not (out / "manifest.json").exists())


def test_default_matches_plain(root: pathlib.Path) -> None:
    """The axis must be additive: omitting the flag is `plain`, byte for byte."""
    a, b = root / "default", root / "plain"
    ra = generate(a)
    rb = generate(b, "--encoding-mix", "plain")
    check("no-flag run succeeds", ra.returncode == 0, ra.stderr.strip()[:200])
    check("explicit plain run succeeds", rb.returncode == 0, rb.stderr.strip()[:200])
    if ra.returncode or rb.returncode:
        return
    same = all(
        (a / f"{i:03d}.eml").read_bytes() == (b / f"{i:03d}.eml").read_bytes()
        for i in range(6)
    )
    check("omitted --encoding-mix is byte-identical to plain", same)


def test_quoted_printable_round_trips(root: pathlib.Path) -> None:
    """Quoted-printable must change WHICH BYTES carry the site, never whether the site exists."""
    plain_dir, qp_dir = root / "qp-plain", root / "qp"
    generate(plain_dir, "--encoding-mix", "plain")
    result = generate(qp_dir, "--encoding-mix", "quoted-printable")
    check("quoted-printable run succeeds", result.returncode == 0, result.stderr.strip()[:200])
    if result.returncode:
        return

    declared = 0
    soft_break = 0
    mismatched = []
    for i in range(6):
        raw = (qp_dir / f"{i:03d}.eml").read_bytes()
        _, leaves = parts_of(raw)
        text_leaves = [p for p in leaves if p.get_content_type() == "text/plain"]
        if not text_leaves:
            mismatched.append(f"{i:03d}: no text/plain leaf")
            continue
        for part in text_leaves:
            if part.get("Content-Transfer-Encoding", "").lower() == "quoted-printable":
                declared += 1
        if b"=\r\n" in raw:
            soft_break += 1

        plain_raw = (plain_dir / f"{i:03d}.eml").read_bytes()
        _, plain_leaves = parts_of(plain_raw)
        plain_text = [p for p in plain_leaves if p.get_content_type() == "text/plain"]
        want = plain_text[0].get_content().strip() if plain_text else None
        got = text_leaves[0].get_content().strip()
        if want != got:
            mismatched.append(f"{i:03d}: decoded text differs")

    check("every text/plain leaf declares quoted-printable", declared == 6, f"declared={declared}")
    check("a soft line break is present in every message", soft_break == 6, f"soft_break={soft_break}")
    check("decoded text equals the plain run's text", not mismatched, "; ".join(mismatched[:3]))


def test_rfc2047_subject_round_trips(root: pathlib.Path) -> None:
    """An 8-bit subject must be an encoded-word on the wire and decode to the same string."""
    plain_dir, rfc_dir = root / "rfc-plain", root / "rfc"
    generate(plain_dir, "--encoding-mix", "plain")
    result = generate(rfc_dir, "--encoding-mix", "rfc2047")
    check("rfc2047 run succeeds", result.returncode == 0, result.stderr.strip()[:200])
    if result.returncode:
        return

    encoded = 0
    not_ascii = []
    lost = []
    for i in range(6):
        raw = (rfc_dir / f"{i:03d}.eml").read_bytes()
        plain_raw = (plain_dir / f"{i:03d}.eml").read_bytes()
        wire_headers = raw.split(b"\r\n\r\n", 1)[0].decode("utf-8", "replace")
        wire_subject = [
            line for line in wire_headers.split("\r\n") if line.lower().startswith("subject:")
        ][0]
        if "=?" in wire_subject and "?=" in wire_subject:
            encoded += 1
        msg = email.message_from_bytes(raw, policy=email.policy.default)
        plain_msg = email.message_from_bytes(plain_raw, policy=email.policy.default)
        decoded = str(msg["Subject"])
        plain_subject = str(plain_msg["Subject"])
        # The variant must genuinely NEED the encoded-word. An ASCII subject wrapped in =?...?= is
        # legal and proves nothing: the encoding would be decorative. This is the assertion that
        # would have passed under a no-op implementation, which is why it is asserted separately
        # from the encoded-word check rather than folded into it.
        if decoded.isascii():
            not_ascii.append(f"{i:03d}: {decoded!r} is ASCII, so it never needed encoding")
        if plain_subject not in decoded:
            lost.append(f"{i:03d}: {plain_subject!r} not in {decoded!r}")

    check("every subject is an RFC 2047 encoded-word", encoded == 6, f"encoded={encoded}")
    check("the decoded subject is genuinely non-ASCII", not not_ascii, "; ".join(not_ascii[:3]))
    check("the plain subject survives inside the encoded one", not lost, "; ".join(lost[:3]))


def test_manifest_records_the_drawn_encoding(root: pathlib.Path) -> None:
    """Section 3.7: the drawn shape is recorded, not left for a consumer to infer from the bytes."""
    fixed = root / "fixed"
    mixed = root / "mixed"
    rf = generate(fixed, "--encoding-mix", "quoted-printable")
    rm = generate(mixed, "--encoding-mix", "mixed")
    check("fixed-mix run succeeds", rf.returncode == 0, rf.stderr.strip()[:200])
    check("mixed run succeeds", rm.returncode == 0, rm.stderr.strip()[:200])
    if rf.returncode or rm.returncode:
        return

    fixed_msgs = manifest_of(fixed)["messages"]
    mixed_msgs = manifest_of(mixed)["messages"]
    check("every message records `encoding`", all("encoding" in m for m in fixed_msgs),
          str([m.get("encoding") for m in fixed_msgs[:3]]))
    check("a fixed mix records that value everywhere",
          {m.get("encoding") for m in fixed_msgs} == {"quoted-printable"},
          str({m.get("encoding") for m in fixed_msgs}))
    check("`mixed` draws more than one value",
          len({m.get("encoding") for m in mixed_msgs}) > 1,
          str({m.get("encoding") for m in mixed_msgs}))
    check("drawn values are all known mixes",
          {m.get("encoding") for m in mixed_msgs} <= {"plain", "quoted-printable", "rfc2047"},
          str({m.get("encoding") for m in mixed_msgs}))


def test_the_axis_is_deterministic(root: pathlib.Path) -> None:
    """The standing rule in this lane: same inputs, byte-identical bytes, across processes."""
    a, b = root / "det-a", root / "det-b"
    ra = generate(a, "--encoding-mix", "mixed")
    rb = generate(b, "--encoding-mix", "mixed")
    if ra.returncode or rb.returncode:
        check("both determinism runs succeed", False,
              (ra.stderr or rb.stderr).strip()[:200])
        return
    same_bytes = all(
        (a / f"{i:03d}.eml").read_bytes() == (b / f"{i:03d}.eml").read_bytes() for i in range(6)
    )
    same_manifest = (a / "manifest.json").read_bytes() == (b / "manifest.json").read_bytes()
    check("two runs give byte-identical .eml", same_bytes)
    check("two runs give byte-identical manifest.json", same_manifest)


def main() -> int:
    if not CORPUS.exists():
        print(f"missing {CORPUS}", file=sys.stderr)
        return 1
    root = pathlib.Path(tempfile.mkdtemp(prefix="corpus-encoding-axis-"))
    try:
        for name, fn in sorted(globals().items()):
            if name.startswith("test_") and callable(fn):
                print(f"\n{name}")
                try:
                    fn(root)
                except Exception as exc:  # a crash is a failure, never a silent skip
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
