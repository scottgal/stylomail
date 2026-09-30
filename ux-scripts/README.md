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

./ux-scripts/probe-submission-route.sh       # not a smoke: measures what the routes answer
./ux-scripts/check-runner-gate.sh            # not a smoke: checks the runner gate itself
```

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
`/tmp/stylomail-console-build/solution.log`, so a failure is readable after the fact; the three
scripts used to discard it, which turned a build error into exit 1 and a 0-byte log.

Each script owns its own Host, its own `CONSOLE_RUN` scratch directory and its own output directory
under `ux-results/`, so one run's artifacts can never be read as another's. The main smoke wipes
`ux-results` wholesale, so running it deletes the other scripts' screenshots: run those again rather
than reading a stale image.

## The live feed's three states

The feed is one surface with three states, and they are the whole reason there are three scripts. A
console following a Host and a console that never had anything to follow look identical in every pane
and mean opposite things about whether what is on screen is current, so each state gets an assertion
rather than an assumption.

| State | Reached by | Script | Says |
| --- | --- | --- | --- |
| Following | Hub mapped (`StyloMail__Traffic__Enabled=true`) | `run-console-smoke.sh` | `Live` |
| No feed | Hub absent, which is a real deployment's default | `run-console-no-feed-smoke.sh` | `No live feed`, and **not** stale |
| Dropped | a feed that was Live stops | `run-console-feed-drop-smoke.sh` | `Live updates stopped, screen may be out of date`, in amber |

`console-harness.sh` reads `CONSOLE_TRAFFIC` when it starts the Host: `true` (the default) maps the
Hub, `false` starts a Host without it. The third state cannot be reached from a YAML at all, because
the harness has no shell-out action and nothing in a script can stop a process, so
`run-console-feed-drop-smoke.sh` kills the Host from outside. It waits for the screenshot that proves
the console is Live before killing, rather than sleeping a fixed amount: a fixed sleep would be the
one thing here that makes the test flaky, and flaky is the same as absent for a proof.

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
of entry point does not need an edit here. The switch is wired but **no committed runner calls it
yet**, and it has never been exercised end to end under a smoke: which script seeds a batch, and what
it then asserts, is the next step. A verified invocation is not a verified run.

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
    --ux-headless --ux-test --script ux-scripts/console-smoke.yaml --output ux-results

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
   **before** the `source` - including `CONSOLE_RUN`. The harness assigns `CONSOLE_RUN` itself, so a
   runner that sets it afterwards with `${CONSOLE_RUN:-...}` keeps the harness's value and silently
   shares the main smoke's scratch directory. That is two runs writing one tree, which the comment in
   each runner says cannot happen. It did, in all three of them, until the not-ready script was
   written and its `ready.json` turned up in another script's directory.

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
the run passed; all six runners end with it. The process status is still honoured, and a missing
`result.json` is a failure rather than an absence of one, because that is a run that never got far
enough to have a verdict. Read the file, never the exit code.

`./ux-scripts/check-runner-gate.sh` checks that gate rather than believing it: non-zero on the real
failing artifact kept at `.styloagent/scratch/desktop/quarantine-failed-result.json` (the failing run
that started all this), non-zero on a missing file, zero on a passing result. It needs no Host and no
build, and it refuses to report ok if it could not check all three shapes, because a check that
silently skips a shape is the same defect in a smaller place.

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
- **Recovery after a feed drops.** `run-console-feed-drop-smoke.sh` proves the console announces the
  drop and keeps what it had read. It does not prove the other half, that the console goes back to
  `Live` and re-reads the visible surface when the Host returns. That needs a Host that comes back on
  the same address with its key and database intact, which is a different harness rather than a
  longer one.
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
