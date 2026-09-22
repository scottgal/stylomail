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

    public static async Task<int> CaptureAsync(string outputPath)
    {
        try
        {
            Program.BuildHeadlessApp().SetupWithoutStarting();

            var services = AppServices.Create(ConsoleEnvironment.HostAddress(), ConsoleEnvironment.Keychain());
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

            // One more set of jobs, so the bindings the status update raised
            // are laid out before the bitmap is taken.
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
    }
}
#endif
