#!/usr/bin/env python3
"""Run every harness in this directory and report one total.

    python3 tools/corpus/tests/run.py

ONE COMMAND, no arguments, no shell loop to get wrong: shell `for` loops over a glob differ between
`zsh` and `bash` in ways that have already cost this fleet a night (an unmatched glob aborts a `zsh`
command where `bash` passes it through literally), so the discovery lives here rather than in a shell
snippet a reader has to paste correctly.

EXIT CODES, which are the point of a runner:
  0  every harness passed
  1  at least one harness FAILED (its name is printed)
  2  a harness SKIPPED (it refused to measure, e.g. a dataset it needs is absent). Not a pass: a
     runner that returned 0 for a skip would report a verification that did not happen.

Each harness is run with `-B` so no `__pycache__` appears next to the code it guards.
"""

from __future__ import annotations

import pathlib
import subprocess
import sys


def main() -> int:
    here = pathlib.Path(__file__).resolve().parent
    harnesses = sorted(p for p in here.glob("test-*.py"))
    if not harnesses:
        print(f"no harnesses found in {here}", file=sys.stderr)
        return 1

    failed: list[str] = []
    skipped: list[str] = []
    assertions_run = 0
    for harness in harnesses:
        result = subprocess.run(
            [sys.executable, "-B", str(harness)], capture_output=True, text=True
        )
        tail = (result.stdout or result.stderr).strip().splitlines()
        summary = tail[-1] if tail else "(no output)"
        # The COUNT each harness now leads its summary with, summed so the SUITE carries a total
        # as well. Without it a suite whose harnesses all executed nothing reads exactly like a
        # clean one, which is the same hole the per-harness count closes one level down: a run
        # that recorded no tests reports the same zero for an entirely different reason.
        lead = summary.split(" assertion(s) run", 1)[0]
        if lead.isdigit():
            assertions_run += int(lead)
        else:
            # NOT counted as zero, and said out loud: a summary that lost its count is a format
            # drift, and silently adding nothing for it is how the total would start lying.
            print(f"  {harness.name}: summary carried no assertion count: {summary!r}",
                  file=sys.stderr)
        print(f"{harness.name:<28} exit={result.returncode}  {summary}")
        if result.returncode == 2:
            skipped.append(harness.name)
        elif result.returncode != 0:
            failed.append(harness.name)
            # The whole output of a failing harness, so one command is enough to see why.
            print(result.stdout)
            print(result.stderr, file=sys.stderr)

    print()
    # The total goes on the LAST line, because the last line is what a reader and any oracle take.
    print(f"{len(harnesses)} harness(es): "
          f"{len(harnesses) - len(failed) - len(skipped)} passed, {len(failed)} failed, "
          f"{len(skipped)} skipped, {assertions_run} assertion(s) run")
    if failed:
        print(f"FAILED: {', '.join(failed)}", file=sys.stderr)
        return 1
    if skipped:
        print(
            f"SKIPPED (refused to measure, NOT a pass): {', '.join(skipped)}",
            file=sys.stderr,
        )
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
