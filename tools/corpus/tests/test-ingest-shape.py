#!/usr/bin/env python3
"""TDD harness for the reconstituted (`ingest`) path against the version 4 shape fields.

WHY THIS FILE EXISTS, and it is a regression rather than a feature. Adding the per-message
`encoding`/`size` fields and making `check` require them at `corpusVersion` 4 broke a path I had not
touched: `cmd_ingest` writes `corpusVersion: CORPUS_VERSION` (now 4) but builds its message entries
INLINE, so it wrote no shape fields and `check` would have failed every message of every reconstituted
batch with "requires `encoding`, and this message has none".

That is a false failure, and it is the worst kind for this lane: `check` reporting a defect in a
correct batch is how a real defect gets ignored later. It was found by reading what the OTHER writer
of the manifest writes, not by reasoning about the change.

The fix is NOT to loosen the gate. A reconstituted message's shape is real and verifiable from its own
bytes, so `ingest` records what its builder actually produced, and the claim is checked the same way
every other shape claim is.

Run from the repo root (the dataset must be present; the test SKIPS if it is not, and says so, rather
than reporting a pass it did not earn):
    PYTHONDONTWRITEBYTECODE=1 python3 tools/corpus/tests/test-ingest-shape.py
"""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[3]
CORPUS = REPO / "tools" / "corpus" / "corpus.py"
DATASETS = pathlib.Path.home() / "Downloads"

MARKER = "SHAPE:"
DEAD_URL = "http://127.0.0.1:9"

FAILURES: list[str] = []
CHECKS: list[str] = []
SKIPPED = False


def check(name: str, ok: bool, detail: str = "") -> None:
    CHECKS.append(name)
    if ok:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name}{(' :: ' + detail) if detail else ''}")
        FAILURES.append(name)


def ingest(out: pathlib.Path, limit: int = 2) -> subprocess.CompletedProcess:
    env = dict(os.environ, STYLOMAIL_CORPUS_DIR=str(DATASETS))
    return subprocess.run(
        [sys.executable, "-B", str(CORPUS), "ingest", "--source-archive", "emails.zip",
         "--limit", str(limit), "--seed", "77", "--out", str(out)],
        cwd=str(REPO), capture_output=True, text=True, env=env,
    )


def run_check(manifest: pathlib.Path, keyfile: pathlib.Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, "-B", str(CORPUS), "check", "--base-url", DEAD_URL,
         "--key-file", str(keyfile), "--manifest", str(manifest)],
        cwd=str(REPO), capture_output=True, text=True,
    )


def test_reconstituted_messages_declare_their_shape(root: pathlib.Path) -> None:
    global SKIPPED
    if not (DATASETS / "emails.zip").is_file():
        print(f"  SKIP dataset absent: {DATASETS / 'emails.zip'}")
        SKIPPED = True
        return

    out = root / "ingest"
    result = ingest(out)
    check("ingest run succeeds", result.returncode == 0, (result.stderr or result.stdout).strip()[:300])
    if result.returncode:
        return

    manifest = out / "manifest.json"
    check("ingest wrote a manifest", manifest.exists())
    if not manifest.exists():
        return
    data = json.loads(manifest.read_text(encoding="utf-8"))
    check("the batch is reconstituted", data.get("source") is not None, str(data.get("source")))
    check("the version is 4, so the shape fields are required",
          data.get("corpusVersion") == 4, str(data.get("corpusVersion")))

    messages = data["messages"]
    # POPULATION CONTROL: an absence of failures over zero messages would be free.
    check("CONTROL: the manifest has messages to check", len(messages) > 0, str(len(messages)))
    missing = [m["file"] for m in messages if "encoding" not in m or "size" not in m]
    check("every reconstituted message records encoding and size", not missing, str(missing))

    # `check` must accept it. Before the fix this reported SHAPE failures for every message.
    keyfile = root / "placeholder-key"
    keyfile.write_text("not-a-key-placeholder\n", encoding="utf-8")
    checked = run_check(manifest, keyfile)
    combined = checked.stdout + checked.stderr
    check("check reports NO shape failure on a reconstituted batch", MARKER not in combined,
          combined.strip().splitlines()[-1][:250] if combined.strip() else "(no output)")


def test_the_recorded_shape_is_true_of_the_bytes(root: pathlib.Path) -> None:
    """The recorded value must be what the bytes are, not a placeholder that happens to pass."""
    global SKIPPED
    if SKIPPED or not (DATASETS / "emails.zip").is_file():
        return
    out = root / "ingest-true"
    if ingest(out).returncode:
        check("second ingest run succeeds", False)
        return
    data = json.loads((out / "manifest.json").read_text(encoding="utf-8"))
    raw = (out / data["messages"][0]["file"]).read_bytes()
    check("a reconstituted message carries no encoded-word subject", b"=?" not in raw.split(b"\r\n\r\n")[0])
    check("a reconstituted message declares `plain`", data["messages"][0]["encoding"] == "plain",
          str(data["messages"][0]["encoding"]))
    check("a reconstituted message declares `small`", data["messages"][0]["size"] == "small",
          str(data["messages"][0]["size"]))
    check("the top-level mixes say no axis was DRAWN",
          data.get("encodingMix") is None and data.get("sizeMix") is None,
          f"encodingMix={data.get('encodingMix')!r} sizeMix={data.get('sizeMix')!r}")


def main() -> int:
    if not CORPUS.exists():
        print(f"missing {CORPUS}", file=sys.stderr)
        return 1
    root = pathlib.Path(tempfile.mkdtemp(prefix="corpus-ingest-shape-"))
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
    if SKIPPED:
        print("SKIPPED: the operator dataset is not on disk, so nothing was verified")
        return 2
    if FAILURES:
        print(f"{len(CHECKS)} assertion(s) run, {len(FAILURES)} FAILED: {FAILURES}")
        return len(FAILURES)
    print(f"{len(CHECKS)} assertion(s) run, all held")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
