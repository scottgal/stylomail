# Driving the operator console

Live UI tests for `src/StyloMail.Desktop`, powered by
[Mostlylucid.Avalonia.UITesting](../../lucidview/external/lucidRESUME/src/Mostlylucid.Avalonia.UITesting).
Modelled on mylo's `ux-scripts/`, including the repeatability rule.

The harness is **Debug-only**. It is not compiled into a Release build and adds no dependency there.

## Run it

```bash
./ux-scripts/run-console-smoke.sh            # the console against a Host with the live feed on
./ux-scripts/run-console-no-feed-smoke.sh    # ... against a Host with no feed at all
./ux-scripts/run-console-feed-drop-smoke.sh  # ... against a Host killed mid-run
./ux-scripts/run-console-not-ready-smoke.sh  # ... against a Host that is up and refusing mail
./ux-scripts/run-console-nimble-smoke.sh     # ... against a Host with a working local assessor
./ux-scripts/run-console-quarantine-smoke.sh # ... and a message policy quarantined, then released
./ux-scripts/run-console-feed-recovery-smoke.sh # ... and that Host taken away and brought back
./ux-scripts/run-console-address-change-smoke.sh # ... and pointed at an address nothing answers on
./ux-scripts/run-console-long-outage-smoke.sh # ... and taken away for longer than the console retries
./ux-scripts/run-console-operator-retry-smoke.sh # ... and pressed Reconnect on while it is still away

./ux-scripts/probe-submission-route.sh       # not a smoke: measures what the routes answer
./ux-scripts/check-runner-gate.sh            # not a smoke: checks the runner gate itself
./ux-scripts/check-build-fingerprint.sh      # not a smoke: checks what the build records about itself
./ux-scripts/check-stop-host-bounded.sh      # not a smoke: checks the stop path cannot hang
./ux-scripts/check-run-dir-refusal.sh        # not a smoke: checks whose run directory a runner takes
./ux-scripts/stamp-host-build.sh             # not a smoke: stamps a real build directory, the instrument
                                             #   the check above is a check of
./ux-scripts/falsify-build-fingerprint.sh    # not a smoke: proves the three checks above can go red
```

`run-console-address-change-smoke.sh` is the only run that changes the Host mid-run from inside the
console: it saves a second address on the connection screen, and the address is a loopback port
nothing is listening on, so the console's own state is the measurement. That makes the port a
precondition rather than a detail, and the runner checks it and refuses to start if something answers
there. It also owns the precedence rule it was written to find: a stored address has to win over the
harness's `STYLOMAIL_HOST`, or the console rebuilds to the Host it is already on and the address the
operator saved is inert. The run was red for that reason before it was green, and it asserts the
address the client holds as well as the state that follows from it, so a future failure says which of
the two moved.

The Nimble and quarantine runs are the only two that need something installed: a local Ollama on 11435
holding a `nimble` model. They are runs of their own rather than sections of the main smoke for that
reason, and because they are the only ones whose Host can fill the middle pane. The quarantine run
needs the model to answer a particular way as well (the message must cross `QuarantineThreshold`), so
its runner refuses to drive the console unless the route actually quarantined it.

Run a script, not the YAML. Each sources `console-harness.sh`, which builds the solution it needs,
starts a throwaway Host on loopback with locally generated values, points the console at it, and takes
it down again on the way out, including on failure and on interrupt. That is what lets the assertions
be exact and the run be repeatable from any starting state.

`probe-submission-route.sh` is the odd one out and deliberately so: it asserts nothing, starts the
same throwaway Host, posts one message and prints what each route answered. A claim about a route, as
opposed to a claim about a screen, is measured before it is asserted, and a probe that failed would
have to decide the right answer before measuring it. Its readings, and which console states they do
and do not reach, are in `ux-scripts/state-coverage.md`.

A fresh clone is enough: nothing has to be built first. The scripts build `StyloMail.slnx` themselves
with `console_dotnet_build`, which cd's into the repo root and names the solution relatively. That
relative path is not a style choice. Naming it absolutely made MSBuild treat one project as two
identities under `/tmp` (a symlink to `/private/tmp` on macOS) and build them against each other, and
the symptoms were a Host that built clean and then died at launch, plus intermittent `CS0006` on
`obj/.../ref/*.dll`. The build's output is shown and also written to
`build-log/solution.log`, so a failure is readable after the fact; the
three scripts used to discard it, which turned a build error into exit 1 and a 0-byte log.

The build now stamps itself as well. `console_record_build_fingerprint` writes `host-build.id` and
`host-build.txt` into the run directory: a digest and an mtime per assembly in the Host's output
directory, and one digest over that. The reason came from the fleet on 2026-10-01. A run identifier
says *when* a measurement was taken and nothing about *what* produced it, and this Host is built from
the shared working tree, so another lane's uncommitted edit sits inside the engine of every console
number taken after their save. `overview-` found a prebuilt Host carrying an uncommitted Policy change
and raised it fleet-wide; the rule that came out of it is that a number whose build cannot be named is
not quoted as a number, and `corpus-` proposed the stamp.

Three things are worth knowing before reading a digest, and the second and third were added on
`overview-`'s 05:13 correction after `policy-` measured their own file and found the value decayed.

- The identity covers assembly *contents* and names only, so a rebuild of unchanged source keeps the
  same id, and the assembly timestamps are kept out of it deliberately.
- **The mtime is the assembly's own timestamp, not the build's.** MSBuild preserves a dependency's
  timestamp when it copies it in, so a copied DLL reads older than the build that placed it: measured
  2026-10-01, the Host's copy of `StyloMail.Policy.dll` is stamped 04:49:52 while `StyloMail.Host.dll`
  beside it is stamped 04:54:18. Read that column as the file's own time, which is the right answer to
  "does this assembly predate the change", and take the digest as the identity.
- **The manifest names the output directory in its header.** The same assembly name exists in several
  bin directories at once and those are different files: `src/StyloMail.Policy/bin/Debug/net10.0/StyloMail.Policy.dll`
  and the Host's own copy of that assembly have different digests, so a manifest of bare names lets two
  lanes hash "the same" assembly, get numbers that match nobody's, and conclude they measured different
  builds. The header is what makes each digest refer to one file.

It is stamped on the build success path only: a
failed build leaves the previous binary in the output directory, and naming that here would attribute
this run's numbers to a build this run did not produce. A run that cannot stamp prints
`Host build: NOT RECORDED` and removes any earlier stamp, so a reader cannot find a previous run's id
and cite it as this run's build.

`./ux-scripts/check-build-fingerprint.sh` checks that without a Host or a build, in seconds, against a
directory of stand-in assemblies: eight cases, 0 failures. The case that matters is the falsifiable one:
one file's bytes change with the file's size and mtime held fixed, and the id has to move. It also pins
the other direction (a new mtime alone leaves the identity alone), every absence path, and the header
that names the directory, which is checked by reading two directories in turn and requiring the header
to follow.

**It is the check of the stamp, not the instrument**, and saying so cost the fleet a broadcast on
2026-10-01. `overview-` told every lane to run it instead of hand-rolling a build hash; its stand-ins
are files it writes itself, so following that literally returns a verdict about the function, exit 0,
and nothing naming your build. Two lanes read the file and caught it, and the file's own header now
opens by saying so. To stamp a real build, run `./ux-scripts/stamp-host-build.sh [directory]`: it points
the function at the directory you name (the Host's output directory by default), reads it, and writes
the manifest and id into this lane's scratch. Run against the Host's output directory on 2026-10-01 it
reproduced `6bc278b14c33` over 18 assemblies, which is the number two other lanes had already recorded.

The last case is the guard on `CONSOLE_RUN`. `${CONSOLE_RUN:-...}` substitutes only when the variable is
**unset**, so a caller who had it exported handed the script its own run directory, which the script then
`rm -rf`'d and started writing into; `overview-` measured it at source and filed it HIGH. The script now
refuses unless `CONSOLE_RUN` is unset or resolves under this lane's scratch, and the guard is shared
(`console_assert_run_dir_is_ours`, which takes each caller's own description of what it does to the
directory, so the warning never describes a hazard that caller does not face). The sibling has the same
call and the sharper first move: it writes a placeholder OVER `auth.headers`, where a run keeps its
principal key. Each of the three scripts asserts the refusal in its own cases, because what has to be true
is that *this* file calls the guard, not that the guard works.

**The guard compares canonical paths, not strings.** As first landed it compared the literal text, and a
symlink inside the allowed base defeated it: `escape-probe/victim` starts with
the prefix while resolving to `scratch/desktop-probe-outside/victim`, outside it, and a caller acting on
that decision deletes a directory the guard exists to protect. Measured on this host with a marker file
before the fix and after it. An automated review routed by `article-` raised it and is credited, with one
correction back: its suggested `realpath -m` is not available here, because macOS `/bin/realpath` rejects
`-m` outright, so the resolution uses the same `python3` the harness already needs for the port preflight.
It fails closed, so a machine that can resolve nothing refuses everything rather than allowing everything.

**Runners have the other half of the rule, and it cannot be the same guard.** A runner's declared job
includes clearing its run directory before it starts, and every runner lives under `/tmp` on purpose, so
"everything under this lane's scratch" would refuse every smoke. What is refused instead is ADOPTION:
the runner keeps its own default and an inherited `CONSOLE_RUN` is refused unless the caller sets
`CONSOLE_REUSE_RUN=1`. That opt-in is not a way to read another run: it means the directory is deleted
first. All eleven runners plus the main smoke carried `export CONSOLE_RUN="${CONSOLE_RUN:-<own path>}"`
before the source and `rm -rf "$CONSOLE_RUN"` after it, so `export CONSOLE_RUN=/tmp/run-a` and then any
one of them deleted run A, principal key and artifacts together. Measured 2026-10-01 and filed medium,
ruled by `overview-` in the shape above; the clearing now exists once, in `console_runner_run_dir`, which
each runner calls immediately before its build. Before it, not after, because `console_build_all` stamps
into `$CONSOLE_RUN`: a refusal that arrived later would already have written a manifest naming this build
into another run's directory.

`./ux-scripts/check-run-dir-refusal.sh` asserts that without a Host and without a build, in seconds: five
cases, 0 failures. Case 1 runs a real runner with the caller's directory set and asserts it exits 2,
leaves the directory's sentinels untouched, names the opt-in, and never reaches the build tool; case 2
that an uninherited call takes the default it is given and clears it; case 3 that the opt-in adopts and
clears; case 4 is the control for case 3, the same call with the opt-in absent, which must refuse; case 5
asserts every one of the eleven is wired before its build, that none still clears `$CONSOLE_RUN` itself,
and that each names its own directory, with exactly one on the main smoke's path. The `dotnet` in case 1
is a stub exported to that child alone, so the check cannot start a solution build even in the red case it
exists to catch: a check that can build is a check nobody runs while the gate is shut.

The falsification is kept at `./ux-scripts/falsify-build-fingerprint.sh` and mutates copies in scratch,
never the shared tree. Seventeen mutations, one per pass, each with the exact set of
cases it must redden: A names-only identity (case 2), B no clearing on absence (4, 5, 6), C header
dropped (1, 7), D the guard call dropped (8), E the guard's pattern losing its trailing slash (8), F the
sibling's guard call dropped (7), G the canonicaliser returning its argument unchanged (8), H the port
validation disabled (the sibling's 8), I the probe's allocation test removed (the sibling's 9), J the
probe stuck on "cannot tell" (the sibling's 4, 6 and 9), K the run-directory opt-in moved to
`!= "1"` (1, 3, 4), L the refusal removed so the function always takes the default (1, 3, 4), M one
runner clearing its own `$CONSOLE_RUN` again (5), N one runner's call moved after its build (1, 5), O one
runner declaring the main smoke's directory as its own (5), P the probe's numeric arm removed (the
sibling's 4), Q the explicit empty argument folded back into the default (the sibling's 4). K and L
share a red set and are still two
mutations: they edit different lines, each with its own precondition. P and Q share the sibling's case 4
the same way, and the falsifier reports at case granularity, so the assertions they kill are what
separate them: P takes the two non-numeric ones, Q the empty-argument one alone. K's set was measured before it was
written down, and the first run reddened 3 as well, because a moved opt-in stops working in both
directions: it no longer gates the adoption and it no longer permits it. One per pass
because several of them target case 8, and a red that two
mutations could have caused names neither property. Each line names the file its case number belongs to,
because three of these redden a case 8 in the fingerprint check and one reddens a case 8 in the sibling,
and a bare "8" cannot be attributed to either. The run-directory check renumbers nothing and shares
case 1 and case 5 with the others, which is the same hazard one check further out. A case header inside a
nested run's captured output is not
a case header: the guard case embeds a whole child run in its failure message, and once a mutation let
the child run, this parser attributed the outer case's later failure to the CHILD's case number, which
moved when a case was added here. The two older checks now indent embedded child output from its second
line, so the parser cannot see it; the run-directory check embeds none, because it flattens the child's
lines onto the FAIL line when it quotes them. The failure that exposed it is mutation F's. Its own scratch guard was the
weaker form of the one it exists to defend: a literal prefix plus a `..` substring, which a symlink at any
component of the path satisfies while the `rm -rf` resolves elsewhere. It now canonicalises both sides
with `console_canonical_path` (from the ORIGINAL harness, since mutation G neuters the copy's) and
compares the resolved forms, and it carries a two-direction control that runs before the passes: a symlink
inside the base pointing out of it must be refused, and a plain path inside must be allowed. Replacing the
canonicalisation with the old lexical test makes that control exit 2, which is how the guard itself was
falsified rather than assumed. Each pass also asserts its own pattern still matched:
a mutation whose perl stops matching is a no-op, and in this session that no-op first read as a clean
pass, then was reported as "applied" by a precondition that had copied the same stale pattern. H was
written with one `]` where the source has two (`$ ]]; then`), and the precondition caught it as
"did not apply" rather than letting a `<none>` pass as a mutation that had been tried.

What none of this establishes is that the stamp is taken on a real run. The check exercises the function
directly and `stamp-host-build.sh` names a real directory, but whether `console_build_all` calls it on a
success path is a reading of that file rather than a measurement of one, and no smoke has run since the
stamp was added.

That log used to live in `/tmp`, and it moved on 2026-10-01 because four agents lost time reading
stale console logs there: `/tmp` is shared, invisible to the fleet's `recent_files` view, and old
enough to be mistaken for current evidence. Artifacts go in this lane's scratch, and the three check
and stamp scripts keep their `CONSOLE_RUN` there too, because what they put in it is a placeholder plus
an artifact meant to be read after a failure. The *run* scripts are the exception and stay under `/tmp`
on purpose: their `CONSOLE_RUN` holds the per-run principal key, and scratch would keep a
credential-bearing file indefinitely where `/tmp` has it removed on the way out.

Each script owns its own Host, its own `CONSOLE_RUN` scratch directory and its own output directory
under `ux-results/`, and it clears only its own. The main smoke writes into `ux-results/console-smoke/`
and wipes that; it used to be `rm -rf ux-results`, which deleted every sibling's evidence and made the
subdirectory scheme inert, because a subdirectory of a directory that is `rm -rf`'d does not survive.
That was not theoretical: a main smoke twenty minutes after the long-outage run destroyed its five
screenshots, and `ux-results/` is gitignored so nothing was recoverable. Found at source by `overview-`
on 2026-10-01. Running the main smoke is now safe for the other runs' artifacts.

## The live feed's four states

The feed is one surface with four states, and they are the whole reason there are four scripts. A
console following a Host and a console that never had anything to follow look identical in every pane
and mean opposite things about whether what is on screen is current, so each state gets an assertion
rather than an assumption.

| State | Reached by | Script | Says |
| --- | --- | --- | --- |
| Following | Hub mapped (`StyloMail__Traffic__Enabled=true`) | `run-console-smoke.sh` | `Live` |
| No feed | Hub absent, which is a real deployment's default | `run-console-no-feed-smoke.sh` | `No live feed`, and **not** stale |
| Dropped | a feed that was Live stops | `run-console-feed-drop-smoke.sh` | `Live updates stopped, screen may be out of date`, in amber |
| Recovered | the same Host, back on the same address, key and database | `run-console-feed-recovery-smoke.sh` | `Live` again, over a surface it has read again |
| Past the budget | the same Host, back after the console has stopped retrying | `run-console-long-outage-smoke.sh` | the stopped feed and the stale row still on screen, and `Live` only after the operator's Reconnect |
| Retrying | an operator's Reconnect, with the Host still away | `run-console-operator-retry-smoke.sh` | `Retrying: attempt n of 4, next in n seconds`, over a screen still marked out of date, and the same count after a second press |

`console-harness.sh` reads `CONSOLE_TRAFFIC` when it starts the Host: `true` (the default) maps the
Hub, `false` starts a Host without it. The last three states cannot be reached from a YAML at all,
because the harness has no shell-out action and nothing in a script can stop a process, so their
runners stop the Host from outside. Each waits for the screenshot that proves the console is Live
before killing, rather than sleeping a fixed amount: a fixed sleep would be the one thing here that
makes the test flaky, and flaky is the same as absent for a proof.

The retrying row is the only one whose runner never brings the Host back, and that is the point of
it rather than an omission. A Host that returns is `run-console-long-outage-smoke.sh`, where the
connect succeeds on its very first attempt and no retry is exercised at all. Here the console spends
its whole sequence knocking at an address with nothing behind it, which is the only arrangement in
which the bound can be seen: the counter is asserted at its last wait (the thirty second one, the
only window wide enough to press the button inside), a second press is required to change nothing,
and the sequence is required to stop on its own schedule rather than on a restarted one. The stale
sentence is asserted across all of it and after it, because the console never did go `Live`; the
long-outage run asserts the other half of that rule, that the sentence comes down when it does.

Recovery is the one state where saying `Live` is not the claim. The console sets its feed state to
Live as soon as the socket is back and raises the resynchronisation after, so a script that only
asserted the headline would pass against a console that reconnected and never re-read. `Live` is
therefore the *second* assertion in that script and not the last: the runner pauses the harness sender
over the Host's own control route while the feed is down, and the script requires the row to offer
`Resume` afterwards. A change published to the Hub while nothing is connected reaches nothing, so that
button can appear only by the console reading the senders again. The runner's restart is timed to land
between SignalR's third and fourth reconnect attempts for the same reason: a change applied after the
socket came back would arrive as a notice, and the assertion would pass without the re-read. That
assertion is shared with the long-outage run, which waits past the budget instead of inside it and
then presses the operator's **Reconnect**: the same `Resume` on the same row, reached by a person
rather than by the client's own retry. It is also the one run in this directory whose schedule
depends on the machine as well as on the console, since its restart has to finish before the click
lands, so its runner times that restart, fails the run outright if it overran, and says so in one
line rather than failing an assertion about the feed ninety seconds later. Both runners' notes are in
`state-coverage.md`, and the `Resume`-after-a-gap assertion is what row 8 there used to say was out
of reach.

## A Host that is up and refusing

Not a feed state, and the reason it has a script of its own. The status line's states are separate
from the feed's, and the one with something to read is "Connected, not accepting mail": the Host
answers every request and will refuse the mail it cannot assess, so nothing is broken and nothing
works. The failed-checks block under the empty queue is where the Host's own check names appear, one
per line, and `run-console-not-ready-smoke.sh` is the run that puts it on screen. The block had been
rendered by nothing until then, which is how it kept a defect that no unit test could see - the model
announced the flag that shows the block and not the list inside it, so the block appeared with its
heading and nothing under it. `FailedChecks` was right the whole time; the window was never told to
read it again.

The state is reached by configuration rather than by breaking anything. `console-harness.sh` reads
`CONSOLE_ASSESSOR` when it starts the Host: `true` (the default) gives it a provider key and an
endpoint that cannot answer, and `false` gives it no assessment credential at all, which composes an
assessor that refuses every message and makes readiness answer `503 not_ready` with
`assessor_unavailable` in the checks. One secret without the other is a misconfiguration that refuses
to start, so the switch cannot produce it. That run also sets `CONSOLE_DECISION_FIXTURE=false`: a
decision body the Host did not produce has no place on the screen of a Host that cannot produce one.

The runner prints what `/health/ready` answered before it drives the console, so a red run can be
told apart from a console bug: if that line is not `HTTP 503`, the Host is what failed and the
assertions below it are about the wrong thing.

## A Host with a working local assessor

A Host with no cloud credential can still assess: `StyloMail__Assessment__Provider=Nimble` asks a
local Ollama and needs the master key alone, which is a supported deployment rather than a test
dodge. `console-harness.sh` reaches it with `CONSOLE_PROVIDER=nimble`, shaped like the other two
switches, and refuses to start unless something is answering on 11435 with a `nimble` model present:
a switch that looks on and measures nothing would be worse than one that fails, and 11435 rather than
Ollama's default 11434 is `NimbleOptions`' decision, so the check has to agree with it.

`CONSOLE_NIMBLE_ENDPOINT` overrides that address, which is how the shape "the provider is selected
and cannot answer" is started: point it at a port nothing is listening on and the Nimble provider is
composed exactly as ever, with no model behind it. `CONSOLE_NIMBLE_GENERATE` is the same address under
the name the provider actually reads; the override wins when both are set. Overriding away from the
local model also skips the model check above, deliberately and with a line saying so, because the
check exists to stop a run measuring nothing by accident and this is a run measuring nothing on
purpose. The address is printed by `probe-submission-route.sh` under `nimble endpoint:` so a reading
says which shape produced it.

This is the only shape on which a run fills the queue, and what it fills is narrower than it sounds.
Measured 2026-09-30 with `probe-submission-route.sh`: the default Host (a Jev endpoint that cannot
answer) refuses the submission with `503 deferred` and queues nothing, while a Nimble Host accepts it
with `202`, and a message policy holds comes back in the listing under both `awaiting_decision` and
`held`. Benign mail is accepted and then absent from every listing, because the listing enumerates
what needs attention and not what was delivered: an empty queue on that shape is the route working.
Equally, a held item is not a quarantined one: `QuarantineThreshold` is 0.80, and crossing it takes a
message that fires a lot of *weighted* semantic dimensions at once (the gradient is measured in
`ux-scripts/state-coverage.md`; sender history and a heavier word list are both measured false), which
is why the fixture for it is written rather than picked. The state-by-state account is
`ux-scripts/state-coverage.md`.

`run-console-nimble-smoke.sh` is what makes that shape worth having. It submits
`tests/StyloMail.Desktop.Tests/fixtures/nimble-held-message.eml` through `POST /v1/submissions`,
refuses to drive the console
unless the route held it, and then opens that message's decision from its own row. Two things about
it are worth copying:

- **It asserts the pane is empty before it presses the row's button.** Without that, "the pane shows
  the decision" would pass on a pane that had been filled some other way, and the run would be
  evidence about rendering rather than about the join. For the same reason it sets
  `CONSOLE_DECISION_FIXTURE=false`: the file fixture fills this pane at window open, and a run that
  left it on could pass every assertion below with the join never having executed.
  The file it declines is asserted where it is *not* declined. `run-console-no-feed-smoke.sh` pins the
  fallback body's own risk index and its first reason sentence on screen, and `DecisionFixtureTests`
  binds the same file from the suite, because a fixture the harness ships but no run asserts is a
  fixture that stops parsing without anything going red: the mirror grew two `required` members, the
  in-test fixtures were updated with it (which is why the suite stayed green), and the shipped file was
  not. Both were added on 2026-10-01 for exactly that reason.
- **It pins the risk index at 0.575 rather than matching loosely.** That is the model's own number
  for this message, and it is what makes the pane's contents about *this* message. A model change
  moves it and the run fails on the value, which is the right outcome for a script whose precondition
  is a disposition it does not control.

Its first run also found a gap in the app rather than in the script. The empty-state block over the
middle pane had no name on its container, so the only assertable control was the text inside it, and
`Control.IsVisible` is the control's own property: `EmptyListDetailText` reports visible while its
parent is hidden, and the assertion failed with a row on screen. The block is now `EmptyListBlock` and
that is what a script asserts on. That is lesson 1 below, walked into for the third time, and the
reason it is written down.

`run-console-quarantine-smoke.sh` is the same shape for the far side of `QuarantineThreshold`,
submitting `tests/StyloMail.Desktop.Tests/fixtures/quarantine-threshold-payment-change.eml` (risk
0.8219178082191781, pinned at 0.822 as rendered) and releasing it from the console. It is a run of its
own rather than a section of the Nimble one because held and quarantined are different traffic, and
because the write action only exists on this side: what it asserts is Release absent before the row is
opened and present after, Confirm refused until a reason is typed, the result line naming the
principal, and then the listing **emptying**, which is the only visible effect a release has. Two
defects came out of writing it, one in the app and one in these runners, and both are lessons below.

## Seeding real traffic, when there is a corpus

`corpus-` owns the traffic and `tools/corpus/`; this harness consumes a batch through the Host's
routes and never parses a CSV itself, so the boundary between the two is one command rather than a
shared file format. `CONSOLE_CORPUS=/path/to/batch` turns seeding on, shaped like `CONSOLE_TRAFFIC`
and off by default because a fresh clone has no corpus, and a run that seeded nothing while looking
as though it had would read as coverage. The command is the interface pinned with `corpus-`: `seed
--base-url <url> --key-file <path> --batch <dir>`, with the key read from a path rather than passed
as an argument, which is why what the harness hands over is its own `principal.key` file. The CLI
path defaults to `tools/corpus/corpus.py` and `CONSOLE_CORPUS_CLI` overrides it, so corpus-'s choice
of entry point does not need an edit here.

**`CONSOLE_CORPUS` is read by `console_seed_corpus`, and that function is a library: no runner is
scaled to call it, and setting the variable alone does nothing.** The wiring is the runner's job:

- `run-console-corpus-smoke.sh` generates a batch from a fixed seed, exports `CONSOLE_CORPUS` to it,
  calls `console_seed_corpus`, runs the corpus's own `check`, and then drives
  `console-corpus-smoke.yaml` over the Host that resulted. It was the switch's first caller, so the
  seam above had been verified as an invocation and never as a run until 2026-10-01.

`console_seed_corpus` also passes `seed --resolve-join`, so the manifest records each message's
`internalMessageId` beside its queue id. Without the flag the manifest carries only the queue id, and
the join key from a message row to its decision row would have to be re-derived by every consumer.

Two things `corpus-` verified against this harness on 2026-10-01, so a future reader does not have to
re-derive them:

- **The invocation and the key file both match their CLI as written.** The flag set above is their
  `seed` signature, and the file the harness writes (`head -c 24 /dev/urandom | xxd -p | tr -d '\n'`,
  a bare hex string) is exactly what their reader wants: `read_text().strip()`, used verbatim as
  `X-StyloMail-Key`.
- **Their `seed` exits non-zero when nothing was accepted, and this harness relays that as a failure.**
  That is deliberate coupling rather than a nuisance. A batch where every message Allows still exits 0,
  so benign traffic seeds normally. But a batch in which every message is refused stops the console run
  there, which is right: a run that seeded nothing and then asserted on an empty listing would be
  measuring the absence of its own input.

## Modes

```bash
# Script mode: runs to completion, exits, writes screenshots and result.json
dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless --ux-test --script ux-scripts/console-smoke.yaml --output ux-results/console-smoke

# Interactive REPL: explore by hand, list controls, click, screenshot
dotnet run --project src/StyloMail.Desktop -- --ux-repl

# MCP server: lets an LLM drive the console over JSON-RPC stdio
dotnet run --project src/StyloMail.Desktop -- --ux-mcp
```

`--ux-headless` renders without a window, so a batch of runs does not steal keyboard focus. Drop it
when you want to watch.

In script mode the process needs a Host. Running the shell script sets that up; running `dotnet run`
by hand needs `STYLOMAIL_HOST` and `STYLOMAIL_SMOKE_KEY` set, which are the Debug-only overrides in
`ConsoleEnvironment`.

## Writing a script

1. Find the control you want to drive in `Views/MainWindow.axaml`. If it has no `x:Name`, give it one.
2. Add a `*.yaml` beside the others and run it through the shell script.
3. If it needs a Host of its own, write a runner for it, and set the switches that configure that Host
   **before** the `source`. The harness assigns `CONSOLE_RUN` itself, so a runner that sets it
   afterwards with `${CONSOLE_RUN:-...}` keeps the harness's value and silently shares the main smoke's
   scratch directory. That is two runs writing one tree, which the comment in each runner says cannot
   happen. It did, in all three of them, until the not-ready script was written and its `ready.json`
   turned up in another script's directory. The same construct has a second failure that took longer to
   find, because it only appears when someone else's variable is in play: an exported `CONSOLE_RUN` is
   KEPT by `${CONSOLE_RUN:-...}`, so the runner that set it then deleted the caller's run. So a runner
   no longer assigns `CONSOLE_RUN` at all. It declares its own path as `CONSOLE_RUN_DEFAULT` before the
   source and hands it to `console_runner_run_dir` immediately before its build, which refuses an
   inherited value unless `CONSOLE_REUSE_RUN=1` says otherwise. See
   `./ux-scripts/check-run-dir-refusal.sh`, and do not write the assignment back in.

Selectors are Playwright-flavoured: `name=`, `type=`, `text=`, `testid=`, `role=`, `label=`, composed
with `inside(...)`, `near(...)`, `first(...)`, `nth(n, ...)`, and `type=Button:has-text(Save)`.

Action types: `Click`, `DoubleClick`, `RightClick`, `TypeText`, `OpenDropDown`, `SelectDropDownItem`,
`PressKey`, `Hover`, `Scroll`, `Wait`, `Screenshot`, `Assert`, `Expect`, `MouseMove`, `MouseDown`,
`MouseUp`, `Drag`, `Wheel`, `WindowResize`, `StartVideo`, `StopVideo`.

Matchers: `HasText`, `ContainsText`, `IsVisible`, `IsEnabled`, and any of them negated with `Not.` or `!`.

### Controls generated from a template need an automation id

A row inside a data template cannot have a unique `x:Name`: a name in a template is per instance and
unreachable from outside it. Bind `AutomationProperties.AutomationId` to something that carries the
identity instead, and target it with `testid=`. `SidebarItem.PauseAutomationId` is the worked example.

## What this harness taught us, all found by running it

Several of these are about the harness rather than the app, and they are the ones worth reading
before writing another script.

**1. `Control.IsVisible` is not effective visibility.** It is the control's own property, so a
`TextBlock` inside a hidden bar still reports `IsVisible=true` and an assertion on it passes whether
the container is there or not. Assert on the container: `ActionConfirmBar`, `DecisionPaneScroll`.
This bit twice in one afternoon, and both times it failed a run where the app was right and the test
was wrong.

**2. Quoting a selector value changes its meaning, not just its parsing.** `text=word` matches a
substring, so `text=Quarantine` also matches "Quarantined" in the sidebar; `text='word'` is an exact
match against the whole displayed text, so a quoted sentence must be the entire sentence. For
anything longer than a word, use a named control with a `ContainsText` matcher instead.

**3. A `Button` whose content is a layout rather than a string does not match `text=`.** The sidebar
rows contain a `Grid`, so `text=Quarantined` finds the `TextBlock` and never the button. Use
`type=Button:has-text(Quarantined)`.

**4. A locator matches controls that are not visible.** Targeting the pause button by its text
reached a hidden one on a different row, because every row has the control and one row shows it.

**5. Record the pid of the process that must actually die.** `( cd … && dotnet run … ) &` records the
subshell's pid, so killing it left the real Host running. Two orphans accumulated, one of them still
holding the port.

**6. Refuse to start when the port is taken.** That orphan held a *different* API key, so the next
run silently drove against it, every authenticated call answered 401, and the console reported
"Connected" throughout because the readiness probe is unauthenticated. It looked exactly like an app
bug and was not one. The Host is now launched as the built binary rather than through `dotnet run`,
and the port is checked first.

**7. Clear the results directory.** The harness writes screenshots without removing what a previous
run left, so a step that stops running leaves its old image behind and it reads as current. A
screenshot from a passing run was read as evidence about a failing one.

**8. A silently swallowed failure is invisible to a harness too.** `LoadSendersAsync` caught the API
exception and returned, on the reasoning that the status bar already said why. It did not, and the
sidebar sat on "Loading" forever while the status bar said "Connected". The harness could not report
it either, because there was nothing to report: the only symptom was a row that never appeared.
Reporting the failure is what turned a mystery into a 401 in one line.

**9. `Click` raises the Click event, so a control that has no Click handler cannot be clicked.**
It resolved `type=ListBoxItem:has-text(...)`, reported `success: true` with a plausible duration, and
did nothing at all: the list's `SelectionChanged` never fired, which was proved by logging from the
handler and seeing no line. A `ListBoxItem` is selected by pointer input, and raising `ClickEvent`
on it reaches nothing.

The verdict it produced was worse than the missing click. The step after it failed, so the run went
red on the *app* while the fault was the harness, and the failure text named a pane that was hidden
for the right reason. Two red runs were spent on the app before the handler log showed the click had
never arrived.

**The rule: click a control that has a `Click` handler.** If a surface has none, that is a gap in the
app rather than in the harness: the ledger row gained an explicit `Open decision` button, which is
also the honest affordance for a row that costs a round trip to open. Anything with a `Click`
handler, and any `Button` whose content is a layout, is reachable with `type=Button:has-text(...)`.

**10. `Not.IsVisible` fails when the locator matches nothing, and it reads as though it would not.**
Found 2026-10-01 writing the quarantine run. The assertion was

```yaml
  - type: Expect
    target: "type=Button:has-text(Open message)"
    matcher: Not.IsVisible
```

after a release, meaning "the row is gone". It failed, after the full 5000 ms, with `expected
'type=Button:has-text(Open message)' to not(be visible)` and `Last seen: Locator ... did not match
any control`. So a locator that resolves to no control is a failure whichever polarity the matcher
has, and "not visible" is not a description of "not there". A locator is a *query*, and the harness
refuses to answer a question about a control it could not find.

The cost is a false red with a plausible story: the step above it had just asserted the listing
empty, so "the row is gone but the assertion about it failed" reads as the app keeping the row
somewhere. Nothing to do with the app, and the useful assertion was already the one that passed.

**The rule: assert absence on a named control that exists and is hidden by a binding.** `name=` is a
direct lookup, so the control is found and its `IsVisible` can be answered; `name=MessageList` (gated
on `HasMessages`), `name=EmptyListBlock` and `name=ReleaseMessageButton` (gated on a selection) are
all of that shape. `type=…:has-text(…)` is for *finding* a control to act on, not for asserting that
one is absent. This is lesson 1 from the other side: there the trap was reading a hidden control's
`IsVisible` as effective visibility, here it is asking about a control that is not in the tree at all.

## The runner's exit code is not the run's verdict

Every runner here used to end `exit $STATUS` on the status of `dotnet run`, and that status does not
carry the verdict. Measured 2026-10-01: the quarantine run's twenty-sixth action failed, the harness
printed `Result: FAIL`, wrote `"success": false` to `result.json`, and the process still exited 0. A
console that failed every assertion would have been reported to the fleet as a passing run: a green
tick with the evidence in a file nobody read. It is lesson 8 turned on the instrument itself, and it
is why a failing run and a passing run looked identical from outside.

So the verdict is read where it is written. `console_final_status <result.json> <process status>`
prints `success=…, N actions, M failed` with the failed actions named, and returns non-zero unless
the run passed; every runner here ends with it (ten of them at the time of writing, and the count is
worth re-deriving rather than trusting: `ls ux-scripts/run-console-*.sh | wc -l`). The process status
is still honoured, and a missing
`result.json` is a failure rather than an absence of one, because that is a run that never got far
enough to have a verdict. Read the file, never the exit code.

`./ux-scripts/check-runner-gate.sh` checks that gate rather than believing it: non-zero on the real
failing artifact kept at `quarantine-failed-result.json` (the failing run
that started all this), non-zero on a missing file, zero on a passing result. It needs no Host and no
build, and it refuses to report ok if it could not check all three shapes, because a check that
silently skips a shape is the same defect in a smaller place.

## A run that has finished must not look like a run still working

The same class as the exit code above, found on 2026-10-01 during a machine-wide memory condition that
had about twenty `lsof` processes, and then one Host, sitting in uninterruptible kernel sleep (state
`U`). A process in that state takes no signal at all: SIGTERM is queued and never delivered, the pid
stays in the table, and a `wait` on it never returns.

`console_stop_host` used `kill` and then an unbounded `wait`, and every runner reaches it through its
`EXIT` trap. So a Host that wedged there held the runner open forever. What it looked like from
outside: the no-feed runner had already printed the harness's own `The harness Host did not become
live within 90s.` and reached its verdict, then sat in teardown for another eight minutes while its
child stayed alive. A run that has decided and a run still driving the console were indistinguishable
in `ps`, which is exactly the problem the verdict-reading gate exists to solve, one layer down.

So the wait is bounded (`CONSOLE_HOST_STOP_WAIT`, 30s by default), the message names the pid and says
the condition is the machine's rather than the run's, and the process is left to expire on its own
instead of being retried. Two further consequences of the same wedge:

- **The key file is now removed before the kill rather than after the wait.** It used to be the last
  statement in that function, so a wedge meant this run's principal key stayed on disk for the whole
  life of the wedge, in the one path that is supposed to be the guarantee that a key does not outlive
  its run. The key is a credential and the process is not, so the file goes first.
- **The harness no longer uses `lsof` anywhere, and this bullet is a correction.** The first version
  of this section claimed "nothing here uses `lsof`" while `console_start_host` called it twice, on
  the start path of every runner: the sentence described a design the code did not have. The
  preflight is now `console_port_is_taken`, a `/dev/tcp` connect to `127.0.0.1:$CONSOLE_PORT` with no
  process in it to wedge, which is both what this section claimed and what the Host's own
  `ASPNETCORE_URLS` binds. The cost is that it can no longer print the holder, so the message points
  at `netstat -an | grep LISTEN | grep <port>` instead, and that it answers in three values: a listener
  accepts a connect, so "connected" is "taken" and "refused" is "free", while a probe that could not
  run at all is neither and says so. It takes an optional port argument, defaulting to
  `CONSOLE_PORT`, because a runner has to ask about an address other than the one it binds.
  Diagnostics that name a
  pid work with `ps`. `./ux-scripts/check-stop-host-bounded.sh` asserts that neither the harness nor any
  of the eleven runners invokes `lsof`, so the sentence and the code cannot drift apart again. That
  guard read the harness alone until 2026-10-01, while `run-console-address-change-smoke.sh` called
  `lsof` on the same run path the whole time, so the claim was tested over a population of one file.
  The runner's preflight now asks this same probe about its `SILENT_PORT` instead of the port it binds,
  which is also the narrower question: the address it saves is `http://127.0.0.1:5392`, and the `lsof`
  form it replaces matched a listener on any address.
- **A probe that cannot run must not answer "free".** This is the hole `nimble-` broadcast against
  their netstat gate on 2026-10-01: a probe that never executed is byte-identical to a quiet endpoint,
  so the dangerous failure is a dead probe read as a count of zero. The two-valued answer described
  above had the same hole at the other end of the pipe: a process with no descriptor left fails the
  connect for a reason that is not "nothing is listening", and the connect's failure read as FREE. The
  probe now returns 2 in that state, both callers refuse on 2 rather than starting a run, and the
  boundary is swept in the harness's own comment: at zero free descriptors over a live listener the
  retired body answered free and this one refuses, while at one or more free descriptors the two agree.
  Measured here, and still surviving, `CONSOLE_PORT=""` makes the `/dev/tcp` connect to `127.0.0.1:`
  fail while allocation is fine, which is still a read of FREE. Line 27's `${CONSOLE_PORT:-5271}`
  catches an empty value that
  arrived through the *environment*, so the reachable route is narrower than it first looked: an
  assignment made after this file is sourced, which is the order the header already tells runners to
  use. The probe now answers 2 for a port that is not a run of digits, so that route cannot read as
  free at all, and `console_start_host` keeps its own refusal in front of it because that one can say
  more: it names the value as the problem and says where to move the assignment, which a generic
  "cannot tell" cannot. The sibling's case 8 asserts
  the refusal, that the message says "not a port number", and that it does **not** say "already in
  use". Mutation H keeps that case load-bearing, and mutations I and J keep the third value itself
  load-bearing: I deletes the allocation test and J sticks the probe on "cannot tell", the arm that
  would otherwise satisfy case 9's refusal assertion while answering nothing about any port. P and Q
  keep the argument path load-bearing: P removes the numeric arm, so a port that is not a number falls
  through to the connect and reads as free, and Q spells the default so that an explicit empty argument
  falls back to `CONSOLE_PORT`, which is the bug that separating the unset and empty cases exists to
  prevent.

Both behaviours are checked without a Host or a build: `./ux-scripts/check-stop-host-bounded.sh` runs
nine cases in seconds, the first two against a child that ignores SIGTERM, and the old body was run beside
the new one to show that it still blocks where the new one returns
(`test-stop-host-bounded-is-load-bearing.py`, kept as the record of that
comparison rather than as a check to run). The stand-in waits for a flag file before anything signals
it, and that handshake is the whole difference between a check and a coin toss: `trap "" TERM` takes
effect only once the child has run it, so a signal in the first milliseconds kills a child that was
supposed to be unstoppable. Two drafts of that probe reported a false failure for exactly that reason.

## Nothing works without a Host

The console is an API client, so a script with no Host behind it asserts on a first-run state. The
shell script exists for that reason rather than for tidiness, and starting your own Host by hand is
the fastest way to make a run meaningless.

## What this harness cannot reach, and why

- **A quarantine release.** Measured rather than assumed, 2026-09-30: `POST /v1/submissions` does not
  accept and hold, it refuses, with `503 assessor_unavailable` when there is no assessor and `503
  deferred` when the assessor cannot reach its provider. So the empty queue on the shapes the
  committed smokes start is correct behaviour for a correct reason. On a Nimble-backed Host the route
  accepts (`202`) and a held message does reach the listing, so the missing thing is not a working
  provider but a message that scores at or above `QuarantineThreshold` (0.80): three single messages
  scored 0.48, 0.55 and 0.58. The evidence says the dimensions that could close that gap need sender
  history, which is traffic rather than a harness setting. The release route's refusal is covered
  here; its success path is covered only by unit tests, and no screenshot of it exists.
- **A message to its decision.** Reached: `run-console-nimble-smoke.sh`, from a held row the Host
  itself listed, over `POST /v1/submissions`. The committed smokes that start a Host which cannot
  assess still cannot start one, because a join needs a listed row and their listings are empty by
  design; that is a fact about those Hosts rather than about the join.
- **Two evidence rows that differ only by trend window.** The pane can tell them apart (they agree on
  signal id and scope, and the producer emits one row per window, "burst" and "slow"). What a Nimble
  Host was measured producing on 2026-09-30 is those rows in the *response*:
  `behavioural.trend.velocity` and `.acceleration` over two scopes, one row per window, each
  `Unavailable` with `sampleSupport: 0` until there is sender history. They are not drawn, and that
  is the same measurement seen from the other side: the pane renders a decision's evidence under the
  reasons that cite it, and no reason cites a trend signal while every one of them is unavailable, so
  the windowed rows never reach the screen. What reaches it needs sender history, which is traffic.
  What the smoke asserts today is the other half of the same contract, that the qualifier is *absent*
  on the unwindowed rows that make up most of a real response. The windowed rendering is covered by
  `DecisionViewTests`; no run reaches it.
- **Recovery after a long outage.** Closed for the console as of `run-console-long-outage-smoke.sh`:
  `run-console-feed-recovery-smoke.sh` proves the console announces the drop, keeps what it had read,
  and goes back to `Live` on its own when the Host returns inside SignalR's retry budget, and the
  long-outage run proves the other ending, where the budget is spent, the console stops retrying and
  stays stopped even after the Host is back, and only the operator's **Reconnect** restores the feed
  and the re-read. What neither run reaches is a Reconnect pressed while the Host is *still* away:
  the console settles into the feed's `Unreachable` state, renders "No live feed", and makes no
  further attempt, losing the stale warning at the same time. That is `state-coverage.md` item 8,
  proposed to `overview-` rather than built, because a retry is a product decision here.
- **Native OS dialogs.** There are none yet. When the API key entry lands it will open one, and that
  is the same wall mylo records: an `NSOpenPanel` is not an Avalonia control, so the harness can
  neither see nor click it.
- **A notice kind this build does not recognise.** Nothing a script can do puts a frame on the Hub:
  the actions are UI actions, the transport is the console's own, and a Host built from this tree
  announces kinds it and the console share. What the console does with an unreadable hint is
  therefore covered by `TrafficNoticeRoutingTests` rather than by a run, which is why that decision
  is a pure function in `Api/TrafficNoticeRouting.cs` and no longer a switch inside the window.

Stated rather than left to be discovered, because a script that silently skips a surface reads as
coverage.

## What the decision assertions actually stand on

A decision needs a semantic provider key, and a rejected key fails the whole assessment request
rather than degrading to unavailable evidence. So a run's decision comes from a Host whose provider
is deliberately unreachable: `console_seed_decision` posts to `/v1/assessments`, policy declines,
and the ledger records a decision whose semantic dimensions are all `Unavailable`.

That is a real decision, reached over the API by the client the console ships, and it is what makes
the `not measured (unavailable)` assertion honest: it is over data nothing wrote for the test. What
it does not establish is anything about a Host whose provider works. A cloud provider key is still
out of this harness's reach, but a working provider no longer is: `CONSOLE_PROVIDER=nimble` starts a
Host with a real local assessor, which is how the held message and the windowed evidence rows above
were reached. The committed smokes still start the unreachable-provider Host on purpose, because
their assertions are about a decision that could not be measured.


## The old `--screenshot` path is gone

There used to be a second, older way to photograph the console: `src/StyloMail.Desktop/Screenshot.cs`
behind `--screenshot <path>`, a dependency-free single-shot render. It overlapped with this harness
and did less, so it was deleted rather than maintained (`Program.cs` lost the flag, the branch in
`Main` and `BuildHeadlessApp`, which existed only for it). Nothing here ever used it.

Two pieces of it are worth remembering, because they are why it took a change of its own to remove.
It injected a decision body from a file (`STYLOMAIL_SMOKE_DECISION_FILE`), which was once the only way
to photograph the decision pane without a provider key; that input now lives in the app's Debug
startup path (`LoadHarnessDecisionAsync`, which prefers a decision the Host actually produced over the
fixture), so the flag was redundant the moment this harness could drive the pane over the API. And it
kept `Avalonia.Headless` referenced, which this harness also needs for `--ux-headless`: the package
stays, and its pin is still explained in `StyloMail.Desktop.csproj`.
