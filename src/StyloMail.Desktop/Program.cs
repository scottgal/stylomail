using Avalonia;
using Avalonia.Data.Core.Plugins;
#if DEBUG
using Avalonia.Headless;
using Mostlylucid.Avalonia.UITesting;
#endif

namespace StyloMail.Desktop;

internal static class Program
{
    /// <summary>
    /// Renders the window to a PNG and exits. Debug only.
    /// </summary>
    private const string ScreenshotFlag = "--screenshot";

    /// <summary>
    /// Runs the harness with no window and no display, so a scripted run does
    /// not take keyboard focus on every launch.
    /// </summary>
    private const string UxHeadlessFlag = "--ux-headless";

    [STAThread]
    public static int Main(string[] args)
    {
        // Handled before Avalonia is given a lifetime, so it works on a machine
        // with no display and cannot be affected by anything the UI does.
#if DEBUG
        if (args.Length >= 2 && args[0] == ScreenshotFlag)
        {
            return Screenshot.CaptureAsync(args[1]).GetAwaiter().GetResult();
        }
#endif

        return BuildAvaloniaApp(args).StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp([]);

    public static AppBuilder BuildAvaloniaApp(string[] args)
    {
        BindingPlugins.DataValidators.Clear();

        var builder = AppBuilder.Configure<App>();

#if DEBUG
        // Driving the app on the native platform steals focus on every launch,
        // and a batch of scripts makes the machine unusable while it runs.
        // UseHeadlessDrawing stays false: true skips real drawing, so every
        // screenshot comes back blank and a script would still pass, which is
        // worse than a failure.
        builder = args.Contains(UxHeadlessFlag)
            ? builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia()
            : builder.UsePlatformDetect();
#else
        builder = builder.UsePlatformDetect();
#endif

        builder = builder.LogToTrace();

#if DEBUG
        // The one line that turns on the ux-test, ux-repl and ux-mcp modes.
        //
        // CaptureScreenshotsByDefault is false so that a run captures what its
        // script asks for and nothing else: a harness that photographs every
        // step produces a directory nobody reads, and the steps worth looking
        // at are the ones somebody chose.
        builder = builder.UseUITesting(opts =>
        {
            opts.DefaultScreenshotDir = "ux-screenshots";
            opts.Log = Console.WriteLine;
            opts.EnableCrossWindowTracking = true;
            opts.CaptureScreenshotsByDefault = false;
        });
#endif

        return builder.AfterSetup(_ => BindingPlugins.DataValidators.Clear());
    }

#if DEBUG
    /// <summary>
    /// The builder the screenshot harness uses.
    /// </summary>
    /// <remarks>
    /// Deliberately without <c>UseUITesting</c>. That harness drives the app
    /// through a lifetime and a startup event, and this one returns a frame and
    /// exits, so the two want different pipelines. Keeping them apart also
    /// means a fault in the UI harness cannot take out the capture that
    /// documents the console.
    /// </remarks>
    public static AppBuilder BuildHeadlessApp()
        => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .AfterSetup(_ => BindingPlugins.DataValidators.Clear());
#endif
}
