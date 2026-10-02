#!/usr/bin/env python3
"""Shared mutation harness, one hardened implementation, not five.

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

**One file per lane, discovered at runtime, never a shared list.** A single module-level list that
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
failure mode this harness exists to detect, one level up, so name the claiming test and the
harness compares:

  * **CLAIMED**, the named test went red. The claim is verified.
  * **ELSEWHERE**, other tests went red, the named one did not. The behaviour is guarded, but NOT
    by the test that claims it. Investigate: either a redundant guard, or a claim that is untested.
  * **GAP**, nothing went red, and the run recorded a population to have gone red. Toothless.
  * **INCONCLUSIVE**, did not compile, exceeded the timeout, or ran no tests at all. Never a pass.
  * **INVALID**, anchor missing/ambiguous, or the replacement is a no-op. Never a pass.

A ZERO NEEDS A POPULATION
-------------------------
GAP is a NEGATIVE result: "no test went red". A run that recorded no tests reports the same zero for
an entirely different reason, and reading that as GAP turns the absence of evidence into evidence of
absence. **A zero without a population is not evidence of absence, it is the absence of evidence.**
So the suite reading carries a population (`total`), and a mutation whose run recorded none is
INCONCLUSIVE, never GAP. The same guard applies to the baseline and to the post-sweep verification: a
suite that discovered nothing has failed=0 and problem='', and would otherwise pass as green.

This was not hypothetical. The one GAP verdict this harness has on record (mutation Q, lane queue,
`.styloagent/scratch/queue/reanchor-Q.log`) carries no population in its artifact, and could not
have: nothing printed one. It was a truncated run, and it is now caught by the `aborted` guard, but
the artifact could not have told you either way.

THE CONVENTION: ANY SWEEP HARNESS TAKES THE LOCK
-----------------------------------------------
**Committed or ad-hoc, in `.styloagent/tools/` or in `/tmp`, every harness that mutates source takes
`.styloagent/tools/.mutation-sweep.lock` before its first mutation and releases it when it finishes.**

Use `.styloagent/tools/sweep-lock.sh with <your-command>`; it acquires, runs, and releases on
success, failure, Ctrl-C or SIGTERM. Prefer it over hand-rolled acquire/release, the failure mode of
a hand-rolled pair is a harness that exits early and leaves the lock behind, blocking every other
sweep until somebody works out why.

**This is a convention and not merely a mechanism, on purpose.** An earlier version of this ruling
covered only this file, and that was not enough: a lane ran mutation rounds from
`/tmp/assess-mutation-round*.sh`, ephemeral, outside the repository, and therefore **invisible to
anyone auditing for sweep tools.** A bystander cannot check for a tool they cannot see, so the lock
is how a harness announces itself rather than something we hope to find.

This file takes the lock too, even though its isolation means it does not need it, so that **one
signal covers every harness**, and "is the lock held?" remains a complete answer.

SWEEPS RUN IN AN ISOLATED COPY, AND WHY THAT IS NOT ABOUT THIS LANE
----------------------------------------------------------------------
The sweep runs against a filesystem copy of the tree, never in place. **This exists because the
completion gate is fleet-wide, not because sweeps are dangerous to their own lane.**

Mutating shared source in place is, from any other lane's point of view, indistinguishable from
*their* code being broken. That is exactly what happened: `access-` ran the completion gate, saw
Queue fail for a reason that had nothing to do with Queue, and concluded the suite was
non-deterministic, a false finding against correct work, recorded as a doubt about the durability
claims. `dotnet test StyloMail.slnx` is now how every lane certifies, so an in-place sweep makes the
fleet's verification instrument lie at the precise moment it is used.

The lock file and stale-`.bak` check remain, but they are no longer the mitigation, they are the
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
   Being killed mid-iteration leaves a mutation applied. That tree LOOKS clean, the source parses,
   most tests pass, which makes it the worst state to resume from. Only SIGKILL escapes every
   mechanism, and that is what the startup check is for.

4. POST-SWEEP GREEN RUN. Restore everything, re-run the untouched suite, require green before
   believing any result above. Without it a mutation can be credited as "caught" by the PREVIOUS
   mutation's lingering binary, a false positive, which is worse than a false negative because it
   makes an unguarded behaviour look guarded.

5. ANALYZERS ARE ERRORS IN THIS REPO. A mutation that fails to build proves nothing. `CA*` and
   `IDE*` diagnostics count as build failures alongside `error CS`.

THE RECURRING SHAPE: A GUARD WHOSE CONDITION CANNOT BE FALSE
------------------------------------------------------------
This is the single defect this project keeps producing, and it has now been observed at **three
different levels**, each time producing confidence rather than a signal:

  1. **In test assertions.** A "clamped to 200" claim tested with 5 items; a "counts terminal
     payloads too" claim tested only on non-terminal rows. Both pass whether the behaviour holds or
     not. A claim about a ceiling cannot be tested below the ceiling.
  2. **In verdict extraction.** The `[FAIL]` regex could not match a parameterised `[Theory]` line,
     so failed-name extraction returned empty while the summary count was non-zero, and the branch
     order fell through to a confident `ELSEWHERE`. My lane had no theories and was right by luck.
  3. **In this harness's own self-check.** The restore verification compared the file against the
     *mutated* text, i.e. against itself. It could never fire.

**The rule:** when you add a guard, ask what input makes it fail, and if you cannot name one,
it is decoration. Then check it fails. A verdict branch that has never been seen firing, a restore
check that has never been seen mismatch, an assertion whose falsifying case you cannot construct:
all of them are the thing this harness exists to find, and a verifier is not exempt from being
verified.

A corollary worth applying across lanes: **a defect that shows up only where two lanes differ is
invisible from inside either one.** My lane could not have found (2), it needed a lane whose
fixtures are parameterised. Same insight as "build the solution, not just your project", one layer up.

RECOGNISING A STALE BINARY, THE SYMPTOM
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

# No __pycache__. A bytecode cache written beside this file is one more stray artifact for the next
# reader to trip over, and if this directory ever joins the tracked tree it would also be an
# untracked entry in `git status --porcelain`, which the sweep's own precondition reads.
sys.dont_write_bytecode = True

TOOLS = Path(__file__).resolve().parent

# The real repository root, used for the lock and for cleanup reporting.
SOURCE_ROOT = TOOLS.parents[1]

# What the isolated copy leaves behind: build output by NAME, agent-local state by PATH.
#
# The name list is build output and OS noise, never read by a build. The path list is the bus,
# scratch evidence, logs, screenshots, spike experiments and parked worktrees: state that belongs
# to the agents rather than to the system under test.
#
# MEASURED 2026-10-01, because the cost was invisible until counted: `.styloagent/spikes` alone is
# 1.4 GB (`spikes/laya/venv` is a Python environment), and including the whole `.styloagent` made
# every copy 42,554 files / 1.59 GB. With these excluded it is 856 files / 10.7 MB, a 150x cut in
# I/O per sweep, on a box that was swap-bound at the time. A sweep now copies less than the test
# binaries it will build.
#
# SAFE TO EXCLUDE, verified the same day rather than assumed: no file under `src/` or `tests/`
# mentions `.styloagent` outside an XML doc comment, and there is no bare `"styloagent"` or
# `"scratch"` string literal, so nothing in a build resolves a path into these directories at run
# time. The doc-comment mentions are references to evidence files, not reads.
#
# BY PATH, NOT BY BASENAME, and that matters: `ignore_patterns` matches the entry NAME, so a rule
# like "logs" or "channel" would prune any directory of that name anywhere in the tree, including
# one a test fixture might later acquire, silently and with no error. A path rule cannot misfire
# that way. (`obj`/`bin`/`TestResults` stay basename rules; those names mean build output anywhere.)
COPIED_NEVER_PATHS = frozenset({
    ".styloagent/spikes",
    ".styloagent/scratch",
    ".styloagent/logs",
    ".styloagent/shots",
    ".styloagent/channel",
    ".worktrees",
})

COPIED_NEVER_NAMES = (".git", "obj", "bin", "__pycache__", ".DS_Store", "TestResults")


def ignore_for_copy(dir_path, names):
    """Ignore build output by name and agent-local state by path. For `shutil.copytree`.

    Resolving both sides keeps this correct when the repository is itself reached through a
    symlink. If a directory is not under the source root, path pruning is skipped rather than
    guessed at, and the name rules still apply.
    """
    ignored = set(shutil.ignore_patterns(*COPIED_NEVER_NAMES)(dir_path, names))
    try:
        rel = Path(dir_path).resolve().relative_to(SOURCE_ROOT)
    except ValueError:
        return ignored
    for name in names:
        if (rel / name).as_posix() in COPIED_NEVER_PATHS:
            ignored.add(name)
    return ignored

# Where the sweep actually runs, set by main() to an isolated copy.
#
# **Deliberately None, not SOURCE_ROOT.** Defaulting to the shared tree makes the SAFE value opt-in
# and the unsafe one the default: any entry point that skips main(), a wrapper script, a REPL, an
# import-and-call, a refactor that reaches a sweep helper directly, would silently mutate the shared
# tree again. That is the same bug re-entering through the initialiser instead of the write.
#
# Found by `access-`, who read the module rather than trusting the fix, and noticed that safety
# currently depended on main() having run.
RUN_ROOT = None
LANES_DIR = TOOLS / "mutations"

# Where that same directory sits INSIDE the isolated copy. `LANES_DIR` is an absolute path in the
# shared tree and `RUN_ROOT` is the copy, and the lane files stand at the same relative position in
# both, so the offset is derived once here instead of being spelled out again at the point of use.
LANES_REL = LANES_DIR.relative_to(SOURCE_ROOT)


def run_root():
    """The tree the sweep operates on. Raises rather than defaulting to the shared tree."""
    if RUN_ROOT is None:
        raise RuntimeError(
            "RUN_ROOT is unset, the sweep was not set up through main(). Refusing to continue: "
            "the only safe default would be an isolated copy, and falling back to the shared tree is "
            "the hazard this tool exists to prevent.")
    return RUN_ROOT

RUN_TIMEOUT = int(os.environ.get("MUTATE_TIMEOUT", "180"))

ENV = {**os.environ,
       "DOTNET_ROOT": "/usr/local/share/dotnet",
       "PATH": "/usr/local/share/dotnet:" + os.environ.get("PATH", ""),
       # A sweep runs 25 builds and 25 test runs back to back. With node reuse on, MSBuild leaves a
       # worker node RESIDENT after each one, so a single lane's sweep grows the machine's resident
       # build-server count by the whole lane and the nodes outlive the sweep by their idle timeout.
       # `ingress-` measured the consequence on 2026-10-01: swap 15854 MB of 17408 with 41 resident
       # MSBuild nodes, and Host tests failing on a 5-minute host-build timeout that had nothing to do
       # with anyone's code. The sweep is the fleet's heaviest builder, so it is the one that must not
       # accumulate. This is set here, not left to the caller's shell, because the caller is a lane
       # that has no reason to know.
       "MSBUILDDISABLENODEREUSE": "1"}

_restore = None


def _restore_if_active():
    global _restore
    if _restore:
        fn, _restore = _restore, None
        fn()
    LOCK.unlink(missing_ok=True)
    remove_isolated_copy()


def _on_signal(signum, _frame):
    print(f"\n!!! signal {signum}, restoring before exit", flush=True)
    _restore_if_active()
    sys.exit(130)


# Covers normal exit, an uncaught exception and sys.exit(), which signal handlers alone do not.
atexit.register(_restore_if_active)


def as_edits(old, new):
    """Normalise a mutation's replacement into a list of (old, new) pairs.

    `old`/`new` may each be a single string, or equal-length lists for a mutation that needs more
    than one edit, declaring a field and then using it cannot be expressed as one replacement, and
    without this such a mutation has to be left out of the lane entirely, leaving that test's teeth
    resting on a recorded run instead of a reproducible entry. That is the thing this harness exists
    to avoid, so the expressiveness belongs here.
    """
    if isinstance(old, list) or isinstance(new, list):
        if not (isinstance(old, list) and isinstance(new, list) and len(old) == len(new)):
            raise ValueError("multi-edit mutations need `old` and `new` lists of equal length")
        return list(zip(old, new))
    return [(old, new)]


# The lock does NOT travel with this script. It is pinned to the repo root rather than derived from
# TOOLS, because it is the fleet's one signal that a sweep is running: every lane's false-red check,
# `.styloagent/PROTOCOL.md` and the operator's standing instructions all look for it at
# `.styloagent/tools/.mutation-sweep.lock`. Derived from TOOLS it would follow this file wherever it
# went, and a lock whose path moves turns every one of those detectors into a predicate that cannot
# fire, silently, in the same change that moved it. Pinned, the path is byte-identical on both sides
# of any relocation of this file.
#
# It also stays OUT of the tracked tree deliberately. `tools/` is tracked, so a lock written there
# would appear in `git status --porcelain` while a sweep ran, and the clean-tree precondition this
# fleet gates its sweeps on reads exactly that output.
LOCK = SOURCE_ROOT / ".styloagent/tools/.mutation-sweep.lock"


def lock_header():
    """Name the holder in the lock, so a bystander can attribute it instead of guessing.

    Added 2026-10-01. `ingress-` read a false red off the lock's mere PRESENCE and could not say
    who had held it, when, or whether the sweep was still alive; the lock as written carried only a
    static explanation, so the one question a lane actually has, "was this caused by the sweep I am
    looking at?", could not be answered from it. A lock that cannot name its holder is a rumour
    that a concurrent sweep existed, not a report that one did.

    The holder is the lane's prefix from `STYLOAGENT_AGENT_PREFIX` when the cockpit exports one,
    and "unknown" otherwise. The invoking command line is recorded too, because it usually carries
    the lane's own scratch path and so identifies the holder even when the variable is not set.
    """
    holder = os.environ.get("STYLOAGENT_AGENT_PREFIX", "").strip() or "unknown"
    started = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())

    parent = "unknown"
    try:
        lines = subprocess.run(
            ["ps", "-o", "command=", "-p", str(os.getppid())],
            capture_output=True, text=True, timeout=5).stdout.strip().splitlines()
        if lines:
            parent = lines[0][:200]
    except Exception:   # a lock must never fail to be written because attribution failed
        pass

    return (
        "mutation sweep: RUNNING\n"
        f"holder:  {holder}    (set STYLOAGENT_AGENT_PREFIX to name the lane)\n"
        f"pid:     {os.getpid()}\n"
        f"started: {started} (UTC)\n"
        f"cmd:     {' '.join(sys.argv[:4])}\n"
        f"invoked from: {parent}\n"
        "\n"
    )


LOCK_EXPLANATION = """\
The lines above name the holder, the PID and the UTC start time. Read them first: a lock with a
stale start time and a PID that is gone is a leftover, not a running sweep.

A mutation sweep is running RIGHT NOW, in an ISOLATED COPY of this tree under TMPDIR.
The shared tree is NOT being modified while this file exists.

That is a change from earlier versions of this tool, and it changes what this lock means.
The sweep copies the tree, applies each mutation to the COPY, builds and tests there, and
deletes the copy at the end. So a `dotnet test` you run in this repository is not reading
mutated source, and a red you see is about your code, not about the sweep.

It is still not nothing: the sweep builds and tests, so it competes for CPU, and a red on
a timing-sensitive test under load is worth re-taking. That is the only remaining reason
to look at this file before believing a failure.

WHAT A KILLED SWEEP LEAVES HAS MOVED. A SIGKILLed sweep cannot clean up, but it now leaves
its work inside its own copy, not in the shared tree: the applied mutation and the *.bak
beside it are under TMPDIR in `stylomail-sweep-*/repo`. So the surviving signal for that
case is the directory, not a file under src/:

    ls -d "${TMPDIR:-/tmp}"/stylomail-sweep-*   # a killed sweep's leftover working copy

A `*.bak` under `src/` is now a leftover from before this isolation landed, from a mutation
applied by hand, or from a lane's own tooling. Do not assume it is harmless: a sweep refuses
to start while one is present, and it is still worth restoring from rather than ignoring.

The lock is written when a sweep starts and unlinked when it ends, so its ABSENCE means the last
sweep ended cleanly. SIGKILL cannot run that cleanup: a killed sweep LEAVES THE LOCK BEHIND. So a
lock present means a sweep is running now or was killed, and the lock alone cannot tell those two
apart, which is the second reason to check for the leftover directory above.
"""


_copy_root = None


def make_isolated_copy():
    """Copy the tree somewhere private and run the whole sweep there.

    <b>Why this exists.</b> The sweep mutates source files. Doing that in place is indistinguishable
    from any other lane's point of view from *that lane's code being broken*, a `dotnet test`
    running at the same moment sees a mutated tree and reports failures that belong to this tool.
    That is not hypothetical: it produced a false "your suite is flaky" report against the queue,
    and it made the fleet-wide completion gate (`dotnet test StyloMail.slnx`) lie for every lane at
    the moment they were certifying.

    The lock file makes the hazard diagnosable, but only for someone who already knows to look in
    `.styloagent/tools/`, and a bystander running the gate has no reason to. **Detection does not
    fix a mechanism whose effects appear far from its cause; isolation does.**

    `git worktree` would be the better form and is blocked: the repository has no baseline commit,
    so there is nothing to branch from. A filesystem copy is the equivalent today.

    `obj/` and `bin/` are excluded, they are build output, and copying them would be slower and
    could carry a stale binary across, which is the very thing the mtime guard exists to catch.
    Agent-local state is excluded too, by path: see `COPIED_NEVER_PATHS`. It was 1.4 GB of the
    1.59 GB copied on 2026-10-01, and no build reads any of it.
    """
    global _copy_root

    _copy_root = Path(tempfile.mkdtemp(prefix="stylomail-sweep-"))
    target = _copy_root / "repo"

    shutil.copytree(
        SOURCE_ROOT, target, symlinks=True,
        ignore=ignore_for_copy,
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
        print(f"!!! refusing to start: {LOCK.name} exists, another sweep is running, or a")
        print("    previous one was SIGKILLed. Do not run two sweeps concurrently: they would")
        print("    interleave mutations and attribute each other's failures.")
        return False

    LOCK.write_text(lock_header() + LOCK_EXPLANATION)
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
        print(f"        source={source:.3f} binary={binary:.3f}, MSBuild would skip the rebuild")
        print("        and this run would execute a STALE binary. Touch the source and re-run.")
        return False
    return True


TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def parse_trx(path):
    """Failed test names, the failed count, and the population, from structured XML.

    **Console parsing cannot do this.** A `[Theory]` failure line carries its parameters between the
    method name and `[FAIL]`:

        ...BytesThatAreNotAMessage_AreRejectedAsMalformed(text: "Hello,\\n\\nHow are you?",
                                                          expectedReason: "no-header-fields") [FAIL]

    Those parameters contain spaces, commas and escaped newlines, so a name-extraction regex either
    matches nothing at all, leaving `failed` empty while the summary count is non-zero, which then
    falls through to ELSEWHERE and reports a *false* verdict about a theory that went red exactly as
    claimed, or grabs a fragment of the parameters as a test name. Found by `mime-` on R10/R13.

    The TRX logger emits the method name as one escaped attribute, so there is nothing to parse
    around. This is the same defect the harness exists to find: a mechanism reporting a verdict its
    data does not support.
    """
    if not path.exists():
        return set(), -1, -1

    # ElementTree rather than defusedxml: the input is a TRX this harness just asked `dotnet test`
    # to write into a private temp directory. There is no untrusted source, so the XXE/billion-laughs
    # threat model does not apply. If this ever reads a TRX from anywhere else, that changes.
    try:
        tree = ET.parse(path)
    except ET.ParseError:
        return set(), -1, -1

    names, count, total = set(), 0, 0
    for result in tree.iter(f"{TRX_NS}UnitTestResult"):
        # The population: every result the run RECORDED, whatever its outcome. Filtering by outcome
        # here would reintroduce the defect, because the question the population answers is "did the
        # run produce a reading at all", not "was every test executed". A run that recorded nothing
        # is the case that must not be read as "nothing failed, therefore nothing is unguarded".
        total += 1
        if result.get("outcome") != "Failed":
            continue
        count += 1
        # "Ns.Class.Method(param: \"x\")" -> "Method"
        method = (result.get("testName") or "").split("(")[0].rsplit(".", 1)[-1]
        if method:
            names.add(method)

    return names, count, total


def run_suite(project):
    """Returns (failed_count, failed_names, problem, total), problem being '' or a short code.

    `total` is the POPULATION: how many results the run recorded, or -1 where there is no reading at
    all (timeout, build failure, no TRX). It exists because a failed count of 0 is ambiguous on its
    own. Without a population, "no test went red" and "no test ran" are the same tuple, and the
    caller reports the second as the first.
    """
    with tempfile.TemporaryDirectory() as results:
        try:
            r = subprocess.run(
                ["dotnet", "test", project, "--nologo", "-v", "q",
                 "--results-directory", results, "--logger", "trx;LogFileName=results.trx"],
                cwd=run_root(), capture_output=True, text=True, env=ENV, timeout=RUN_TIMEOUT)
        except subprocess.TimeoutExpired:
            return 0, set(), "timeout", -1

        out = r.stdout + r.stderr

        # Guard 5: CA*/IDE* are errors in this repo, so they are build failures too.
        if ("error CS" in out) or (re.search(r"error (CA|IDE)\d+", out) is not None):
            return 0, set(), "build", -1

        failed, count, total = parse_trx(Path(results) / "results.trx")

        # A TRUNCATED RUN IS NOT A GREEN RUN. xUnit prints "Passed!  - Failed: 0, Passed: N" for the
        # tests that finished and only THEN "Test Run Aborted." when the host dies, so an aborted run
        # parses as zero failures and its own console cross-check AGREES with the TRX, because both
        # readings are taken from the same truncated output. Read as green, it reports CLAIMED or GAP
        # for behaviour the run never exercised. Observed in this lane: `mutate.py queue Q` reported
        # GAP on one run and CLAIMED on the next, on the same tree with the same mutation, while that
        # mutation reddened its claiming test deterministically in an isolated copy. A run that did
        # not complete cannot support any verdict, so refuse to give one.
        if "Test Run Aborted." in out:
            return count, failed, "aborted", total

        # A missing or unparseable TRX is not a count. parse_trx returns -1 for it, and a bare -1
        # reaching the caller reads as "no test went red" (count == 0) or falls through to ELSEWHERE,
        # neither of which the data supports. Real case: the pre-fix Host suite wrote no TRX when the
        # host crashed (`scratch/queue/host-suite-run1.log`).
        if count < 0:
            return count, failed, "no-trx", -1

        # Cross-check against the console summary. Two independent readings of one run disagreeing
        # means neither can be trusted, and reporting either would be a verdict the data does not
        # support. Treat it as inconclusive rather than picking the one that looks nicer.
        m = re.search(r"Failed:\s+(\d+),\s+Passed:\s+(\d+)", out)
        if m and int(m.group(1)) != count:
            print(f"    !!! reading mismatch: console says {m.group(1)} failed, TRX says {count}")
            return count, failed, "mismatch", total

        # No summary line at all: the run stopped before it could report one, so there is nothing to
        # cross-check the TRX against and no verdict to give.
        if m is None:
            return count, failed, "incomplete", total

        return count, failed, "", total


def run_lane(name, only=""):
    lane = load_lane(name)
    project = lane.PROJECT
    gaps, start = [], time.time()
    ran = 0

    print(f"=== lane {name} :: baseline ===", flush=True)
    count, _, problem, total = run_suite(project)
    # The population guard belongs here too, and it is the same hole: a baseline that discovered no
    # tests gives failed=0 and problem='', so it passes as green and every mutation below is then
    # judged against a suite that never ran. The summary label stays "NOT GREEN" rather than gaining
    # a new string, so a lane matching on it keeps working; the detail is in the line above it.
    if problem or total <= 0:
        print(f"    !!! baseline unusable (failed={count} total={total} problem='{problem}'): a run "
              "with no reading, or one that ran no tests, cannot support any verdict. skipping")
        return [(f"{name}: baseline", "NOT GREEN")], 0
    if count != 0:
        print(f"    !!! baseline not green (failed={count} of {total}), skipping")
        return [(f"{name}: baseline", "NOT GREEN")], 0
    print(f"    baseline green, population={total}", flush=True)

    for entry in lane.MUTATIONS:
        mutation_name, path, old, new = entry[0], entry[1], entry[2], entry[3]
        claims = entry[4] if len(entry) > 4 else None

        if only and mutation_name[0] not in only:
            continue

        ran += 1
        # A MISSING TARGET IS A VERDICT ABOUT THE ENTRY, NOT A CRASH. `path.read_text()` used to be
        # the first statement here and it sits OUTSIDE the try/finally below, so an entry pointing at
        # a moved or deleted file raised FileNotFoundError straight out of run_lane and killed the
        # whole sweep with a traceback, before any verdict and with no summary. Live case: `f76907d`
        # moved `src/StyloMail.Mime/UrlTools.cs` into Core, and mime's R3 and R20 still point at the
        # old path. Lanes run in sorted order, so mime's first dead entry would have ended the run
        # before queue's lane was reached at all, and the operator would be reading a stack trace
        # about a filename instead of a list of unusable mutations.
        if not path.exists():
            print(f"--- {mutation_name}\n    INVALID: target file does not exist: {path}",
                  flush=True)
            gaps.append((mutation_name, "INVALID (target missing)"))
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

            count, failed, problem, total = run_suite(project)

            if problem:
                reason = {
                    "timeout": f"exceeded {RUN_TIMEOUT}s (hang)",
                    "build": "did not compile",
                    "mismatch": "console and TRX disagreed",
                    "aborted": "the test host aborted, the run is truncated, not green",
                    "no-trx": "no usable TRX was written",
                    "incomplete": "the run stopped before reporting a summary",
                }.get(problem, problem)
                print(f"--- {mutation_name}\n    INCONCLUSIVE, {reason}", flush=True)
                gaps.append((mutation_name, f"INCONCLUSIVE ({problem})"))
            elif total <= 0:
                # A ZERO NEEDS A POPULATION. GAP is a NEGATIVE result, "no test went red", and a run
                # that recorded no tests produces the same zero for a different reason. Reading it as
                # GAP turns the absence of evidence into evidence of absence, which is the defect
                # this harness exists to find, committed by the harness in its own verdict.
                print(f"--- {mutation_name}\n    INCONCLUSIVE, no test ran", flush=True)
                gaps.append((mutation_name, "INCONCLUSIVE (no population)"))
            elif count == 0:
                print(f"--- {mutation_name}\n    GAP, no test went red", flush=True)
                gaps.append((mutation_name, "GAP"))
            elif claims and claims in failed:
                print(f"--- {mutation_name}\n    CLAIMED by {claims}", flush=True)
            elif claims:
                others = sorted(failed)
                print(f"--- {mutation_name}\n    ELSEWHERE, '{claims}' did NOT go red; caught by:",
                      flush=True)
                for t in others[:5]:
                    print(f"      - {t}", flush=True)
                if len(others) > 5:
                    print(f"      (+{len(others) - 5} more)", flush=True)
                gaps.append((mutation_name, "ELSEWHERE (claim not verified)"))
            else:
                print(f"--- {mutation_name}\n    caught by {count} test(s), no claim registered",
                      flush=True)

            # Printed for EVERY verdict, on its own line, so it is additive: no existing line's text
            # changes and a lane matching on one keeps working. It is here because the record problem
            # this fixes was not only that the harness never USED a population, it is that it never
            # PRINTED one, so no artifact of any past sweep can be re-read for it.
            print(f"    population={total}", flush=True)
        finally:
            _restore()
            _restore = None
            # Against the PRISTINE text, not the mutated one, comparing to `src` here would be
            # comparing the mutated file against itself and never fire.
            if path.read_text() != original:
                print(f"    !!! RESTORE MISMATCH in {path.name}")
                sys.exit(2)

    print(f"=== lane {name} :: post-sweep verification ===", flush=True)
    count, _, problem, total = run_suite(project)
    print(f"    failed={count} problem='{problem}'", flush=True)
    print(f"    population={total}", flush=True)
    if count != 0 or problem or total <= 0:
        print("!!! tree is NOT green after restore (or the restoring run recorded no tests), "
              "results above are untrustworthy")
        sys.exit(3)

    print(f"=== lane {name} done ({time.time() - start:.0f}s) ===", flush=True)
    return gaps, ran


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
    # The lane directory IN the copy, from the offset derived at module level. This line used to read
    # `RUN_ROOT / ".styloagent/tools/mutations"`: a second copy of a path the module already knows,
    # and the copy that goes stale, because it is spelled against a directory that exists only while
    # this file happens to sit where it sits. Written as a literal, relocating this file by one level
    # would leave it globbing an empty directory, finding no lanes and exiting 5, reporting that it
    # could not find lane files the copy contained all along.
    LANES_DIR = RUN_ROOT / LANES_REL
    print(f"=== isolated sweep in {RUN_ROOT} (shared tree untouched) ===", flush=True)

    lanes = sorted(p.stem for p in LANES_DIR.glob("*.py"))
    if not lanes:
        LOCK.unlink(missing_ok=True)
        print(f"!!! no lanes found in {LANES_DIR}")
        return 5

    selected = sys.argv[1] if len(sys.argv) > 1 else ""
    only = sys.argv[2] if len(sys.argv) > 2 else ""
    if selected:
        lanes = [selected]

    # Counted BEFORE the teardown below, not after: load_lane reads the lane module out of the
    # isolated copy, and remove_isolated_copy() deletes it, so counting afterwards crashed the
    # summary with FileNotFoundError after the sweep itself had finished.
    available = sum(len(load_lane(lane).MUTATIONS) for lane in lanes)

    all_gaps = []
    ran_total = 0
    try:
        for lane in lanes:
            gaps, ran = run_lane(lane, only)
            all_gaps.extend(gaps)
            ran_total += ran
    finally:
        LOCK.unlink(missing_ok=True)
        remove_isolated_copy()

    # The count matters. With the `only` filter this used to print "every mutation verified" after
    # running a single mutation, a claim about the whole lane supported by one test: the same
    # "guard whose condition cannot be false" defect this harness exists to find, in its own summary.
    print("\n=== summary ===")
    if all_gaps:
        for name, verdict in all_gaps:
            print(f"  {verdict}: {name}")
    elif ran_total == available:
        print(f"every mutation verified, no coverage gaps found ({ran_total} of {available} run)")
    else:
        print(f"no gaps in the {ran_total} mutation(s) actually run, but {available - ran_total} "
              f"of {available} were NOT run (only-filter): this says nothing about those")
    return 0


if __name__ == "__main__":
    sys.exit(main())
