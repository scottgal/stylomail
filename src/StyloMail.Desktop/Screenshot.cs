#if DEBUG
using Avalonia.Headless;
using Avalonia.Threading;
using StyloMail.Desktop.Services;
using StyloMail.Desktop.Views;

namespace StyloMail.Desktop;

/// <summary>
/// Renders the console window to a PNG with no display attached.
/// </summary>
/// <remarks>
/// <b>Debug only, and a harness rather than a feature.</b> It exists so that
/// what the window renders can be checked by someone who cannot see it, and so
/// that a verification run does not put a window on screen and steal focus on
/// every build, which is what makes a batch of these unusable while it runs.
///
/// <para>
/// It talks to a real Host when one is configured, because a screenshot of a
/// console that cannot reach anything shows the empty state and not the window.
/// With no Host configured it still renders: the status bar reports the
/// unreachable Host, which is a state the console is designed to show rather
/// than a failure of the harness.
/// </para>
/// </remarks>
internal static class Screenshot
{
    /// <summary>
    /// How many dispatcher pumps to allow before giving up on the Host check.
    /// </summary>
    /// <remarks>
    /// A count rather than a wall-clock deadline, so this file computes no
    /// times. At 20ms a pump this is roughly twenty seconds, far longer than
    /// the five-second deadline the check itself applies, so it only expires if
    /// something is genuinely wedged.
    /// </remarks>
    private const int MaxPumps = 1000;

    private const int PumpIntervalMs = 20;

    /// <summary>
    /// How many dispatcher passes to allow after the last mutation before the
    /// frame is taken. A change made as the load finished needs at least one
    /// layout pass to appear.
    /// </summary>
    private const int SettlePumps = 8;

    public static async Task<int> CaptureAsync(string outputPath)
    {
        try
        {
            Program.BuildHeadlessApp().SetupWithoutStarting();

            var harnessSettings = ConsoleEnvironment.Settings();

            var services = AppServices.Create(
                ConsoleEnvironment.HostAddress(harnessSettings),
                ConsoleEnvironment.Keychain(),
                settings: harnessSettings);
            var window = new MainWindow(services);

            window.Show();
            Dispatcher.UIThread.RunJobs();

            // The window refreshes the Host on Opened, which fires on the
            // dispatcher. Nothing is pumping that dispatcher yet, so the
            // continuation of that async handler is queued and waiting. Without
            // the pump below, this capture shows the "Not checked yet" status
            // bar: a true statement about a window one frame old, and a useless
            // thing to photograph.
            //
            // Pumped rather than awaited. Awaiting here would deadlock, because
            // the continuation posts back to the very dispatcher we are
            // blocking, which is exactly how the first version of this harness
            // hung with no output at all.
            var loaded = LoadEverything(window);
            var pumps = 0;

            while (!loaded.IsCompleted && pumps < MaxPumps)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(PumpIntervalMs);
                pumps++;
            }

            // Let layout settle before the bitmap is taken, and give it more
            // than one pass.
            //
            // Measured, not guessed: a single RunJobs here was enough for
            // everything the window loads on open, and not enough for a change
            // made in the final step. A confirmation bar set visible at the end
            // of the load was present in the model, absent from the bitmap, and
            // its text bindings were blank: the notification had been raised,
            // the layout pass had not run. The capture is the verification, so
            // it has to show the state after the last mutation rather than the
            // state one frame before it.
            for (var settle = 0; settle < SettlePumps; settle++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(PumpIntervalMs);
            }

            // A load that threw leaves the loop above looking exactly like one
            // that finished, because the loop only asks whether the task is
            // complete. The frame would then be captured from a half-loaded
            // window and written out as if it meant something, which is how a
            // fault in this harness cost an afternoon: the diagnostic that
            // would have explained it never ran, and the picture looked
            // plausible. Report and refuse instead.
            if (loaded.IsFaulted)
            {
                Console.Error.WriteLine($"[Screenshot] The window did not finish loading: {loaded.Exception?.GetBaseException().Message}");
                return 1;
            }

            // Force a layout pass before capturing, and ask for it explicitly.
            //
            // Measured, not guessed. With the manual pump alone, a panel that
            // became visible as the load finished was present in the model,
            // raised its notification, and was absent from the bitmap: the
            // headless render path does not drive the layout manager the way a
            // real window's render loop does, so the pass has to be asked for.
            // A capture that silently misses the last state change is worse
            // than no capture, because it still looks like one.
            window.InvalidateMeasure();
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame();

            if (frame is null)
            {
                Console.Error.WriteLine("[Screenshot] No frame was rendered.");
                return 1;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            frame.Save(outputPath);
            Console.WriteLine($"[Screenshot] wrote {outputPath}");

            services.Dispose();

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Screenshot] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Waits for what the window loads on open, then selects a queue.
    /// </summary>
    /// <remarks>
    /// The waiting is the important part, and it is a fix rather than a
    /// formality. An earlier version drove the loads itself, which meant two
    /// callers doing the same work at once, and the sidebar rendered three rows
    /// for one principal. The window owns its own load now; this observes it.
    ///
    /// <para>
    /// Selecting a queue matters for the capture. Left on the Host entry the
    /// list pane shows the connection explanation, which is the state the
    /// window opens in and not the one a screenshot is for.
    /// </para>
    /// </remarks>
    private static async Task LoadEverything(MainWindow window)
    {
        await window.InitialLoad.ConfigureAwait(false);
        var model = window.Model;

        var queue = model.Sections
            .SelectMany(section => section.Items)
            .FirstOrDefault(item => item.Queue is not null);

        if (queue is null) return;

        await window.SelectAsync(queue).ConfigureAwait(false);

        await ShowDecisionIfAsked(window).ConfigureAwait(false);
        await ShowActionIfAsked(window).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the confirmation for a pause, so the dialog can be photographed.
    /// </summary>
    /// <remarks>
    /// It only <em>asks</em>. Nothing is carried out, because asking and doing
    /// are separate steps and this is the asking one: with the flag set the
    /// window shows the confirmation and the Confirm button stays disabled
    /// until a reason is typed, so a capture cannot accidentally act on the
    /// Host it is pointed at.
    /// </remarks>
    private const string ActionVariable = "STYLOMAIL_SMOKE_ACTION";

    private static async Task ShowActionIfAsked(MainWindow window)
    {
        if (Environment.GetEnvironmentVariable(ActionVariable) != "1") return;

        var sender = window.Model.Sections
            .SingleOrDefault(section => section.Title == "Senders")
            ?.Items
            .FirstOrDefault(item => item.CanPause);

        // Through the window, which marshals. Assigning the model directly from
        // this thread produced a confirmation that was pending in the model and
        // absent from the window: the binding never heard about it.
        if (sender is not null) await window.RequestPauseAsync(sender).ConfigureAwait(false);
    }

    /// <summary>
    /// Environment variable naming a decision body to render, for photographing
    /// the detail pane.
    /// </summary>
    /// <remarks>
    /// <b>A harness input, and a temporary one.</b> It exists because the
    /// decision pane cannot be photographed on a deployment without a semantic
    /// provider key: an assessment needs Jev, a rejected key currently fails the
    /// whole request rather than degrading to unavailable evidence, and the
    /// operator's real key is not something this harness may hold.
    ///
    /// <para>
    /// So the pane is photographed against the same wire body the contract
    /// tests are written from, and that is stated rather than implied. When the
    /// Host can reach a ledger without a provider key, this goes away.
    /// </para>
    /// </remarks>
    private const string DecisionFileVariable = "STYLOMAIL_SMOKE_DECISION_FILE";

    private static async Task ShowDecisionIfAsked(MainWindow window)
    {
        var path = Environment.GetEnvironmentVariable(DecisionFileVariable);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);

        var decision = System.Text.Json.JsonSerializer.Deserialize<Api.Contracts.DecisionResponse>(
            json,
            Api.StyloMailApiClient.JsonOptions);

        if (decision is null)
        {
            Console.Error.WriteLine($"[Screenshot] {path} did not bind as a decision.");
            return;
        }

        await window.ShowDecisionAsync(decision).ConfigureAwait(false);
    }
}
#endif
