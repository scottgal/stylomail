using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StyloMail.Desktop.Models;
using StyloMail.Desktop.Services;

namespace StyloMail.Desktop.Views;

/// <summary>
/// The console window.
/// </summary>
/// <remarks>
/// Deliberately thin. Everything the window shows lives in
/// <see cref="ShellModel"/>, which has no Avalonia dependency and is therefore
/// testable without a display; the code here is limited to wiring events to it.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly AppServices? _services;
    private readonly ShellModel _model;

    /// <summary>
    /// The parameterless constructor exists for the XAML loader and for a
    /// headless render, which has no Host to talk to and must still be able to
    /// build the window.
    /// </summary>
    public MainWindow()
        : this(services: null)
    {
    }

    public MainWindow(AppServices? services)
    {
        _services = services;

        // The address is passed rather than left to the model's default. Its
        // test asserted the property, not that the window supplies one, so
        // omitting this left the right-hand side of the status bar rendering
        // blank with every test still green.
        _model = ShellModel.CreateDefault(services?.HostAddress);

        AvaloniaXamlLoader.Load(this);

        DataContext = _model;

        // On Opened rather than in the constructor: a check started here would
        // run before the window exists and have nowhere to report to, and an
        // operator looking at an unrendered window is not waiting for it yet.
        Opened += async (_, _) => await RefreshHostAsync().ConfigureAwait(true);
    }

    /// <summary>Asks the Host how it is and puts the answer in the status bar.</summary>
    public async Task RefreshHostAsync(CancellationToken cancellationToken = default)
    {
        if (_services is null) return;

        _model.IsCheckingHost = true;

        try
        {
            // CheckHostAsync turns every expected failure into a status, so
            // this needs no catch of its own: the only thing that reaches here
            // is the caller's own cancellation, which the finally still resets.
            _model.Status = await _services.CheckHostAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _model.IsCheckingHost = false;
        }
    }

    private void OnSidebarItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SidebarItem item })
        {
            _model.SelectedItem = item;
        }
    }
}
