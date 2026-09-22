using Avalonia;
using Avalonia.Data.Core.Plugins;
#if DEBUG
using Avalonia.Headless;
#endif

namespace StyloMail.Desktop;

internal static class Program
{
    /// <summary>
    /// Renders the window to a PNG and exits.
    /// </summary>
    /// <remarks>
    /// The flag is handled before Avalonia is given a lifetime, so it works on
    /// a machine with no display and cannot be affected by anything the UI
    /// does. Debug only: a Release build has no such flag.
    /// </remarks>
    private const string ScreenshotFlag = "--screenshot";

    [STAThread]
    public static int Main(string[] args)
    {
#if DEBUG
        if (args.Length >= 2 && args[0] == ScreenshotFlag)
        {
            return Screenshot.CaptureAsync(args[1]).GetAwaiter().GetResult();
        }
#endif

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .AfterSetup(_ => BindingPlugins.DataValidators.Clear());

#if DEBUG
    /// <summary>
    /// The builder a screenshot run uses.
    /// </summary>
    /// <remarks>
    /// <c>UseHeadlessDrawing</c> is set to false, which reads backwards and is
    /// not a typo. False means "do not use the headless drawing stub", so real
    /// Skia drawing runs and the capture has something in it; leaving it at its
    /// default produces a blank bitmap, which is worse than useless because the
    /// run still succeeds. mylo records the same finding after losing time to
    /// it, which is why it is written down in both places.
    /// </remarks>
    public static AppBuilder BuildHeadlessApp()
        => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .AfterSetup(_ => BindingPlugins.DataValidators.Clear());
#endif
}
