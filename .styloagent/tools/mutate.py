#!/usr/bin/env python3
"""Shared mutation harness — one hardened implementation, not five.

WHY THIS EXISTS
---------------
A safety test that has never been seen failing is not evidence of anything. Introduce a mutation
that breaks the behaviour, confirm a test goes RED, restore. A mutation that produces no red test
is a COVERAGE GAP: that behaviour is unguarded, and any test whose name claims to cover it is
passing for some other reason.

    python3 .styloagent/tools/mutate.py            # every lane
    python3 .styloagent/tools/mutate.py queue      # one lane
    python3 .styloagent/tools/mutate.py queue BC   # selected mutations (by leading letter)

HOW A LANE DECLARES ITS MUTATION SET
------------------------------------
Add `.styloagent/tools/mutations/<prefix>.py` exporting `PROJECT` (the test csproj) and `MUTATIONS`,
a list of `(name, file, old_text, new_text)` with an optional 5th element naming the test that
*claims* to cover it.

**One file per lane, discovered at runtime — never a shared list.** A single module-level list that
every lane edits is a collision hazard in two ways: five agents editing one list conflict, and worse,
a lane rewriting it can silently drop another lane's entries. After that a sweep reports a clean run
with three lanes' mutations gone, which is a false all-clear produced by the tooling itself.

  * `old_text` must appear in the file EXACTLY ONCE, or the entry is INVALID.
  * `new_text` must actually change behaviour. A no-op replacement is INVALID, never a verdict:
    it reports GAP while guarding nothing.
  * Mutations must still COMPILE. "Delete this state change", "return the wrong enum member",
    "skip this sweep" beat rewrites. A mutation that removes a method's only use of instance state
    stops compiling in this repo (CA1822 is an error), which is a mutation-design problem, not a
    finding.

THE VERDICT IS THREE-WAY, NOT TWO
---------------------------------
Running the *full* suite finds attribution but has a blind spot of its own: it reports CAUGHT when
*some* test went red, even if the test whose name claims that behaviour did not. That is the very
failure mode this harness exists to detect, one level up — so name the claiming test and the
harness compares:

  * **CLAIMED**  — the named test went red. The claim is verified.
  * **ELSEWHERE** — other tests went red, the named one did not. The behaviour is guarded, but NOT
    by the test that claims it. Investigate: either a redundant guard, or a claim that is untested.
  * **GAP**      — nothing went red. Toothless.
  * **INCONCLUSIVE** — did not compile, or exceeded the timeout. Never a pass.
  * **INVALID**  — anchor missing/ambiguous, or the replacement is a no-op. Never a pass.

THE CONVENTION: ANY SWEEP HARNESS TAKES THE LOCK
-----------------------------------------------
**Committed or ad-hoc, in `.styloagent/tools/` or in `/tmp` — every harness that mutates source takes
`.styloagent/tools/.mutation-sweep.lock` before its first mutation and releases it when it finishes.**

Use `.styloagent/tools/sweep-lock.sh with <your-command>`; it acquires, runs, and releases on
success, failure, Ctrl-C or SIGTERM. Prefer it over hand-rolled acquire/release — the failure mode of
a hand-rolled pair is a harness that exits early and leaves the lock behind, blocking every other
sweep until somebody works out why.

**This is a convention and not merely a mechanism, on purpose.** An earlier version of this ruling
covered only this file, and that was not enough: a lane ran mutation rounds from
`/tmp/assess-mutation-round*.sh` — ephemeral, outside the repository, and therefore **invisible to
anyone auditing for sweep tools.** A bystander cannot check for a tool they cannot see, so the lock
is how a harness announces itself rather than something we hope to find.

This file takes the lock too, even though its isolation means it does not need it — so that **one
signal covers every harness**, and "is the lock held?" remains a complete answer.

SWEEPS RUN IN AN ISOLATED COPY — AND WHY THAT IS NOT ABOUT THIS LANE
----------------------------------------------------------------------
The sweep runs against a filesystem copy of the tree, never in place. **This exists because the
completion gate is fleet-wide, not because sweeps are dangerous to their own lane.**

Mutating shared source in place is, from any other lane's point of view, indistinguishable from
*their* code being broken. That is exactly what happened: `access-` ran the completion gate, saw
Queue fail for a reason that had nothing to do with Queue, and concluded the suite was
non-deterministic — a false finding against correct work, recorded as a doubt about the durability
claims. `dotnet test StyloMail.slnx` is now how every lane certifies, so an in-place sweep makes the
fleet's verification instrument lie at the precise moment it is used.

The lock file and stale-`.bak` check remain, but they are no longer the mitigation — they are the
**detector for the one failure isolation cannot prevent: a sweep violating its own isolation.**
Defence in depth, and cheap.

`git worktree` would be the better form. It is blocked: the repository has no baseline commit, so
there is nothing to branch from. A filesystem copy is the equivalent today; move to worktrees if a
baseline commit is ever authorised.

THE FIVE GUARDS, AND WHAT BREAKS WITHOUT EACH
---------------------------------------------
1. os.utime AFTER RESTORE, and a PRE-RUN MTIME GUARD before each run.
   `shutil.copy2` preserves the SOURCE mtime, so a restored file can look OLDER than the binary
   built from the mutation. MSBuild then skips the rebuild and the next run silently executes the
   MUTATED binary. The pre-run guard prints source vs binary mtime and refuses to proceed, which
   stops you BEFORE a result is formed. Keep both: the post-sweep green run is the backstop.

2. A TIMEOUT ON EVERY RUN. A mutation that causes a hang would otherwise block the sweep forever.
   A hang is INCONCLUSIVE, never "caught". Corollary for the lane: no unbounded loop in a test.
   A `while (true)` drain turns "stopped making progress" into a hang, which blocks the run and
   reports nothing; bound it and throw on exceeding the bound.

3. STARTUP REFUSAL ON A STALE .bak, plus restore on SIGINT/SIGTERM and atexit.
   Being killed mid-iteration leaves a mutation applied. That tree LOOKS clean — the source parses,
   most tests pass — which makes it the worst state to resume from. Only SIGKILL escapes every
   mechanism, and that is what the startup check is for.

4. POST-SWEEP GREEN RUN. Restore everything, re-run the untouched suite, require green before
   believing any result above. Without it a mutation can be credited as "caught" by the PREVIOUS
   mutation's lingering binary — a false positive, which is worse than a false negative because it
   makes an unguarded behaviour look guarded.

5. ANALYZERS ARE ERRORS IN THIS REPO. A mutation that fails to build proves nothing. `CA*` and
   `IDE*` diagnostics count as build failures alongside `error CS`.

THE RECURRING SHAPE: A GUARD WHOSE CONDITION CANNOT BE FALSE
------------------------------------------------------------
This is the single defect this project keeps producing, and it has now been observed at **three
different levels** — each time producing confidence rather than a signal:

  1. **In test assertions.** A "clamped to 200" claim tested with 5 items; a "counts terminal
     payloads too" claim tested only on non-terminal rows. Both pass whether the behaviour holds or
     not. A claim about a ceiling cannot be tested below the ceiling.
  2. **In verdict extraction.** The `[FAIL]` regex could not match a parameterised `[Theory]` line,
     so failed-name extraction returned empty while the summary count was non-zero — and the branch
     order fell through to a confident `ELSEWHERE`. My lane had no theories and was right by luck.
  3. **In this harness's own self-check.** The restore verification compared the file against the
     *mutated* text, i.e. against itself. It could never fire.

**The rule:** when you add a guard, ask what input makes it fail — and if you cannot name one,
it is decoration. Then check it fails. A verdict branch that has never been seen firing, a restore
check that has never been seen mismatch, an assertion whose falsifying case you cannot construct:
all of them are the thing this harness exists to find, and a verifier is not exempt from being
verified.

A corollary worth applying across lanes: **a defect that shows up only where two lanes differ is
invisible from inside either one.** My lane could not have found (2) — it needed a lane whose
fixtures are parameterised. Same insight as "build the solution, not just your project", one layer up.

RECOGNISING A STALE BINARY — THE SYMPTOM
----------------------------------------
**Tests failing with values the source provably cannot produce.**

This inverts the usual instinct. A surprising red normally means "investigate the code", and here
that instinct is exactly wrong: the code that RAN is not the code you are READING. If you find
yourself re-reading SQL, dumping raw rows and disabling passes to explain an impossible value,
stop and check the binary's mtime.
"""
import atexit
import importlib.util
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
from pathlib import Path

# No __pycache__: this directory is shared coordination material, not a Python project, and a
# stray artifact in it is one more thing for someone else to trip over.
sys.dont_write_bytecode = True

TOOLS = Path(__file__).resolve().parent

# The real repository root, used for the lock and for cleanup reporting.
SOURCE_ROOT = TOOLS.parents[1]

# Where the sweep actually runs — set by main() to an isolated copy.
#
# **Deliberately None, not SOURCE_ROOT.** Defaulting to the shared tree makes the SAFE value opt-in
# and the unsafe one the default: any entry point that skips main() — a wrapper script, a REPL, an
# import-and-call, a refactor that reaches a sweep helper directly — would silently mutate the shared
# tree again. That is the same bug re-entering through the initialiser instead of the write.
#
# Found by `access-`, who read the module rather than trusting the fix, and noticed that safety
# currently depended on main() having run.
RUN_ROOT = None
LANES_DIR = TOOLS / "mutations"


def run_root():
    """The tree the sweep operates on. Raises rather than defaulting to the shared tree."""
    if RUN_ROOT is None:
        raise RuntimeError(
            "RUN_ROOT is unset — the sweep was not set up through main(). Refusing to continue: "
            "the only safe default would be an isolated copy, and falling back to the shared tree is "
            "the hazard this tool exists to prevent.")
    return RUN_ROOT

RUN_TIMEOUT = int(os.environ.get("MUTATE_TIMEOUT", "180"))

ENV = {**os.environ,
       "DOTNET_ROOT": "/usr/local/share/dotnet",
       "PATH": "/usr/local/share/dotnet:" + os.environ.get("PATH", "")}

_restore = None


def _restore_if_active():
    global _restore
    if _restore:
        fn, _restore = _restore, None
        fn()
    LOCK.unlink(missing_ok=True)
    remove_isolated_copy()


def _on_signal(signum, _frame):
    print(f"\n!!! signal {signum} — restoring before exit", flush=True)
    _restore_if_active()
    sys.exit(130)


# Covers normal exit, an uncaught exception and sys.exit(), which signal handlers alone do not.
atexit.register(_restore_if_active)


def as_edits(old, new):
    """Normalise a mutation's replacement into a list of (old, new) pairs.

    `old`/`new` may each be a single string, or equal-length lists for a mutation that needs more
    than one edit — declaring a field and then using it cannot be expressed as one replacement, and
    without this such a mutation has to be left out of the lane entirely, leaving that test's teeth
    resting on a recorded run instead of a reproducible entry. That is the thing this harness exists
    to avoid, so the expressiveness belongs here.
    """
    if isinstance(old, list) or isinstance(new, list):
        if not (isinstance(old, list) and isinstance(new, list) and len(old) == len(new)):
            raise ValueError("multi-edit mutations need `old` and `new` lists of equal length")
        return list(zip(old, new))
    return [(old, new)]


LOCK = TOOLS / ".mutation-sweep.lock"

LOCK_EXPLANATION = """\
A mutation sweep is running RIGHT NOW and is editing source files in place.

While this file exists, `dotnet test` in this repository can fail for reasons that
have nothing to do with anyone's code: the sweep applies a mutation, builds, runs,
and restores. A concurrent test run sees the mutated source.

If you are running tests, wait for this file to disappear and re-run. If you are
seeing intermittent failures with no explanation, check whether this file exists
before concluding your suite is flaky.

This file is removed when the sweep ends, including on Ctrl-C. If it is present and
no sweep is running, a sweep was SIGKILLed: check for stray *.bak files and restore
from them.

*** THE LOCK IS ABSENT IN THE ONE CASE THAT MATTERS MOST. ***

SIGKILL cannot be handled, so a SIGKILLed sweep leaves mutated source, a .bak beside
it, and NO lock. A bystander following "wait for the lock to disappear" would see no
lock, conclude the tree is clean, and believe a failure that is not real.

So the check is BOTH signals:

    ls .styloagent/tools/.mutation-sweep.lock   # sweep running now
    find src -name '*.bak'                      # sweep was killed, mutation still applied

Either one means: do not trust a failure until the tree is confirmed clean. The .bak
is the only surviving signal in the SIGKILL case, and the person confused by the
failure is usually not the person who will run the next sweep.
"""


_copy_root = None


def make_isolated_copy():
    """Copy the tree somewhere private and run the whole sweep there.

    <b>Why this exists.</b> The sweep mutates source files. Doing that in place is indistinguishable
    from any other lane's point of view from *that lane's code being broken* — a `dotnet test`
    running at the same moment sees a mutated tree and reports failures that belong to this tool.
    That is not hypothetical: it produced a false "your suite is flaky" report against the queue,
    and it made the fleet-wide completion gate (`dotnet test StyloMail.slnx`) lie for every lane at
    the moment they were certifying.

    The lock file makes the hazard diagnosable, but only for someone who already knows to look in
    `.styloagent/tools/` — and a bystander running the gate has no reason to. **Detection does not
    fix a mechanism whose effects appear far from its cause; isolation does.**

    `git worktree` would be the better form and is blocked: the repository has no baseline commit,
    so there is nothing to branch from. A filesystem copy is the equivalent today.

    `obj/` and `bin/` are excluded — they are build output, and copying them would be slower and
    could carry a stale binary across, which is the very thing the mtime guard exists to catch.
    """
    global _copy_root

    _copy_root = Path(tempfile.mkdtemp(prefix="stylomail-sweep-"))
    target = _copy_root / "repo"

    shutil.copytree(
        SOURCE_ROOT, target, symlinks=True,
        ignore=shutil.ignore_patterns(".git", "obj", "bin", "__pycache__", ".DS_Store", "TestResults"),
        dirs_exist_ok=False)

    return target


def remove_isolated_copy():
    global _copy_root
    if _copy_root is not None:
        shutil.rmtree(_copy_root, ignore_errors=True)
        _copy_root = None


def acquire_lock():
    """Refuse to run two sweeps at once, and make the hazard visible to everyone else."""
    if LOCK.exists():
        print(f"!!! refusing to start: {LOCK.name} exists — another sweep is running, or a")
        print("    previous one was SIGKILLed. Do not run two sweeps concurrently: they would")
        print("    interleave mutations and attribute each other's failures.")
        return False

    LOCK.write_text(LOCK_EXPLANATION)
    return True


def load_lane(name):
    """Import mutations/<name>.py without needing it to be a package."""
    spec = importlib.util.spec_from_file_location(f"lane_{name}", LANES_DIR / f"{name}.py")
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot load lane '{name}'")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def binary_mtime(project):
    bin_dir = (run_root() / project).parent / "bin"
    if not bin_dir.exists():
        return None
    stamps = [p.stat().st_mtime for p in bin_dir.rglob("*.dll")]
    return max(stamps) if stamps else None


def pre_run_mtime_guard(path, project):
    """Guard 1 (strong form): refuse if compiled output is newer than the source."""
    binary = binary_mtime(project)
    if binary is None:
        return True
    source = path.stat().st_mtime
    if source <= binary:
        print(f"    !!! PRE-RUN GUARD: {path.name} is older than the build output.")
        print(f"        source={source:.3f} binary={binary:.3f} — MSBuild would skip the rebuild")
        print("        and this run would execute a STALE binary. Touch the source and re-run.")
        return False
    return True


TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def parse_trx(path):
    """Failed test names and count, from structured XML rather than console text.

    **Console parsing cannot do this.** A `[Theory]` failure line carries its parameters between the
    method name and `[FAIL]`:

        ...BytesThatAreNotAMessage_AreRejectedAsMalformed(text: "Hello,\\n\\nHow are you?",
                                                          expectedReason: "no-header-fields") [FAIL]

    Those parameters contain spaces, commas and escaped newlines, so a name-extraction regex either
    matches nothing at all — leaving `failed` empty while the summary count is non-zero, which then
    falls through to ELSEWHERE and reports a *false* verdict about a theory that went red exactly as
    claimed — or grabs a fragment of the parameters as a test name. Found by `mime-` on R10/R13.

    The TRX logger emits the method name as one escaped attribute, so there is nothing to parse
    around. This is the same defect the harness exists to find: a mechanism reporting a verdict its
    data does not support.
    """
    if not path.exists():
        return set(), -1

    # ElementTree rather than defusedxml: the input is a TRX this harness just asked `dotnet test`
    # to write into a private temp directory. There is no untrusted source, so the XXE/billion-laughs
    # threat model does not apply. If this ever reads a TRX from anywhere else, that changes.
    try:
        tree = ET.parse(path)
    except ET.ParseError:
        return set(), -1

    names, count = set(), 0
    for result in tree.iter(f"{TRX_NS}UnitTestResult"):
        if result.get("outcome") != "Failed":
            continue
        count += 1
        # "Ns.Class.Method(param: \"x\")" -> "Method"
        method = (result.get("testName") or "").split("(")[0].rsplit(".", 1)[-1]
        if method:
            names.add(method)

    return names, count


def run_suite(project):
    """Returns (failed_count, failed_names, problem), problem being '' or a short code."""
    with tempfile.TemporaryDirectory() as results:
        try:
            r = subprocess.run(
                ["dotnet", "test", project, "--nologo", "-v", "q",
                 "--results-directory", results, "--logger", "trx;LogFileName=results.trx"],
                cwd=run_root(), capture_output=True, text=True, env=ENV, timeout=RUN_TIMEOUT)
        except subprocess.TimeoutExpired:
            return 0, set(), "timeout"

        out = r.stdout + r.stderr

        # Guard 5: CA*/IDE* are errors in this repo, so they are build failures too.
        if ("error CS" in out) or (re.search(r"error (CA|IDE)\d+", out) is not None):
            return 0, set(), "build"

        failed, count = parse_trx(Path(results) / "results.trx")

        # Cross-check against the console summary. Two independent readings of one run disagreeing
        # means neither can be trusted, and reporting either would be a verdict the data does not
        # support. Treat it as inconclusive rather than picking the one that looks nicer.
        m = re.search(r"Failed:\s+(\d+),\s+Passed:\s+(\d+)", out)
        if m and int(m.group(1)) != count:
            print(f"    !!! reading mismatch: console says {m.group(1)} failed, TRX says {count}")
            return count, failed, "mismatch"

        return count, failed, ""


def run_lane(name, only=""):
    lane = load_lane(name)
    project = lane.PROJECT
    gaps, start = [], time.time()

    print(f"=== lane {name} :: baseline ===", flush=True)
    count, _, problem = run_suite(project)
    if count != 0 or problem:
        print(f"    !!! baseline not green (failed={count} problem='{problem}') — skipping")
        return [(f"{name}: baseline", "NOT GREEN")]

    for entry in lane.MUTATIONS:
        mutation_name, path, old, new = entry[0], entry[1], entry[2], entry[3]
        claims = entry[4] if len(entry) > 4 else None

        if only and mutation_name[0] not in only:
            continue

        original = path.read_text()
        src = original
        edits = as_edits(old, new)

        bad = [(o, src.count(o)) for o, _ in edits if src.count(o) != 1]
        if bad:
            for anchor, hits in bad:
                print(f"--- {mutation_name}\n    INVALID: anchor matched {hits} times "
                      f"(need exactly 1): {anchor.strip()[:60]!r}", flush=True)
            gaps.append((mutation_name, "INVALID (anchor)"))
            continue
        if all(o == n for o, n in edits):
            print(f"--- {mutation_name}\n    INVALID: replacement identical to the original "
                  "(guards nothing)", flush=True)
            gaps.append((mutation_name, "INVALID (no-op)"))
            continue

        backup = path.with_suffix(".bak")
        shutil.copy2(path, backup)

        def _restore(path=path, backup=backup):
            shutil.copy2(backup, path)
            backup.unlink(missing_ok=True)
            os.utime(path, None)   # guard 1

        _restore = _restore
        try:
            for anchor, replacement in edits:
                src = src.replace(anchor, replacement, 1)
            path.write_text(src)
            os.utime(path, None)

            if not pre_run_mtime_guard(path, project):
                gaps.append((mutation_name, "STALE BINARY (pre-run guard)"))
                continue

            count, failed, problem = run_suite(project)

            if problem:
                reason = {
                    "timeout": f"exceeded {RUN_TIMEOUT}s (hang)",
                    "build": "did not compile",
                    "mismatch": "console and TRX disagreed",
                }.get(problem, problem)
                print(f"--- {mutation_name}\n    INCONCLUSIVE — {reason}", flush=True)
                gaps.append((mutation_name, f"INCONCLUSIVE ({problem})"))
            elif count == 0:
                print(f"--- {mutation_name}\n    GAP — no test went red", flush=True)
                gaps.append((mutation_name, "GAP"))
            elif claims and claims in failed:
                print(f"--- {mutation_name}\n    CLAIMED by {claims}", flush=True)
            elif claims:
                others = sorted(failed)
                print(f"--- {mutation_name}\n    ELSEWHERE — '{claims}' did NOT go red; caught by:",
                      flush=True)
                for t in others[:5]:
                    print(f"      - {t}", flush=True)
                if len(others) > 5:
                    print(f"      (+{len(others) - 5} more)", flush=True)
                gaps.append((mutation_name, "ELSEWHERE (claim not verified)"))
            else:
                print(f"--- {mutation_name}\n    caught by {count} test(s), no claim registered",
                      flush=True)
        finally:
            _restore()
            _restore = None
            # Against the PRISTINE text, not the mutated one — comparing to `src` here would be
            # comparing the mutated file against itself and never fire.
            if path.read_text() != original:
                print(f"    !!! RESTORE MISMATCH in {path.name}")
                sys.exit(2)

    print(f"=== lane {name} :: post-sweep verification ===", flush=True)
    count, _, problem = run_suite(project)
    print(f"    failed={count} problem='{problem}'", flush=True)
    if count != 0 or problem:
        print("!!! tree is NOT green after restore — results above are untrustworthy")
        sys.exit(3)

    print(f"=== lane {name} done ({time.time() - start:.0f}s) ===", flush=True)
    return gaps


def main():
    stale = sorted(p for p in SOURCE_ROOT.rglob("*.bak") if "obj" not in p.parts and "bin" not in p.parts)
    if stale:
        print("!!! refusing to start: backup files present, meaning a previous run was killed")
        print("    mid-iteration and a mutation may still be applied. Inspect and remove:")
        for p in stale:
            print(f"      {p}")
        return 4

    signal.signal(signal.SIGINT, _on_signal)
    signal.signal(signal.SIGTERM, _on_signal)

    if not acquire_lock():
        return 6

    global RUN_ROOT, LANES_DIR
    RUN_ROOT = make_isolated_copy()
    LANES_DIR = RUN_ROOT / ".styloagent/tools/mutations"
    print(f"=== isolated sweep in {RUN_ROOT} (shared tree untouched) ===", flush=True)

    lanes = sorted(p.stem for p in LANES_DIR.glob("*.py"))
    if not lanes:
        LOCK.unlink(missing_ok=True)
        print(f"!!! no lanes found in {LANES}")
        return 5

    selected = sys.argv[1] if len(sys.argv) > 1 else ""
    only = sys.argv[2] if len(sys.argv) > 2 else ""
    if selected:
        lanes = [selected]

    all_gaps = []
    try:
        for lane in lanes:
            all_gaps.extend(run_lane(lane, only))
    finally:
        LOCK.unlink(missing_ok=True)
        remove_isolated_copy()

    print("\n=== summary ===")
    if not all_gaps:
        print("every mutation verified — no coverage gaps found")
    else:
        for name, verdict in all_gaps:
            print(f"  {verdict}: {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
