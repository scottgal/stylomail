# The mutation harness: what it measures, and the five ways it used to lie

**Why this record exists.** The mutation harness lives at `.styloagent/tools/mutate.py` with its lane
definitions beside it, and `.styloagent/` is ignored (`.gitignore:147`) with zero tracked files. So the
harness, the coverage numbers every lane quotes from it, and the reasoning behind its verdict rules are
all invisible to a reader who clones this repository. A repair to a load-bearing instrument with no
versioned home is the same class of gap the instrument exists to find. This file is that home: the
rules, why each one is there, and the evidence that each guard works. The script itself stays
unversioned by design, because it mutates source and must never be part of a build.

**How it works, in one paragraph.** A lane file declares mutations: a target file, an anchor string
that must appear exactly once, a replacement, and optionally the name of the test expected to catch it.
The harness copies the tree to an isolated directory, mutates the copy, runs a test suite, and asks one
question: did a test go red, and was it the test that was claimed. A sweep never touches the shared
working tree. Green after a mutation means that mutation is invisible to the suite, which is a coverage
gap rather than a success.

## The verdict vocabulary

| Verdict | Meaning |
| --- | --- |
| CLAIMED | A red, and the test named in the lane went red. The coverage claim is verified. |
| ELSEWHERE | A red, but the named test did not go red. Caught by something other than the claim. **Read this one against defect 5: the same verdict is produced when the named test was RENAMED, and the harness cannot tell the two apart.** |
| GAP | No test went red. The mutation is invisible to the suite. |
| INCONCLUSIVE | No reading at all: did not compile, timed out, the host aborted, or no test ran. |
| INVALID | The entry itself is broken: anchor missing or ambiguous, a no-op edit, or a missing target. |
| NOT GREEN | The baseline or the post-sweep run was not clean, so nothing above it can be trusted. |

A gap list is a list of things that went wrong. **CLAIMED is not in it**, because a verified claim is
the harness working. A lane checking for coverage must therefore read the printed verdict, not only the
returned list.

## Five defects, all the same shape

Every one of these is an instrument reporting absence as though it were evidence. They were found by
reading the asserting line rather than the test result, and three of them produced confident output
while making no claim at all. The fifth is that same shape one step further out, and it is the only
one whose verdict was not merely unsupported but FALSE: the harness was right about the suite and
wrong about the code, because the label it compared against had moved.

### 1. A baseline that ran zero tests passed as green (the worst of the five)

The baseline is what every mutation verdict below it is calibrated against. It ran first and only its
FAILURE count was examined, so a baseline that discovered no tests produced "0 failed" and read as
clean. Every mutation in that lane was then judged against a suite that never ran, which makes the
whole sweep vacuous rather than one verdict wrong.

The fix reads the population from the TRX (every `UnitTestResult` the run recorded, whatever its
outcome) and refuses the baseline when there is no reading at all, or the reading records no tests.
The post-sweep check got the identical guard, because a restoring run that recorded nothing cannot
show the tree was restored.

### 2. A zero-test run was reported as GAP

The rule was `count == 0`, where `count` is the number of failed tests. A run with no tests at all
produces the same zero as a run where everything passed, so the harness reported the strongest possible
claim, "this mutation is invisible to the suite", from a run that could not have produced a red under
any mutation.

**A zero needs a population. A zero without a population is not evidence of absence, it is the absence
of evidence.** GAP is a NEGATIVE result, and the absence of evidence was being converted into a
negative result. That there is exactly one GAP verdict in the harness's history (mutation `Q`) is not
the point and never was: the question was too narrow. **No GAP this harness has ever produced carries a
population, because it never printed one on any run.** The defect was the normal shape, not an
exception, which is why `Q` is kept rather than withdrawn: it is evidence about the instrument, and
withdrawing evidence about the instrument is how the instrument's limits get forgotten.

### 3. The population was never printed, so no past artifact can be re-read for it

Fixing the rule is not enough if the record does not carry the number. Every verdict now prints
`population=N` on its own line, additively so that no existing output string moved and a lane matching
on one keeps working. This is not recoverable for any artifact written before the change. It is only
preventable going forward, which is why the number is printed on the baseline, on every mutation
verdict, and on the post-sweep line.

### 4. A moved file killed the whole sweep

`path.read_text()` sat outside the per-mutation `try/finally`, and the sweep's own `try` has no
`except`. A mutation entry pointing at a file that had been moved or deleted therefore raised
`FileNotFoundError` out of the lane loop, and a whole sweep ended with a traceback, no summary and no
gap list, after the lock and the isolated copy had been cleaned up. Lanes run in sorted order, so one
broken entry in `mime` would have taken `queue` down with it: an entire run, and this lane's own
coverage claims among the casualties, lost to a filename. A missing target is now a verdict,
`INVALID (target missing)`, and the lane continues to its next entry.

### 5. A renamed test was reported as someone else catching it

The harness decides CLAIMED with `elif claims and claims in failed`: the claiming test name, a string
from the lane file, is compared against the test names the suite reported failing. That string is
load-bearing at run time. So when a test is renamed, the claim stops matching while the mutation stays
covered: the renamed test still goes red, and the harness prints `ELSEWHERE, '<old name>' did NOT go
red; caught by:`, listing the renamed test as a different test that caught it. ELSEWHERE goes into the
gap list. **A rename therefore converts a clean CLAIMED into a false statement about the code: "this
behaviour is guarded by something other than the test that claims it."** Nothing about the code changed.

This one cannot be caught from inside a sweep. From the harness's vantage point, "the test was renamed"
and "a different test caught it" are the same observation: one name in the claim, a different name in
the failing set. Only the tree can tell them apart, where the claim is text and the test names are text.
So the guard is not a runtime branch; it is a **preflight**, `check-claim-names.py`, which resolves every
entry's claim against the lane's own test project and reports any name that resolves nowhere. It needs
no build, no copy, no lock and no window.

**How this one is established, stated differently from the four above.** Defects 1 to 4 are each a
change to a verdict rule, so each has a staged pre-fix and post-fix arm. Defect 5 is not a wrong rule,
it is a correct rule whose INPUT can go stale, and the mechanism is read from the two lines that decide
it (`claims in failed`, and the `else` that prints "did NOT go red"). It is not staged with a renamed
test, because staging it would mean renaming a test and running the suite in the shared tree to watch
the verdict flip, which is the one thing a sweep is configured never to do. What IS falsified is the
guard: that it reports a name that resolves nowhere, and that it does not report everything.

**Its limit, stated rather than discovered later.** Resolution is a substring test, so it catches a
rename to a name sharing no substring and does NOT catch a truncation of the same name. It answers "does
this name still exist", which is not "does that test still assert the mutated behaviour": a claim whose
assertion was gutted still resolves, and a sweep still has to settle that. As with defect 4, the guard
removes one way to be wrong, not every way.

## How each guard is demonstrated rather than asserted

A guard proved against an invented tuple proves the arithmetic, not the plumbing. So each guard inside
the harness is driven through the real `run_lane` with readings taken from real staged runs, and the
assertions are on the RETURNED verdict rather than on parsing the text under test. The guard for defect
5 is not inside `run_lane` and cannot be, so it is driven a different way: the falsifier calls the very
function the preflight calls, against the real `tests/` tree, with a fabricated lane whose claims are
known-present and known-absent.

The problem is that the harness is untracked, so there is no revision to check out for a "before". Each
falsifier reconstructs the pre-fix arm by deleting exactly the code the fix added, and ASSERTS that the
deletion landed, because a control built by a substitution that silently failed to substitute proves
nothing. **A check that exercises a copy of a rule while the instrument runs a different copy is the
same failure one level up**, so the claim rule exists in exactly one place and the falsifier asserts
that the preflight calls it rather than inlining its own.

| Evidence | What it shows |
| --- | --- |
| `.styloagent/scratch/queue/zero-test-probe.sh` | Two real zero-test shapes staged on this toolchain. A filter matching nothing, and a project that genuinely contains no test classes. Both write a TRX with zero results and print no `Failed: N, Passed: M` summary. |
| `.styloagent/scratch/queue/falsify-no-population.py` | 12 checks, 0 failures. Pre-fix and fixed arms on the real readings; a constructed reading where they differ; a control showing a real coverage gap is still GAP; and the real `run_suite` at all three call sites, so the arity is proven end to end. |
| `.styloagent/scratch/queue/falsify-missing-target.py` | 10 checks, 0 failures. The pre-fix arm really does raise out of `run_lane`. The fixed arm reports the verdict and is driven dead-entry-then-live-entry, with the live one still going CLAIMED, so the guard cannot degenerate into silently skipping mutations. |
| `.styloagent/scratch/queue/validate-guards.py` | The pre-existing guard validator, updated for the new reading, with the population asserted per case rather than merely carried. |
| `.styloagent/scratch/queue/check-claim-names.py` | Resolves every lane entry's claiming test name against the tree. Reading at first run: 49 of 49 resolve in their lane's own project, 0 only elsewhere, 0 stale. |
| `.styloagent/scratch/queue/falsify-claim-names.py` | 17 checks, 0 failures. One fabricated lane carrying a claim that exists and one that does not, both required to land as expected; a no-claim entry required to stay OUT of the population, so the denominator cannot be inflated by entries that never had a claim; the real lanes re-scanned in the same process, so the fabricated stale is the name and not the scan's default answer; the substring limit asserted as a limit; and the preflight asserted to call this rule rather than inline a copy. |
| `.styloagent/scratch/queue/preflight-anchors.py` | Every lane's anchors AND claiming test names checked against the working tree with no build, no copy, no lock and no mutation, before a gated sweep window is spent on a lane with dead entries or a stale claim. Two summary lines, nonzero exit on either an INVALID anchor or a STALE name. |

**Honest limit on defect 2.** On this toolchain the false GAP is not reachable: both staged zero-test
shapes are refused earlier by the pre-existing `incomplete` guard, because a run that discovers nothing
prints no summary line to cross-check against, so the pre-fix and fixed arms agree on both real
readings. The population guard is therefore defence in depth rather than the closure of a live defect,
and it is worth keeping on the two-independent-readings argument the harness already makes elsewhere:
the older coverage rests on the console TEXT of a summary line, the new guard rests on the TRX DATA,
and a change in xunit's console output or a lane using a different logger takes the first away and
leaves the second.

## Operational rules a reader must not re-derive

- **A sweep runs in a copy, and the copy is pruned by relative path, never by basename.**
  `shutil.ignore_patterns` matches the entry NAME, so a rule spelled `logs` prunes any directory of
  that name anywhere in the tree, silently: the copy simply lacks it and nothing is raised. Pruning
  agent-local state by path took a copy from 1.59 GB to 10.70 MB.
- **The lock names its holder.** `.styloagent/tools/.mutation-sweep.lock` carries a header with the
  holder prefix, pid, start time and invocation. Its PRESENCE means a sweep is running now OR was
  killed: a SIGKILL cannot run the unlink. The leftover `stylomail-sweep-*` directory under `${TMPDIR}`
  tells the two apart. A `.bak` beside a source file under `src/` is a legacy or hand-applied leftover,
  not a live sweep: mutation backups live in the copy.
- **Before believing a red, check the tree.** A sweep mutates a copy, so the shared tree is not the
  source of the failure. Check both signals anyway, and read the sweep log rather than the lock alone.
- **A lane's claiming test name is a REFERENCE, not a comment.** It is compared against the reported
  failing tests at run time, so it goes stale the moment a test is renamed, and the failure is silent
  and one-sided: the verdict becomes ELSEWHERE and a false gap is filed. Run
  `.styloagent/scratch/queue/check-claim-names.py` before a window, with the anchors.
- **The `only` filter matches the FIRST CHARACTER of a mutation name**, so `only=R` selects every
  mutation whose name starts with R. It can run more than intended, never fewer.
- **A lane may need more than one root when code moves between layers.** When a refactor moves a
  target out of a surface without moving the tests that claim it, the honest repair is a second root
  constant named for the new layer (for example `SHARED = ROOT / "src/StyloMail.Core"`), resolved
  against the moved entries and commented with the fact: the code moved, the claiming tests did not,
  and the project still reaches it through a project reference. Renaming the lane instead would break
  every recorded verdict that refers to it by name, and retiring it would delete verified mutations
  to fix a label.
- **Serialise.** A sweep runs many builds and test runs in a burst on a shared host. Check for a
  neighbouring lane's announced run before starting one, and announce before starting.

## The rule all five defects leave behind

Report the population beside every count, and never read a zero as a negative result until you know
what could have made it non-zero. A guard whose condition cannot be false is not a guard, and a green
that no test could have turned red is not evidence of anything.

The fifth adds the other half. **When a verdict is decided by comparing a NAME, check the name against
the thing it names, because a name that moved reads exactly like the absence of the thing.** The
harness was not failing to look; it was looking at a string. That check cannot live inside the sweep,
where the two cases are indistinguishable, so it lives before the sweep, in the window's preflight.
Report the population beside every count, and check the label beside every verdict.
