using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using StyloMail.Desktop.Services;
using StyloMail.Desktop.Views;

namespace StyloMail.Desktop;

/// <summary>
/// The application, and the one place the composition root is built and torn down.
/// </summary>
public sealed class App : Application
{
    private AppServices? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            InstallGlobalExceptionHooks();

            // The settings are loaded once and handed to the services, so the
            // connection screen can rebuild against a new address without the
            // window having to hold them separately.
            _services = AppServices.Create(
                ConsoleEnvironment.HostAddress(ConsoleEnvironment.Settings()),
                ConsoleEnvironment.Keychain(),
                settings: ConsoleEnvironment.Settings());

            desktop.MainWindow = new MainWindow(_services);

            // Synchronous on purpose, and the reason mylo gives for the same
            // decision: an async handler returns at its first await, and every
            // continuation it has queued then tries to resume on a dispatcher
            // that has already been shut down. The HttpClient would never be
            // disposed.
            desktop.ShutdownRequested += (_, _) => _services?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// A last line of defence. Without it an exception on a thread with no
    /// handler makes the window vanish with nothing written anywhere.
    /// </summary>
    private static void InstallGlobalExceptionHooks()
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Console.Error.WriteLine($"[UnobservedTask] {e.Exception.GetType().Name}: {e.Exception.Message}");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var text = e.ExceptionObject is Exception ex
                ? $"{ex.GetType().Name}: {ex.Message}"
                : e.ExceptionObject.ToString();

            Console.Error.WriteLine($"[Unhandled] {text}");
        };
    }
}
