# Rendered screenshots as a verification step

Written by `desktop-` on 2026-09-22, at `overview-`'s request, after a rendered screenshot found six
defects in one day that no test did. The files in this directory are the artefacts; this is the method.

## Why this is a verification step and not documentation

A test asserts a claim about a model. "The window shows it" is a different claim, and a thorough suite
of model assertions will pass with the window completely blank. Every defect below was in that gap:

| Defect | Why no test caught it |
| --- | --- |
| Senders section rendered three rows for one principal | Two concurrent loads mutated a non-thread-safe `ObservableCollection`. The API returned one row; the model held three; the render showed three. |
| Pane header read "Not checked yet" above a status bar reading "Connected" | The model was correct. The binding simply never re-evaluated. |
| Right-hand side of the status bar rendered blank | The model had the property and a test for it. The window never passed one. |
| First run said "the Host refused the request" when the truth was "no API key set" | The code said one thing, the truth was another, and only rendering it showed the console would mislead an operator about why it was not working. |
| A confirmation panel pending in the model and absent from the window | The model raised `PropertyChanged` with the right value. The visual never changed. |
| A harness that captured a frame from a load that had thrown | The harness looked like it had worked. |

Not one of those is reachable by asserting on a model. If you are building anything with a surface,
put a rendered capture in the path and look at it.

## The pattern, in about eighty lines

Debug-only. It renders into a bitmap with no display attached and no window on screen, which matters
because a verification step that steals keyboard focus on every build is one nobody runs in a batch.

1. **A `--screenshot <path>` flag handled before Avalonia is given a lifetime**, so it works on a
   machine with no display and cannot be affected by anything the UI does.
2. **`UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })` then `UseSkia()`.**
   `false` reads backwards and is not a typo: false means "do not use the headless drawing stub", so
   real Skia drawing runs. Left at the default you get a blank bitmap and the run still succeeds,
   which is worse than useless. `mylo` records the same finding.
3. **`SetupWithoutStarting()`**, then build the window and `Show()`.
4. **Pump the dispatcher by hand.** Nothing runs the loop for you, so drive it:
   `while (!work.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }`
5. **Await nothing on the UI thread from outside.** An `await` there deadlocks, because the
   continuation posts back to the dispatcher you are blocking. This hung the first version with no
   output at all.
6. **`InvalidateMeasure()` before capturing.** See gotcha 2 below.
7. **`CaptureRenderedFrame()` and save.**

The reference implementation is `src/StyloMail.Desktop/Screenshot.cs`. Copy the shape rather than the
file; the comments there explain the reasoning at each step and are worth reading before adapting it.

## Four gotchas, each of which cost real time

**1. Surface a faulted load, or your harness will lie to you.**

```csharp
if (work.IsFaulted) { Report(work.Exception); return 1; }
```

The pump loop only asks whether the task completed, so a load that threw looks exactly like one that
finished. The frame is then captured from a half-loaded window and written out as though it meant
something. This cost an afternoon: the diagnostic that would have explained the problem never ran, and
the picture looked plausible. A verification harness that can silently capture a broken state is worse
than none.

**2. An `IsVisible` change on a docked child needs an explicit layout pass.**

The headless render path does not drive the layout manager the way a real window's render loop does.
Without `window.InvalidateMeasure(); Dispatcher.UIThread.RunJobs();` immediately before the capture, a
panel that became visible as the load finished is present in the model, has raised its notification,
and is absent from the bitmap. Two guesses at this were wrong before the measurement settled it.

**3. Never touch the window from a non-UI thread except through its own methods.**

`window.DataContext` is an Avalonia property and reading it off-thread throws "Call from invalid
thread". A plain model object is safe to read. This one is self-inflicted and easy to do while adding
a diagnostic.

**4. The bug that will bite the app itself, not just the harness.**

**A model mutation made from a background continuation raises `PropertyChanged` off the UI thread.
The model is correct, the notification fires, and the visual never changes.** This appeared three
times in one window and passed every model test each time. Route every mutation through the view:

```csharp
private static Task OnUiThreadAsync(Action update)
    => Dispatcher.UIThread.CheckAccess()
        ? RunInline(update)
        : Dispatcher.UIThread.InvokeAsync(update).GetTask();
```

Prove a mutation landed by reading state back from the *system*, not the response: a write that
answers `paused: true` without anything changing passes every stubbed test.

## What a capture does not prove

It proves what rendered, once, for the state you set up. It does not prove the state was reachable by
a click, that the numbers were right, or that the same screen is right on another DPI or theme. Pair
it with the model tests rather than replacing them with it: the model tests are cheap, fast and
exact, and this is the one that catches the class they cannot reach.
