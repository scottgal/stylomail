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

## Four things this harness taught us, all found by running it

**1. A locator matches controls that are not visible.** The first version targeted the pause button by
its text and reached a hidden one on a different row, because every row has the control and only one
row shows it. Naming the target removes the ambiguity rather than depending on the order the visual
tree happens to be in.

**2. `Control.IsVisible` is not effective visibility.** It is the control's own property, so a
`TextBlock` inside a hidden bar still reports `IsVisible=true` and an assertion on it passes whether
the bar is there or not. Assert on the container. This one showed up as a failure where the app was
right and the test was wrong.

**3. A `Button` whose content is a layout, not a string, does not match `text=`.** The sidebar rows
contain a `Grid`, so `text=Quarantined` finds the `TextBlock` and never the button. Use
`type=Button:has-text(Quarantined)`.

**4. Nothing works without the Host.** The console is an API client, so a script with no Host behind
it asserts on a first-run state. The shell script exists for that reason rather than for tidiness.

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
