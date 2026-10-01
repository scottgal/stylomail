#!/usr/bin/env python3
"""TDD harness for the dense body-shape axis (`--body-shapes`).

WHAT THIS IS FOR. `nimble-` measured six body shapes and found the expansion runs from 2.808x to
7.017x with the message's CONTENT as the only variable, so a corpus's safety is a property of the
corpus and not of the setting alone. This axis makes that property DECLARABLE, so a batch can say
which shapes it drew instead of leaving a consumer to infer density from the bytes.

TWO PROPERTIES MAKE THIS TESTABLE WITH NO HOST AND NO MODEL, which is why it is checked here rather
than in a window: the axis is a transform of the plan's own text, and the manifest is local. Nothing
in this file starts a Host, claims a slot, or reads a ledger.

WHAT IT DELIBERATELY DOES NOT ASSERT. It does not assert a token count, an expansion, or that any
message would be refused by the provider. Those are the Host's evaluation on a run's own model and are
read back through a run, never predicted here. The claim this file tests is narrower and checkable:
the body IS the declared shape, and the manifest says which shape it drew.

Run from the repo root:
    PYTHONDONTWRITEBYTECODE=1 python3 tools/corpus/tests/test-body-shapes.py
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

# The six, in the order the implementation declares them. Asserted by name so that renaming one is a
# deliberate change to this harness rather than a silent pass.
SHAPES = ("prose", "base64ish", "mixed", "randomcase", "punct", "hexish")

# The alphabet each shape is DEFINED by, for the two whose definition is exact. `hexish` draws only
# hexadecimal and `base64ish` only the base64 alphabet; the other four are prose-like or mixed and a
# set membership would be a claim about the generator rather than about the shape.
HEXISH_ALPHABET = re.compile(r"\A[0-9a-f ]*\Z")
BASE64_ALPHABET = re.compile(r"\A[A-Za-z0-9+/=]*\Z")

# A shape is a 2000-character body BY DEFAULT, and the number is a MEASUREMENT rather than the
# adapter's budget. The adapter's budget is `MaxBodyCharacters` 2500 (`NimbleOptions.cs:229`) and
# `check` still refuses anything above it, but a run through the shipping path at the pinned window
# showed the FIT's cut is a size rule: 2000 characters arrives un-cut, 2500 is shortened. So the
# default is the largest length MEASURED to arrive un-cut, and the two numbers answer different
# questions. Measured at three messages per arm, `benign`, `--coverage full`, twelve questions.
EXPECTED_BODY_CHARACTERS = 2000

# The report marker `check` emits for a shape failure, and a URL nothing listens on so `check` gets
# past the local shape pass and then fails on the ledger. No Host is started, so this is not a take.
MARKER = "SHAPE:"
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


def body_of(raw: bytes) -> str:
    """The text/plain part's body, with line breaks removed.

    A first version of this helper split on the first blank line, which is correct only for a
    single-part message and silently returned the MULTIPART STRUCTURE of every html-carrying message
    this harness generates: the alphabet assertion then failed against `--alt\\r\\nContent-Type`, a
    string no shape could ever satisfy. Parsing is the fix, and the decoding is why the assertion is
    about the authored text rather than about a transfer encoding of it.

    Line breaks are removed because the generated MIME hard-wraps a long body. A unit here contains
    spaces and no newlines, so removing them changes nothing about which alphabet the body is drawn
    from, and it lets the length claim be a claim about the AUTHORED characters.
    """
    message = email.message_from_bytes(raw, policy=email.policy.default)
    if message.is_multipart():
        for part in message.walk():
            if part.get_content_type() == "text/plain":
                payload = part.get_content()
                return str(payload).replace("\r\n", "").replace("\n", "")
        raise AssertionError("no text/plain part in a multipart message")
    return str(message.get_content()).replace("\r\n", "").replace("\n", "")


def declared(manifest: pathlib.Path) -> list[dict]:
    data = json.loads(manifest.read_text(encoding="utf-8"))
    return data["messages"]


def test_the_default_declares_no_shape(root: pathlib.Path) -> None:
    """ABSENCE IS THE CLAIM: a batch that drew no shapes says nothing about shapes."""
    batch = root / "off"
    result = generate(batch, "benign")
    check("generate with no --body-shapes succeeds", result.returncode == 0, result.stderr[:200])
    messages = declared(batch / "manifest.json")
    check("no message declares a bodyShape by default",
          all("bodyShape" not in m for m in messages),
          str([m.get("bodyShape") for m in messages])[:120])


def test_all_draws_each_of_the_six(root: pathlib.Path) -> None:
    batch = root / "all"
    result = generate(batch, "benign", "--body-shapes", "all")
    check("generate --body-shapes all succeeds", result.returncode == 0, result.stderr[:300])
    messages = declared(batch / "manifest.json")
    drawn = [m.get("bodyShape") for m in messages]
    check("every message declares a shape", all(d is not None for d in drawn), str(drawn))
    check("`all` draws each of the six exactly once", sorted(drawn) == sorted(SHAPES), str(drawn))


def test_each_body_is_the_shape_it_declares(root: pathlib.Path) -> None:
    """The load-bearing assertion: a declared shape is a claim about the BYTES, not about a field."""
    batch = root / "alphabet"
    result = generate(batch, "benign", "--body-shapes", "all")
    if result.returncode != 0:
        check("generate for the alphabet pass succeeds", False, result.stderr[:300])
        return
    bodies: dict[str, str] = {}
    for entry in declared(batch / "manifest.json"):
        shape = entry.get("bodyShape")
        bodies[shape] = body_of((batch / entry["file"]).read_bytes())

    hexish = bodies.get("hexish", "")
    check("hexish draws only hexadecimal and spaces", bool(HEXISH_ALPHABET.match(hexish)),
          f"{len(hexish)} chars, first foreign={hexish.strip()[:40]!r}")

    base64ish = bodies.get("base64ish", "")
    check("base64ish draws only the base64 alphabet", bool(BASE64_ALPHABET.match(base64ish)),
          f"{len(base64ish)} chars, first foreign={base64ish.strip()[:40]!r}")
    # DISCRIMINATION, because hexish is a SUBSET of the base64 alphabet and the test above would pass
    # on a batch that drew hexish for both. The base64 unit carries `+`, `/`, `=` and uppercase.
    check("base64ish is not hexish under another name",
          bool(re.search(r"[A-Z+/=]", base64ish)) and not bool(HEXISH_ALPHABET.match(base64ish)),
          f"uppercase/+/= present={bool(re.search(r'[A-Z+/=]', base64ish))}")

    prose = bodies.get("prose", "")
    check("prose is not an alphabet soup", bool(re.search(r"[a-z]{3,} ", prose)),
          prose[:60])

    check("the six declared bodies are pairwise different",
          len(set(bodies.values())) == 6, f"{len(set(bodies.values()))} distinct of {len(bodies)}")


def test_the_body_is_one_character_under_the_turn_limit(root: pathlib.Path) -> None:
    batch = root / "length"
    result = generate(batch, "benign", "--body-shapes", "all")
    if result.returncode != 0:
        check("generate for the length pass succeeds", False, result.stderr[:300])
        return
    for entry in declared(batch / "manifest.json"):
        body = body_of((batch / entry["file"]).read_bytes())
        if len(body) != EXPECTED_BODY_CHARACTERS or entry["turnCharacters"] != EXPECTED_BODY_CHARACTERS:
            check(f"{entry['file']} is {EXPECTED_BODY_CHARACTERS} characters", False,
                  f"body={len(body)} turnCharacters={entry['turnCharacters']}")
            return
    check(f"every drawn body is exactly {EXPECTED_BODY_CHARACTERS} characters and the turn agrees",
          True)


def test_a_profile_whose_facts_ride_its_text_is_refused(root: pathlib.Path) -> None:
    """The guard, and it is the fail-closed direction: a profile that has not said it is safe is not."""
    batch = root / "refused"
    result = generate(batch, "phishing", "--body-shapes", "all")
    check("--body-shapes on a text-bearing profile is refused", result.returncode != 0,
          f"rc={result.returncode}")
    combined = result.stdout + result.stderr
    check("the refusal names the profile", "phishing" in combined, combined[-200:])
    check("the refusal writes NO batch", not (batch / "manifest.json").exists(),
          f"manifest exists: {(batch / 'manifest.json').exists()}")
    check("the refusal writes no message either",
          not batch.exists() or not list(batch.glob("*.eml")),
          str(sorted(p.name for p in batch.glob('*.eml')))[:120] if batch.exists() else "")


def test_a_single_named_shape_can_be_forced(root: pathlib.Path) -> None:
    batch = root / "forced"
    result = generate(batch, "benign", "--body-shapes", "hexish")
    check("generate --body-shapes hexish succeeds", result.returncode == 0, result.stderr[:300])
    drawn = [m.get("bodyShape") for m in declared(batch / "manifest.json")]
    check("a named shape is drawn on every message", drawn == ["hexish"] * 6, str(drawn))


def test_the_body_length_is_settable(root: pathlib.Path) -> None:
    """The ceiling is a PARAMETER, because the fit's cut turned out to be a size rule.

    A 2500-character body is shortened and a ~450-character one is not, so the threshold lies between
    and no arm could sit there while the length was a constant. This is the arm that resolves it, and
    it is also what a consumer wants when asking for "a dense body of N characters".
    """
    batch = root / "len800"
    result = generate(batch, "benign", "--body-shapes", "hexish", "--body-characters", "800")
    check("generate --body-characters succeeds", result.returncode == 0, result.stderr[:300])
    messages = declared(batch / "manifest.json")
    wrong = [m["file"] for m in messages if m["turnCharacters"] != 800]
    check("every manifest turn is exactly 800 characters", not wrong, str(wrong))
    body = body_of((batch / messages[0]["file"]).read_bytes())
    check("and the BYTES agree with the manifest", len(body) == 800, f"{len(body)}")
    check("the body is still the declared shape", bool(HEXISH_ALPHABET.match(body)),
          body[:40])

    # CONTROL: the default is untouched, or a settable length would have silently moved the family's
    # existing fixtures while appearing to add a knob.
    default_batch = root / "len-default"
    generate(default_batch, "benign", "--body-shapes", "hexish")
    got = declared(default_batch / "manifest.json")[0]["turnCharacters"]
    check("CONTROL: the default length is unchanged", got == EXPECTED_BODY_CHARACTERS,
          f"{got} != {EXPECTED_BODY_CHARACTERS}")


def run_check(manifest: pathlib.Path, keyfile: pathlib.Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, "-B", str(CORPUS), "check", "--base-url", DEAD_URL,
         "--key-file", str(keyfile), "--manifest", str(manifest)],
        cwd=str(REPO), capture_output=True, text=True,
    )


def test_a_shape_outside_the_closed_six_is_reported(root: pathlib.Path) -> None:
    """`check` is the only reader of a manifest that can refuse a claim, so it needs its own arm."""
    batch = root / "checked"
    result = generate(batch, "benign", "--body-shapes", "all")
    if result.returncode != 0:
        check("generate for the check pass succeeds", False, result.stderr[:300])
        return
    manifest = batch / "manifest.json"
    keyfile = root / "placeholder-key"
    keyfile.write_text("not-a-key-placeholder\n", encoding="utf-8")

    # CONTROL FIRST, because an absence assertion with no control proves nothing: an honest batch
    # must produce no shape report, and if it did the tampered one below would prove nothing either.
    honest = run_check(manifest, keyfile)
    honest_out = honest.stdout + honest.stderr
    check("CONTROL: an honest shaped batch reports no SHAPE failure", MARKER not in honest_out,
          honest_out.strip().splitlines()[-1][:160] if honest_out.strip() else "(no output)")

    data = json.loads(manifest.read_text(encoding="utf-8"))
    target = data["messages"][3]["file"]
    data["messages"][3]["bodyShape"] = "cryptic"
    manifest.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")

    tampered = run_check(manifest, keyfile)
    tampered_out = tampered.stdout + tampered.stderr
    check("a shape outside the closed six is reported", MARKER in tampered_out,
          tampered_out.strip().splitlines()[-1][:160] if tampered_out.strip() else "(no output)")
    check("the report names the file", target in tampered_out, target)
    check("the report names the claim", "cryptic" in tampered_out)
    check("a shape failure exits non-zero", tampered.returncode != 0, f"rc={tampered.returncode}")


def main() -> int:
    if not CORPUS.exists():
        print(f"missing {CORPUS}", file=sys.stderr)
        return 1
    root = pathlib.Path(tempfile.mkdtemp(prefix="corpus-body-shapes-"))
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
