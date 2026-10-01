#!/usr/bin/env python3
"""TDD harness for `check`'s shape assertion (design rev 2, section 7 item 4).

"`check` extended to assert the shape fields it now declares, so a batch that claims 'one large
quoted-printable message at index 7' fails when index 7 is neither."

Two properties make this testable with NO Host, which is the point of checking it here rather than in
a window: the assertion is about the batch DIRECTORY and the manifest, both local, so it runs before
any network call. The test discriminates pass from fail without a ledger by looking for the report
marker, not by reading the exit code, because the exit code is also 1 when the Host is simply absent.

The version gate is asserted too: a `corpusVersion` 3 manifest has no shape claims, so it must SKIP
rather than fail. A check that demanded the new fields of an old batch would call a correct batch
broken, and one that silently skipped a v4 batch would let the claim go unverified.

Run from the repo root:
    PYTHONDONTWRITEBYTECODE=1 python3 tools/corpus/tests/test-check-shape.py
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

# The report marker the implementation must emit. Asserted by name so a future rename is a deliberate
# change to this harness rather than a silent pass.
MARKER = "SHAPE:"

# Port 9 is discard and nothing listens; a connection is refused at once, so `check` gets past the
# local shape pass quickly and then fails on the ledger. No Host is started, so this is not a take.
DEAD_URL = "http://127.0.0.1:9"

FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    if ok:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name}{(' :: ' + detail) if detail else ''}")
        FAILURES.append(name)


def generate(out: pathlib.Path, profile: str, *extra: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, "-B", str(CORPUS), "generate", "--seed", "77", "--count", "6",
         "--profile", profile, "--coverage", "full", "--out", str(out), *extra],
        cwd=str(REPO), capture_output=True, text=True,
    )


def run_check(manifest: pathlib.Path, keyfile: pathlib.Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, "-B", str(CORPUS), "check", "--base-url", DEAD_URL,
         "--key-file", str(keyfile), "--manifest", str(manifest)],
        cwd=str(REPO), capture_output=True, text=True,
    )


def make_batch(root: pathlib.Path, name: str) -> tuple[pathlib.Path, pathlib.Path, pathlib.Path]:
    """A fresh batch plus a placeholder key file. The key file holds NO secret: it is a throwaway."""
    batch = root / name
    result = generate(batch, "phishing", "--encoding-mix", "quoted-printable", "--size-mix", "large")
    if result.returncode != 0:
        raise RuntimeError(f"generate failed: {result.stderr[:200]}")
    keyfile = root / "placeholder-key"
    keyfile.write_text("not-a-key-placeholder\n", encoding="utf-8")
    return batch, batch / "manifest.json", keyfile


def test_an_honest_batch_produces_no_shape_report(root: pathlib.Path) -> None:
    batch, manifest, keyfile = make_batch(root, "honest")
    result = run_check(manifest, keyfile)
    combined = result.stdout + result.stderr
    check("an honest batch reports no SHAPE failure", MARKER not in combined,
          combined.strip().splitlines()[-1][:200] if combined.strip() else "(no output)")


def test_a_false_encoding_claim_is_reported(root: pathlib.Path) -> None:
    batch, manifest, keyfile = make_batch(root, "bad-encoding")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    data["messages"][0]["encoding"] = "plain"          # the bytes are quoted-printable
    manifest.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    result = run_check(manifest, keyfile)
    combined = result.stdout + result.stderr
    check("a false encoding claim is reported", MARKER in combined)
    check("the report names the file", "000.eml" in combined)
    check("the report names the claim", "plain" in combined)
    check("a shape failure exits non-zero", result.returncode != 0, f"rc={result.returncode}")


def test_a_false_size_claim_is_reported(root: pathlib.Path) -> None:
    batch, manifest, keyfile = make_batch(root, "bad-size")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    data["messages"][2]["size"] = "small"              # the bytes are large
    manifest.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    result = run_check(manifest, keyfile)
    combined = result.stdout + result.stderr
    check("a false size claim is reported", MARKER in combined)
    check("the report names the size file", "002.eml" in combined)


def test_both_claims_wrong_are_both_reported(root: pathlib.Path) -> None:
    """A check that stops at the first finding hides the rest of the batch's defects."""
    batch, manifest, keyfile = make_batch(root, "both-bad")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    data["messages"][0]["encoding"] = "plain"
    data["messages"][2]["size"] = "small"
    manifest.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    result = run_check(manifest, keyfile)
    combined = result.stdout + result.stderr
    check("both mismatches are reported, not just the first",
          "000.eml" in combined and "002.eml" in combined)


def test_a_version_3_manifest_is_skipped_not_failed(root: pathlib.Path) -> None:
    """An old batch makes no shape claim, so there is nothing to hold it to."""
    batch, manifest, keyfile = make_batch(root, "v3")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    data["corpusVersion"] = 3
    for message in data["messages"]:
        message.pop("encoding", None)
        message.pop("size", None)
    manifest.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    result = run_check(manifest, keyfile)
    combined = result.stdout + result.stderr
    check("a v3 manifest produces no shape failure", MARKER not in combined,
          combined.strip().splitlines()[-1][:200] if combined.strip() else "(no output)")


def test_a_v4_manifest_missing_the_field_is_reported(root: pathlib.Path) -> None:
    """Version 4 is what makes the field mandatory, so its absence is a defect and not a skip."""
    batch, manifest, keyfile = make_batch(root, "v4-missing")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    data["messages"][1].pop("encoding", None)
    manifest.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    result = run_check(manifest, keyfile)
    combined = result.stdout + result.stderr
    check("a v4 message with no encoding field is reported", MARKER in combined)
    check("the report names the file", "001.eml" in combined)


def test_an_unreadable_version_fails_closed(root: pathlib.Path) -> None:
    """A manifest is a text file anyone can edit, so the version may not be a number.

    Failing closed rather than crashing: a traceback is a poor report for a tool whose whole point is
    honest reporting, and an unreadable version must not be silently treated as "old, so skip".
    """
    batch, manifest, keyfile = make_batch(root, "badversion")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    data["corpusVersion"] = "not-a-number"
    manifest.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    result = run_check(manifest, keyfile)
    combined = result.stdout + result.stderr
    check("an unreadable corpusVersion does not traceback", "Traceback" not in combined,
          combined.strip().splitlines()[-1][:200] if combined.strip() else "(no output)")
    check("an unreadable corpusVersion fails closed", result.returncode != 0, f"rc={result.returncode}")
    check("the refusal names the version", "corpusVersion" in combined, combined.strip()[:200])


def test_an_escaping_entry_is_a_failure_not_a_read(root: pathlib.Path) -> None:
    """A manifest entry pointing outside the batch must FAIL, never be read and never be skipped.

    Found by an automated security review on the increment that added the shape pass: `entry["file"]`
    comes straight out of the manifest, so `batch_dir / entry["file"]` with `../../...` reads outside
    the batch. `seed` already refuses this shape (see the README's note on entry resolution) and the
    new pass did not carry the same guard, which is the ordinary way a guard fails to travel: it was
    written on one code path and the second path was added later without it.

    A skip would be the worse fix and is asserted against separately: a `check` that quietly ignores
    an escaping entry reports a clean batch for a manifest it never fully read.
    """
    # THE ESCAPE MUST REACH A FILE THAT EXISTS, or the test proves nothing. A first version of this
    # used `../../../../etc/passwd`, which from a temp directory overshoots and resolves to a path
    # that is absent, so the ORDINARY missing-file arm fired and every assertion below passed with no
    # guard in place: the needle did not fit the haystack's alphabet. The target here is a real file
    # one level above the batch, so an unguarded read genuinely succeeds and finds nothing to report.
    outside = root / "outside-the-batch.txt"
    outside.write_text("not a message, and not inside any batch\n", encoding="utf-8")

    batch, manifest, keyfile = make_batch(root, "escaping")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    data["messages"][0]["file"] = "../outside-the-batch.txt"
    manifest.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    check("the escape target really is outside and really exists",
          outside.is_file() and not str(outside).startswith(str(batch) + "/"),
          str(outside))

    result = run_check(manifest, keyfile)
    combined = result.stdout + result.stderr
    check("an escaping entry is reported", MARKER in combined)
    check("the report names it as an escape rather than as a missing file",
          "escapes" in combined and "no such file" not in combined, combined.strip()[:250])
    check("the report names the escaping entry", "outside-the-batch.txt" in combined,
          combined.strip()[:250])
    check("an escaping entry exits non-zero", result.returncode != 0, f"rc={result.returncode}")
    check("an escaping entry does not traceback", "Traceback" not in combined)

    # CONTROL for the other arm, in the same run: an entry that is INSIDE the batch and merely absent
    # must take the ORDINARY missing-file path, not the escape path. Without this, a guard that
    # reported "escapes" for every bad entry would pass the assertions above.
    control_batch, control_manifest, control_key = make_batch(root, "absent-inside")
    control = json.loads(control_manifest.read_text(encoding="utf-8"))
    control["messages"][0]["file"] = "does-not-exist.eml"
    control_manifest.write_text(json.dumps(control, indent=2) + "\n", encoding="utf-8")
    control_result = run_check(control_manifest, control_key)
    control_out = control_result.stdout + control_result.stderr
    check("CONTROL: an absent entry inside the batch does not read as an escape",
          "does-not-exist.eml" in control_out and "escapes" not in control_out,
          control_out.strip()[:250])


def main() -> int:
    if not CORPUS.exists():
        print(f"missing {CORPUS}", file=sys.stderr)
        return 1
    root = pathlib.Path(tempfile.mkdtemp(prefix="corpus-check-shape-"))
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
