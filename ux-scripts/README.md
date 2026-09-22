# Driving the operator console

Live UI tests for `src/StyloMail.Desktop`, powered by
[Mostlylucid.Avalonia.UITesting](../../lucidview/external/lucidRESUME/src/Mostlylucid.Avalonia.UITesting).
Modelled on mylo's `ux-scripts/`, including the repeatability rule.

The harness is **Debug-only**. It is not compiled into a Release build and adds no dependency there.

## Run it

```bash
./ux-scripts/run-console-smoke.sh
```

Run the script, not the YAML. `run-console-smoke.sh` sources `console-harness.sh`, which starts a
throwaway Host on loopback with locally generated values, points the console at it, and takes it down
again on the way out, including on failure and on interrupt. That is what lets the assertions be exact
and the run be repeatable from any starting state.

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

## Nothing works without a Host

The console is an API client, so a script with no Host behind it asserts on a first-run state. The
shell script exists for that reason rather than for tidiness, and starting your own Host by hand is
the fastest way to make a run meaningless.

## What this harness cannot reach, and why

- **Anything that needs a decision.** An assessment needs a semantic provider key; a rejected key
  currently fails the whole request rather than degrading to unavailable evidence, and this harness
  must not hold the operator's real key. So the decision pane, quarantine release and feedback are
  covered by their own unit tests, not here.
- **Native OS dialogs.** There are none yet. When the API key entry lands it will open one, and that
  is the same wall mylo records: an `NSOpenPanel` is not an Avalonia control, so the harness can
  neither see nor click it.

Both are stated rather than left to be discovered, because a script that silently skips a surface
reads as coverage.

## Relationship to `--screenshot`

`src/StyloMail.Desktop/Screenshot.cs` is an older, dependency-free single-shot capture behind
`--screenshot <path>`. It predates this harness and **overlaps with it**. Everything it does, this one
does better: it drives the real app rather than a one-shot render, and it can assert rather than only
photograph.

It survives only because it can inject a decision body from a file
(`STYLOMAIL_SMOKE_DECISION_FILE`), which is how the decision pane is photographed without a provider
key. Moving that input into the app's Debug startup path would let this harness drive the decision
pane too, and then the older one should be deleted rather than maintained as a second way to do one
thing.
