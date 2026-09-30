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
```

Run a script, not the YAML. Each sources `console-harness.sh`, which builds the solution it needs,
starts a throwaway Host on loopback with locally generated values, points the console at it, and takes
it down again on the way out, including on failure and on interrupt. That is what lets the assertions
be exact and the run be repeatable from any starting state.

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
`ux-results` wholesale, so running it deletes the other two scripts' screenshots: run those again
rather than reading a stale image.

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

## Nothing works without a Host

The console is an API client, so a script with no Host behind it asserts on a first-run state. The
shell script exists for that reason rather than for tidiness, and starting your own Host by hand is
the fastest way to make a run meaningless.

## What this harness cannot reach, and why

- **A quarantine release.** `POST /v1/submissions` declines an assessment when the semantic provider
  is unavailable, which is correct, so no message ever reaches a quarantined state in a run. The
  release route's refusal is covered here; its success path is covered only by unit tests, and no
  screenshot of it exists.
- **A message to its decision.** The ledger is listable and driven here, but the queue listings stay
  empty for the same reason, so the join from a message row to its decision has no row to start from.
- **Two evidence rows that differ only by trend window.** The pane can now tell them apart (they agree
  on signal id and scope, and the producer emits one row per window, "burst" and "slow"), but a run
  cannot reach them: the harness Host has no assessor, so its decision is a declined one whose
  evidence is entirely semantic. Behavioural evidence needs a working semantic provider. What the
  smoke does assert is the other half of the same contract, that the qualifier is *absent* on the
  unwindowed rows that make up most of a real response. The windowed case is covered by
  `DecisionViewTests`.
- **Recovery after a feed drops.** `run-console-feed-drop-smoke.sh` proves the console announces the
  drop and keeps what it had read. It does not prove the other half, that the console goes back to
  `Live` and re-reads the visible surface when the Host returns. That needs a Host that comes back on
  the same address with its key and database intact, which is a different harness rather than a
  longer one.
- **Native OS dialogs.** There are none yet. When the API key entry lands it will open one, and that
  is the same wall mylo records: an `NSOpenPanel` is not an Avalonia control, so the harness can
  neither see nor click it.

Stated rather than left to be discovered, because a script that silently skips a surface reads as
coverage.

## What the decision assertions actually stand on

A decision needs a semantic provider key, and a rejected key fails the whole assessment request
rather than degrading to unavailable evidence. So a run's decision comes from a Host whose provider
is deliberately unreachable: `console_seed_decision` posts to `/v1/assessments`, policy declines,
and the ledger records a decision whose semantic dimensions are all `Unavailable`.

That is a real decision, reached over the API by the client the console ships, and it is what makes
the `not measured (unavailable)` assertion honest: it is over data nothing wrote for the test. What
it does not establish is anything about a Host whose provider works. `docs/running.md` and the
provider key are what that needs, and neither is in this harness's reach.


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
