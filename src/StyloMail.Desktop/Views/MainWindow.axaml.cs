using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;
using StyloMail.Desktop.Services;

namespace StyloMail.Desktop.Views;

/// <summary>
/// The console window.
/// </summary>
/// <remarks>
/// Deliberately thin: everything the window shows lives in
/// <see cref="ShellModel"/>, which has no Avalonia dependency and is therefore
/// testable without a display. What remains here is the one thing that is
/// genuinely the view's job, which is deciding <b>which thread</b> the model is
/// touched on.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly AppServices? _services;
    private readonly ShellModel _model;

    /// <summary>
    /// Completes once the window has loaded what it loads on open.
    /// </summary>
    /// <remarks>
    /// The window owns this, and callers observe it rather than repeating the
    /// work. Two callers each driving their own load is exactly how a duplicate
    /// appeared in the sidebar during a screenshot run.
    /// </remarks>
    private readonly TaskCompletionSource _initialLoad =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        // On Opened rather than in the constructor: a check started there would
        // run before the window exists and have nowhere to report to, and an
        // operator looking at an unrendered window is not waiting for it yet.
        Opened += async (_, _) =>
        {
            try
            {
                await RefreshHostAsync().ConfigureAwait(true);
                await LoadSendersAsync().ConfigureAwait(true);
                await LoadSelectionAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Nothing here should throw, because every call below converts
                // its failures into state. If something does, the window is
                // still up and usable, so it must not take the app down.
                Console.Error.WriteLine($"[Open] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _initialLoad.TrySetResult();
            }
        };
    }

    /// <summary>
    /// What the window is showing, and a way to drive the same selections an
    /// operator would make without the window needing a special path for being
    /// photographed.
    /// </summary>
    public ShellModel Model => _model;

    /// <summary>Completes when the window has finished loading what it loads on open.</summary>
    public Task InitialLoad => _initialLoad.Task;

    /// <summary>
    /// Selects a sidebar entry and loads what it lists, on the UI thread.
    /// </summary>
    /// <remarks>
    /// The click handler and the screenshot harness both go through here rather
    /// than assigning <see cref="ShellModel.SelectedItem"/> themselves. Setting
    /// the selection from a caller's thread leaves the binding unrefreshed: the
    /// first version of the harness did exactly that and photographed a window
    /// still showing the entry the window opens on, with the model correctly
    /// pointing somewhere else.
    /// </remarks>
    public async Task SelectAsync(SidebarItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await OnUiThreadAsync(() => _model.SelectedItem = item).ConfigureAwait(false);
        await LoadSelectionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks the Host how it is and puts the answer in the status bar.</summary>
    public async Task RefreshHostAsync(CancellationToken cancellationToken = default)
    {
        if (_services is null) return;

        var status = await _services.CheckHostAsync(cancellationToken).ConfigureAwait(false);

        await OnUiThreadAsync(() =>
        {
            _model.IsCheckingHost = false;
            _model.Status = status;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Fills the senders section.
    /// </summary>
    /// <remarks>
    /// A failure leaves the placeholder in place rather than clearing the
    /// section. The status bar already carries the reason, and a sidebar that
    /// emptied itself on a failed call would look like a tenant with no
    /// senders, which is a different and more alarming statement.
    /// </remarks>
    public async Task LoadSendersAsync(CancellationToken cancellationToken = default)
    {
        if (_services is null) return;

        SenderListingResponse listing;

        try
        {
            listing = await _services.Client.GetSendersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (StyloMailApiException)
        {
            // Reported by the status bar. Swallowed here so a first run with no
            // key opens a usable window rather than one that failed to load.
            return;
        }

        await OnUiThreadAsync(() => _model.ApplySenders(listing)).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads whatever the selected sidebar entry lists.
    /// </summary>
    /// <remarks>
    /// Only queue entries ask for anything. Selections that are not queues
    /// clear the list rather than leaving the previous queue's messages behind
    /// under a different title, which would attribute one pane's contents to
    /// another's heading.
    /// </remarks>
    public async Task LoadSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (_services is null) return;

        if (_model.SelectedItem?.Queue is not { } state)
        {
            await OnUiThreadAsync(() => _model.ApplyMessages(Empty)).ConfigureAwait(false);
            return;
        }

        MessageListingResponse listing;

        try
        {
            listing = await _services
                .Client
                .GetMessagesAsync(state, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StyloMailApiException)
        {
            // As above: the status bar carries why. An empty pane plus a stated
            // reason is the honest outcome; a thrown exception would take the
            // window down over a queue the operator can simply look at later.
            return;
        }

        await OnUiThreadAsync(() => _model.ApplyMessages(listing)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a model update on the UI thread, wherever the caller is.
    /// </summary>
    /// <remarks>
    /// <b>This is not defensive tidiness, it is a fix for a real defect found by
    /// photographing the window.</b> The continuations of the awaits above run on
    /// whichever context the caller had, and a caller with none resumes on the
    /// thread pool. Two loads then ran <c>ApplySenders</c> concurrently on
    /// different threads, against an <c>ObservableCollection</c> that is not
    /// thread-safe, and the senders section rendered three rows for one
    /// principal. Marshal the mutation explicitly and it cannot happen
    /// regardless of who called.
    /// </remarks>
    private static Task OnUiThreadAsync(Action update)
        => Dispatcher.UIThread.CheckAccess()
            ? RunInline(update)
            : Dispatcher.UIThread.InvokeAsync(update).GetTask();

    private static Task RunInline(Action update)
    {
        update();
        return Task.CompletedTask;
    }

    private static MessageListingResponse Empty => new()
    {
        TenantId = string.Empty,
        State = string.Empty,
        Messages = [],
        HasMore = false,
    };

    private async void OnSidebarItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarItem item }) return;

        await SelectAsync(item).ConfigureAwait(true);
    }
}
