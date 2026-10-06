using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.Globalization;
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
    /// <summary>
    /// Not readonly: the connection screen replaces it when the address or the
    /// key changes, because both are baked into the client and the connection
    /// pool it owns.
    /// </summary>
    private AppServices? _services;

    /// <summary>
    /// The subscription to the Host's live feed, when there is one.
    /// </summary>
    /// <remarks>
    /// Owned by the window rather than by the services, because a notice's
    /// whole consequence is a read of what is on screen, and only the window
    /// knows what that is. Replaced on reconnect, since a feed to the previous
    /// Host is a feed to the wrong Host.
    /// </remarks>
    private TrafficFeed? _feed;

    /// <summary>
    /// The connect an operator asked for that is running, or null when none is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard behind the second of the three conditions a bounded operator
    /// retry has to meet: a press while a sequence is running must not start a
    /// second one. Without it a press during the thirty-second wait would
    /// rebuild the feed and start the count again, so impatience would keep the
    /// console knocking indefinitely, which is the opposite of bounded.
    /// </para>
    /// <para>
    /// <b>An identity rather than a flag</b>, because two sequences can overlap.
    /// The connection screen's save supersedes one that is running, and a bool
    /// would then be cleared by the superseded sequence's own unwind, leaving
    /// the live one unguarded. Each sequence clears only the ticket it set.
    /// </para>
    /// </remarks>
    private object? _connectInFlight;

    private readonly ShellModel _model;

    /// <summary>
    /// Set while a row's open button is assigning the selection itself.
    /// </summary>
    /// <remarks>
    /// The button selects the row and then loads its decision, and the
    /// selection raises <c>SelectionChanged</c> when the row was not already
    /// selected. Without this the same row would be fetched twice, once by the
    /// event and once by the click, which is two requests for one intention.
    /// </remarks>
    private bool _openingMessageFromRowButton;

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
#if DEBUG
                await LoadHarnessDecisionAsync().ConfigureAwait(true);
#endif
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

            // After the first load, not during it. A notice that arrived while
            // the window was still filling itself would race that load and
            // report a read against a screen that was not up yet.
            await StartTrafficFeedAsync().ConfigureAwait(true);
        };
    }

    /// <summary>The sidebar entry that opens the connection screen.</summary>
    public const string ConnectionSectionTitle = "Connection";

    /// <summary>The sidebar entry that opens the company screen.</summary>
    public const string CompaniesSectionTitle = "Companies";

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
    /// <returns>Whether the Host answered at all.</returns>
    /// <remarks>
    /// The answer is returned rather than only rendered because one caller
    /// needs to tell a read that landed from one that did not: the live feed's
    /// resynchronisation, which may only stop claiming the screen might be out
    /// of date once it has actually read the Host. A Host that refused or did
    /// not answer has told this console nothing, so it cannot be the read that
    /// makes anything current.
    /// </remarks>
    public async Task<bool> RefreshHostAsync(CancellationToken cancellationToken = default)
    {
        if (_services is null) return false;

        var status = await _services.CheckHostAsync(cancellationToken).ConfigureAwait(false);

        await OnUiThreadAsync(() =>
        {
            _model.IsCheckingHost = false;
            _model.Status = status;
        }).ConfigureAwait(false);

        return status.IsReachable;
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
    /// <returns>Whether the listing was read. See the note on the live feed.</returns>
    public async Task<bool> LoadSendersAsync(CancellationToken cancellationToken = default)
    {
        if (_services is null) return false;

        SenderListingResponse listing;

        try
        {
            listing = await _services.Client.GetSendersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (StyloMailApiException failure)
        {
            // Reported by the status bar *and* on the sidebar entry, and logged.
            //
            // This used to return silently on the reasoning that the status bar
            // already said why. It does not say why this failed: a 403 from a
            // missing privilege and a 500 from the Host look identical in a
            // status bar reading "Connected", and the sidebar sat on its
            // "Loading" placeholder forever, which reads as a console that is
            // still working. Swallowing the reason is the silent failure this
            // project keeps finding, and it was in my own lane.
            Console.Error.WriteLine($"[Senders] {failure.Failure}: {failure.Message}");

            await OnUiThreadAsync(() => _model.SendersUnavailable(failure)).ConfigureAwait(false);
            return false;
        }

        // The company list is a second call, and a failure there must not lose
        // the senders. Null means "could not read them", which the model turns
        // into one plain section rather than a heading per unreadable id.
        CompanyListingResponse? companies = null;

        try
        {
            companies = await _services.Client.GetCompaniesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (StyloMailApiException failure)
        {
            Console.Error.WriteLine($"[Companies] {failure.Failure}: {failure.Message}");
        }

        await OnUiThreadAsync(() => _model.ApplySenders(listing, companies?.Companies))
            .ConfigureAwait(false);

        return true;
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
    /// <returns>Whether what the pane shows was read from the Host.</returns>
    public async Task<bool> LoadSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (_services is null) return false;

        if (_model.SelectedItem?.Ledger is { } ledger)
        {
            return await LoadLedgerAsync(ledger, cancellationToken).ConfigureAwait(false);
        }

        if (_model.SelectedItem?.Queue is not { } state)
        {
            // Nothing to read: the destination lists nothing, and the pane says
            // so. That is a pane known to be current rather than one that was
            // never filled.
            await OnUiThreadAsync(() => _model.ApplyMessages(Empty)).ConfigureAwait(false);
            return true;
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
            return false;
        }

        await OnUiThreadAsync(() => _model.ApplyMessages(listing)).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Fills the middle pane from the decision ledger.
    /// </summary>
    /// <remarks>
    /// A failure is reported on the pane as well as the status bar, because an
    /// empty ledger and an unreadable one are the same picture and different
    /// facts. The ledger is the one listing whose emptiness is a claim about
    /// the whole system ("nothing has been assessed"), so it is the one where
    /// getting that wrong matters most.
    /// </remarks>
    private async Task<bool> LoadLedgerAsync(LedgerListing ledger, CancellationToken cancellationToken)
    {
        await OnUiThreadAsync(_model.BeginLedgerLookup).ConfigureAwait(false);

        DecisionListingResponse listing;

        try
        {
            listing = await _services!
                .Client
                .GetDecisionsAsync(action: ledger.Action, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StyloMailApiException failure)
        {
            Console.Error.WriteLine($"[Ledger] {failure.Failure}: {failure.Message}");

            await OnUiThreadAsync(_model.LedgerUnavailable).ConfigureAwait(false);
            return false;
        }

        await OnUiThreadAsync(() => _model.ApplyDecisions(listing)).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Opens a decision by its assessment id and shows it in the detail pane.
    /// </summary>
    /// <remarks>
    /// Both routes into a decision end here: the ledger lists summaries and a
    /// row carries the id that fetches the full explanation, and a message row
    /// reaches it through the ledger filtered by its internal message id.
    /// </remarks>
    public async Task<bool> OpenDecisionAsync(string assessmentId, CancellationToken cancellationToken = default)
    {
        if (_services is null) return false;

        try
        {
            var decision = await _services.Client.GetDecisionAsync(assessmentId, cancellationToken).ConfigureAwait(false);

            await OnUiThreadAsync(() => _model.ShowDecision(decision)).ConfigureAwait(false);

            return true;
        }
        catch (StyloMailApiException)
        {
            return false;
        }
    }

#if DEBUG
    /// <summary>
    /// Opens a decision body from a file, so the UI harness can drive the pane.
    /// </summary>
    /// <remarks>
    /// <b>A harness input, and the reason it lives here rather than in a
    /// harness.</b> The decision pane is the console's headline surface and no
    /// route can reach it without a decision to show, which needs a semantic
    /// provider key that a test run must never hold. Without this the pane is
    /// the one part of the console a driving script cannot touch, and a script
    /// that silently covers everything except the part that matters reads as
    /// coverage.
    ///
    /// <para>
    /// Debug only, and gated on an environment variable: the same shape as the
    /// key override in <c>ConsoleEnvironment</c>. A Release build has no such
    /// path, and nothing in the console can reach it by accident.
    /// </para>
    /// </remarks>
    private async Task LoadHarnessDecisionAsync(CancellationToken cancellationToken = default)
    {
        // A decision the Host actually produced is preferred over the fixture,
        // and the preference is the point: a live decision exercises the client,
        // the contract and the pane together, where a fixture exercises the pane
        // alone. It also means the pane's assertions are about real evidence,
        // including the dimensions a real outage leaves Unavailable.
        var live = Environment.GetEnvironmentVariable("STYLOMAIL_SMOKE_DECISION_ID");

        if (!string.IsNullOrWhiteSpace(live))
        {
            if (await OpenDecisionAsync(live, cancellationToken).ConfigureAwait(true)) return;

            Console.Error.WriteLine(
                $"[Harness] This Host has no decision {live}; falling back to the fixture.");
        }

        var path = Environment.GetEnvironmentVariable("STYLOMAIL_SMOKE_DECISION_FILE");

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

            var decision = System.Text.Json.JsonSerializer.Deserialize<DecisionResponse>(
                json,
                StyloMailApiClient.JsonOptions);

            if (decision is null)
            {
                Console.Error.WriteLine($"[Harness] {path} did not bind as a decision.");
                return;
            }

            await OnUiThreadAsync(() => _model.ShowDecision(decision, isFixture: true)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"[Harness] Could not load the decision fixture: {ex.Message}");
        }
    }
#endif

    /// <summary>
    /// Loads the decision behind the selected message, in two hops.
    /// </summary>
    /// <remarks>
    /// <b>The console's headline flow.</b> A message row carries an internal
    /// message id; the ledger is filtered by it to get summaries; the newest
    /// summary's assessment id fetches the full explanation with evidence. Two
    /// requests rather than one, because the ledger returns a list: a message
    /// can be assessed more than once, and a single id on the row could only
    /// have held one of them.
    ///
    /// <para>
    /// An empty list is a fact, not a failure, and the pane says which of the
    /// two it is. "This message has no decisions" and "the lookup failed" have
    /// different remedies, and a pane that showed its generic empty text for
    /// both would send an operator to look at the wrong thing.
    /// </para>
    /// </remarks>
    public async Task LoadDecisionForSelectedMessageAsync(CancellationToken cancellationToken = default)
    {
        if (_services is null) return;

        var message = _model.SelectedMessage;

        if (message is null) return;

        if (string.IsNullOrEmpty(message.InternalMessageId))
        {
            // The Host stopped sending the join key, which is a contract
            // change rather than a message without a decision. Saying so beats
            // showing the "no decisions" text, which would be a wrong answer.
            await OnUiThreadAsync(_model.DecisionLookupFailed).ConfigureAwait(false);
            return;
        }

        await OnUiThreadAsync(_model.BeginDecisionLookup).ConfigureAwait(false);

        try
        {
            var listing = await _services.Client
                .GetDecisionsAsync(message.InternalMessageId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (listing.Decisions.Count == 0)
            {
                await OnUiThreadAsync(_model.NoDecisionsForMessage).ConfigureAwait(false);
                return;
            }

            var newest = listing.Decisions[0];

            var decision = await _services.Client
                .GetDecisionAsync(newest.AssessmentId, cancellationToken)
                .ConfigureAwait(false);

            await OnUiThreadAsync(() => _model.ShowDecision(decision, listing.Decisions.Count))
                .ConfigureAwait(false);
        }
        catch (StyloMailApiException failure)
        {
            Console.Error.WriteLine($"[Decision] {failure.Failure}: {failure.Message}");

            await OnUiThreadAsync(_model.DecisionLookupFailed).ConfigureAwait(false);
        }
    }

    /// <summary>Shows a decision the caller already has. Used by the UI harness.</summary>
    public Task ShowDecisionAsync(DecisionResponse decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return OnUiThreadAsync(() => _model.ShowDecision(decision));
    }

    // ===================== write actions =====================

    /// <summary>
    /// Asks to pause a principal. Marshalled, like every other model update.
    /// </summary>
    /// <remarks>
    /// <b>Every mutation goes through one of these or through
    /// <see cref="OnUiThreadAsync"/>.</b> That is not a style rule: the third
    /// occurrence of the same defect was a caller assigning the model directly
    /// from a background continuation, and the only symptom was a binding that
    /// never refreshed. The window is the single place that knows which thread
    /// the model lives on, so it is the single place that may touch it.
    /// </remarks>
    public Task RequestPauseAsync(SidebarItem sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        return OnUiThreadAsync(() => _model.RequestPause(sender));
    }

    /// <summary>Asks to lift a pause. Marshalled.</summary>
    public Task RequestResumeAsync(SidebarItem sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        return OnUiThreadAsync(() => _model.RequestResume(sender));
    }

    /// <summary>Asks to release a quarantined message. Marshalled.</summary>
    public Task RequestReleaseAsync(MessageRow message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return OnUiThreadAsync(() => _model.RequestRelease(message));
    }

    /// <summary>
    /// Carries out the confirmed action, and reports what happened either way.
    /// </summary>
    /// <remarks>
    /// <b>Every failure is reported as a failure, with the Host's own words.</b>
    /// Not catching here and letting the exception escape would leave the
    /// operator with a dialog that vanished and no statement about what
    /// happened, which for a release means not knowing whether the mail went
    /// out. The <c>failed</c> flag is passed explicitly rather than sniffed
    /// from the message: inferring it from the Host's prose breaks the day a
    /// sentence is reworded.
    /// </remarks>
    public async Task ConfirmActionAsync(CancellationToken cancellationToken = default)
    {
        if (_services is null) return;

        var request = _model.ConfirmedAction;

        // Null when nothing is pending or the reason is blank, so an
        // unexplained action cannot be carried out by reaching this method
        // directly.
        if (request is null) return;

        string result;
        var failed = false;

        try
        {
            result = await ExecuteAsync(_services, request, cancellationToken).ConfigureAwait(false);
        }
        catch (StyloMailApiException failure)
        {
            failed = true;
            result = Describe(failure);
        }

        await OnUiThreadAsync(() => _model.CompleteAction(result, failed)).ConfigureAwait(false);

        if (failed) return;

        // A sender's controls move with the action, so the buttons match what
        // the Host now holds rather than what it held a moment ago.
        await RefreshSendersAsync().ConfigureAwait(false);

        // And a release moves a row out of the listing that is on screen. Before
        // this, the released row stayed listed: a pane showing the operator
        // something the Host no longer holds, with the Release button still
        // offered for a message that is now queued for delivery. The re-read is
        // also what makes the row's disappearance assertable, rather than a
        // difference between a listing that is current and one that was never
        // refreshed and happens to look the same.
        //
        // Only a release needs it. A pause moves a sender's controls and not a
        // queue: the listings are inbound mail, and a sender control does not
        // change which messages are waiting for attention.
        if (request.Kind == Models.ActionKind.ReleaseQuarantine)
        {
            await LoadSelectionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshSendersAsync()
    {
        if (_services is null) return;

        try
        {
            var listing = await _services.Client.GetSendersAsync().ConfigureAwait(false);
            await OnUiThreadAsync(() => _model.ApplySenders(listing)).ConfigureAwait(false);
        }
        catch (StyloMailApiException)
        {
            // The buttons keep whatever state they had. The result bar already
            // says what the action did.
        }
    }

    private static Task<string> ExecuteAsync(
        AppServices services,
        Models.ActionRequest request,
        CancellationToken cancellationToken) => request.Kind switch
    {
        Models.ActionKind.PauseSender => PauseAsync(services, request, cancellationToken),
        Models.ActionKind.ResumeSender => ResumeAsync(services, request, cancellationToken),
        Models.ActionKind.ReleaseQuarantine => ReleaseAsync(services, request, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unmapped action."),
    };

    private static async Task<string> PauseAsync(
        AppServices services,
        Models.ActionRequest request,
        CancellationToken cancellationToken)
    {
        var response = await services.Client
            .PauseSenderAsync(request.PrincipalId!, request.Reason, cancellationToken)
            .ConfigureAwait(false);

        return $"Paused {response.PrincipalId}. Recorded against {response.UpdatedBy}.";
    }

    private static async Task<string> ResumeAsync(
        AppServices services,
        Models.ActionRequest request,
        CancellationToken cancellationToken)
    {
        var response = await services.Client
            .ResumeSenderAsync(request.PrincipalId!, request.Reason, cancellationToken)
            .ConfigureAwait(false);

        return $"Resumed {response.PrincipalId}. Recorded against {response.UpdatedBy}.";
    }

    private static async Task<string> ReleaseAsync(
        AppServices services,
        Models.ActionRequest request,
        CancellationToken cancellationToken)
    {
        var response = await services.Client
            .ReleaseQuarantineAsync(request.QueueId!, cancellationToken)
            .ConfigureAwait(false);

        // Released false means it was already released, which is a success: the
        // caller asked for it not to be quarantined, and it is not. Saying
        // "already released" rather than "released" is the difference between
        // reporting what happened and reporting what was wanted.
        return response.Released
            ? $"{response.QueueId} released. Recorded against {response.ReleasedBy}."
            : $"{response.QueueId} was already released. Recorded against {response.ReleasedBy}.";
    }

    /// <summary>
    /// Records the drafted label against the open decision.
    /// </summary>
    /// <remarks>
    /// A label feeds the Host's trusted baseline, so a failure must not leave
    /// the draft cleared: an operator who was told nothing was recorded, and
    /// whose text has vanished, has to retype it and cannot tell whether the
    /// first attempt landed.
    /// </remarks>
    public async Task SubmitFeedbackAsync(CancellationToken cancellationToken = default)
    {
        if (_services is null) return;

        if (_model.Decision is not { } decision) return;

        if (!_model.Feedback.CanSubmitFor(decision.AssessmentId)) return;

        var request = _model.Feedback.ToRequest(decision.AssessmentId);

        string result;
        var failed = false;

        try
        {
            var response = await _services.Client
                .RecordFeedbackAsync(request, cancellationToken)
                .ConfigureAwait(false);

            result = $"Recorded {response.Label} against {response.DecisionId}"
                + (response.Recipient is null ? "." : $" for {response.Recipient}.");

            // Scoped labels are what the baseline learns from, so say which
            // kind was recorded rather than letting the operator assume it was
            // a verdict about the message.
            if (_model.Feedback.IsRecipientPreference)
            {
                result += " Recorded as a recipient preference, not a verdict about the message.";
            }
        }
        catch (StyloMailApiException failure)
        {
            failed = true;
            result = Describe(failure);
        }

        var recorded = !failed;

        await OnUiThreadAsync(() =>
        {
            // Cleared only on success, so a failed attempt keeps what was
            // typed.
            if (recorded) _model.Feedback.Reset();

            _model.CompleteAction(result, failed);
            _model.RefreshFeedbackState();
        }).ConfigureAwait(false);
    }

    /// <summary>Turns a failure into something an operator can act on.</summary>
    private static string Describe(StyloMailApiException failure) => failure.Failure switch
    {
        StyloMailApiFailure.ApiKeyNotConfigured => "No API key is configured, so nothing was sent.",
        StyloMailApiFailure.Unreachable => "The Host could not be reached, so nothing was applied.",
        StyloMailApiFailure.UnreadableResponse =>
            "The Host answered with something this build cannot read. The action may or may not have been applied.",
        _ => $"Not applied. {failure.Detail ?? failure.Code ?? "The Host refused the request."}",
    };

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

        // Connection opens a dialog rather than filling a pane: it has nothing
        // to list, and the one thing it does is a form.
        if (item.Title == ConnectionSectionTitle)
        {
            await OpenConnectionDialogAsync().ConfigureAwait(true);
            return;
        }

        if (item.Title == CompaniesSectionTitle)
        {
            await OpenCompaniesDialogAsync().ConfigureAwait(true);
            return;
        }

        await SelectAsync(item).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the connection screen and, when something changed, reconnects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client is rebuilt rather than patched, because the address is its
    /// base address and the key is read through the provider it holds. A
    /// patched client would be one whose connection pool was opened against a
    /// different Host.
    /// </para>
    /// <para>
    /// The gate is <see cref="ConnectionEdit.RequiresReconnect"/> and not "the
    /// key changed", which is what it used to be. Gating on the key alone meant
    /// an operator who changed only the address was told the address had been
    /// saved and kept talking to the old Host, under a button labelled "Save and
    /// connect". See <see cref="ConnectionEdit"/>.
    /// </para>
    /// </remarks>
    public async Task OpenConnectionDialogAsync()
    {
        if (_services is null) return;

        var current = _services;

        var dialog = new ApiKeyDialog(current.Settings, current.ApiKey);

        await dialog.ShowDialog(this).ConfigureAwait(true);

        if (!dialog.Edit.RequiresReconnect) return;

        // The connection itself changed, so this supersedes any sequence still
        // running: those attempts are answers about the Host being left behind.
        await ReconnectAsync(connectionChanged: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Re-asserts the connection to this Host, reading the stored key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the way back from a feed this console has stopped
    /// following.</b> After SignalR's retry budget is spent the console gives
    /// up for good, and reconnecting is what rebuilds the feed with a fresh
    /// budget. Until this existed the only path that called
    /// <see cref="ReconnectAsync"/> was the connection dialog's own save, which
    /// meant an operator had to re-enter a credential the console is built never
    /// to display in order to re-assert a connection they had not changed.
    /// </para>
    /// <para>
    /// Nothing here holds, reads or displays a key. <see cref="ReconnectAsync"/>
    /// rebuilds the client from the settings and the keychain provider, so the
    /// stored key is read again rather than carried through the UI. That is the
    /// distinction the ruling turned on: this re-asserts a connection, it does
    /// not re-prove identity.
    /// </para>
    /// </remarks>
    private async void OnReconnectClick(object? sender, RoutedEventArgs e)
    {
        await ReconnectAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the company screen, and reloads the senders if anything changed.
    /// </summary>
    /// <remarks>
    /// Reloading matters: renaming a company changes a heading in the sidebar,
    /// and filing a sender changes which heading it sits under. Without the
    /// reload the operator would have to restart the console to see what they
    /// just did.
    /// </remarks>
    public async Task OpenCompaniesDialogAsync(CancellationToken cancellationToken = default)
    {
        if (_services is null) return;

        var store = new ApiCompanyStore(_services.Client);

        IReadOnlyList<CompanyResponse> companies;

        try
        {
            companies = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (StyloMailApiException failure)
        {
            Console.Error.WriteLine($"[Companies] {failure.Failure}: {failure.Message}");

            await OnUiThreadAsync(() => _model.CompleteAction(
                $"Could not read the companies. {failure.Detail ?? failure.Code}",
                failed: true)).ConfigureAwait(false);
            return;
        }

        var changed = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new CompaniesDialog(store, companies);
            await dialog.ShowDialog(this);
            return dialog.Changed;
        }).ConfigureAwait(false);

        if (changed) await RefreshSendersAsync().ConfigureAwait(true);
    }

    /// <summary>Rebuilds against whatever the settings and keychain now say.</summary>
    /// <remarks>
    /// <para>
    /// Two callers, and they are different acts. <c>OpenConnectionDialogAsync</c>
    /// calls it because the operator changed something. <c>OnReconnectClick</c>
    /// calls it because the operator wants the connection re-asserted, which is
    /// the way back from an outage longer than the feed's retry budget; that one
    /// changes nothing and reads the stored key.
    /// </para>
    /// <para>
    /// <b>Both are operator actions, so the feed they open is retried.</b> A
    /// press is the operator saying the Host should be there, and a single
    /// attempt that lands on a Host still starting up would leave them with
    /// nothing to do but press again, which is the state P1 exists to remove.
    /// Nothing else in this window opens a retried feed: a window that came up
    /// on its own gets one attempt, so no sequence ever starts without a person.
    /// </para>
    /// </remarks>
    /// <param name="connectionChanged">
    /// Whether the operator changed the address or the key, as opposed to asking
    /// for the same connection again. A changed connection supersedes a sequence
    /// already running, because those attempts are answers about a Host this
    /// console has just left.
    /// </param>
    public async Task ReconnectAsync(bool connectionChanged = false)
    {
        if (_services is null) return;

        // A press while one of these is already running does nothing. The
        // sequence is bounded and it is already showing which attempt it is on,
        // so a second press has nothing to add and starting it again would undo
        // the bound.
        if (_connectInFlight is not null && !connectionChanged) return;

        var ticket = new object();
        _connectInFlight = ticket;

        try
        {
            var previous = _services;

            try
            {
                _services = AppServices.Create(
                    ConsoleEnvironment.HostAddress(previous.Settings),
                    previous.Keychain,
                    settings: previous.Settings);

                previous.Dispose();

                await OnUiThreadAsync(() => _model.SetHostAddress(_services.HostAddress.ToString()))
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                // The address is refused by policy. Report it and keep the old
                // connection rather than leaving the console with no client at all.
                Console.Error.WriteLine($"[Connection] {ex.Message}");

                await OnUiThreadAsync(() => _model.CompleteAction(ex.Message, failed: true))
                    .ConfigureAwait(false);
                return;
            }

            await RefreshHostAsync().ConfigureAwait(true);
            await LoadSendersAsync().ConfigureAwait(true);
            await LoadSelectionAsync().ConfigureAwait(true);

            // The old feed was to the old Host. Leaving it subscribed would have
            // this window reporting on a deployment it no longer talks to, and a
            // dropped feed never recovers by being pointed somewhere else.
            await StartTrafficFeedAsync(retry: true).ConfigureAwait(true);
        }
        finally
        {
            // Only this sequence's own ticket: a superseded one that cleared the
            // field outright would unguard the sequence that replaced it.
            if (ReferenceEquals(_connectInFlight, ticket))
            {
                _connectInFlight = null;
            }
        }
    }

    /// <summary>
    /// Opens this console's subscription to the Host's live feed, replacing any previous one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A feed that cannot be opened is not a failure of the console.</b>
    /// Every state below <see cref="TrafficFeedState.Live"/> leaves a working
    /// console that reads its rows when they are opened, which is what this
    /// console did before the Hub existed. So this reports rather than throws,
    /// and the status bar renders the reason.
    /// </para>
    /// <para>
    /// Public so the harness and the screenshot path can open a feed without
    /// pretending to be an operator opening a window.
    /// </para>
    /// <para>
    /// <b>What the previous subscription knew is carried into the replacement.</b>
    /// The fact "what is on screen may be behind" belongs to the screen, and the
    /// screen outlives this object: without the handover, the reconnect an
    /// operator reaches for during an outage would drop the very warning the
    /// outage raised, and the console would claim a currency it does not have.
    /// </para>
    /// </remarks>
    /// <param name="retry">
    /// Whether to keep trying on the bounded cadence after this attempt. Set
    /// only by an operator action; see <see cref="ReconnectAsync"/>.
    /// </param>
    public async Task StartTrafficFeedAsync(
        CancellationToken cancellationToken = default,
        bool retry = false)
    {
        if (_services is null) return;

        var previous = _feed;
        _feed = null;

        var surfaceMayBeStale = false;

        if (previous is not null)
        {
            surfaceMayBeStale = previous.SurfaceMayBeStale;

            previous.NoticeReceived -= OnTrafficNotice;
            previous.StateChanged -= OnFeedStateChanged;
            previous.Resynchronise -= OnFeedResynchronise;

            // Disposing cancels any sequence the previous subscription was
            // still running, which is what makes a superseding reconnect
            // abandon its attempts at the Host it is leaving.
            await previous.DisposeAsync().ConfigureAwait(true);
        }

        var feed = new TrafficFeed(
            _services.HostAddress,
            _services.ApiKey,
            surfaceMayBeStale: surfaceMayBeStale);

        feed.NoticeReceived += OnTrafficNotice;
        feed.StateChanged += OnFeedStateChanged;
        feed.Resynchronise += OnFeedResynchronise;

        _feed = feed;

        var state = retry
            ? await feed.StartWithRetryAsync(cancellationToken).ConfigureAwait(true)
            : await feed.StartAsync(cancellationToken).ConfigureAwait(true);

        await PublishFeedStatusAsync(feed).ConfigureAwait(true);

        // A sequence can outlive the reads above: it is bounded at about
        // forty-two seconds, and the Host may have come back inside that. What
        // is on screen was read before it did, so a live feed here has not yet
        // been reflected on the screen, and the stale marker comes off only on
        // a read that lands. Guarded on this still being the window's feed,
        // since a superseding reconnect owns the screen from that point on.
        if (retry && state is TrafficFeedState.Live && ReferenceEquals(_feed, feed))
        {
            await ResynchroniseAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Puts the feed's state into the model, on the UI thread.</summary>
    private Task PublishFeedStatusAsync(TrafficFeed feed)
        => OnUiThreadAsync(() =>
            _model.LiveFeed = LiveFeedStatus.From(feed.State, feed.SurfaceMayBeStale, feed.Retrying));

    /// <summary>
    /// A notice arrived. Raised on a transport thread.
    /// </summary>
    /// <remarks>
    /// Nothing is rendered from this: the notice is a hint, and the read below
    /// is what makes the screen true. The handler starts the work and returns,
    /// because a transport thread must not be held up by a Host round trip.
    /// </remarks>
    private void OnTrafficNotice(TrafficNotice notice)
        => _ = ApplyTrafficNoticeAsync(notice);

    /// <summary>
    /// Reads back whatever a notice points at.
    /// </summary>
    /// <remarks>
    /// <b>Every branch here is a read of the Host.</b> Nothing the notice
    /// carried is displayed, which is the console's documented rule for pushed
    /// events: a dropped, duplicated or reordered event rendered directly would
    /// be a permanently wrong screen, and a console showing a stale verdict as
    /// current is worse than one showing nothing.
    /// <para>
    /// Which read a notice calls for is decided by
    /// <see cref="TrafficNoticeRouting"/>, not here. That mapping has rules in
    /// it and belongs somewhere a test can hand it a notice, which this class is
    /// not. This class is what performs the read.
    /// </para>
    /// </remarks>
    private async Task ApplyTrafficNoticeAsync(TrafficNotice notice)
    {
        try
        {
            await (TrafficNoticeRouting.For(notice) switch
            {
                TrafficNoticeRoute.Readiness => RefreshHostAsync(),
                TrafficNoticeRoute.Senders => LoadSendersAsync(),
                TrafficNoticeRoute.Selection => LoadSelectionAsync(),
                TrafficNoticeRoute.Everything => ResynchroniseAsync(),

                // Unreachable for a route the mapping produced, and the same
                // answer if it ever is not: a route this build has no read for
                // gets a full read rather than no read at all. An enum switch
                // needs this arm (CS8524), so a route added later lands here
                // rather than failing the build: the cost of that is one round
                // trip, which is the cheap way to be wrong.
                _ => ResynchroniseAsync(),
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The window is still up and the operator's next action still reads
            // the Host. Taking the app down over a hint would be the tail
            // wagging the dog.
            Console.Error.WriteLine($"[Traffic] {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads everything the window is showing, because the feed cannot say what changed.
    /// </summary>
    /// <remarks>
    /// This is the answer to both a reconnect and a notice this build cannot
    /// interpret. The Host tells the console nothing about what it missed, so
    /// the only way for the screen to be true again is to read it again.
    /// </remarks>
    private async Task ResynchroniseAsync()
    {
        // Ordered so the operator sees the reason before the consequences.
        var host = await RefreshHostAsync().ConfigureAwait(true);
        var senders = await LoadSendersAsync().ConfigureAwait(true);
        var selection = await LoadSelectionAsync().ConfigureAwait(true);

        if (_feed is not { } feed)
        {
            return;
        }

        // Only a read that landed makes the screen current again. A gap that
        // ends in failed reads leaves the same picture on screen, and taking
        // the warning off then would be the console claiming a read it did not
        // get, which is the one thing this marker must never do.
        if (host && senders && selection)
        {
            feed.SurfaceIsCurrent();
        }

        await PublishFeedStatusAsync(feed).ConfigureAwait(true);
    }

    private void OnFeedStateChanged()
        => _ = PublishFeedStatusAsyncForCurrentFeedAsync();

    private void OnFeedResynchronise()
        => _ = ResynchroniseAsync();

    private async Task PublishFeedStatusAsyncForCurrentFeedAsync()
    {
        if (_feed is { } feed)
        {
            await PublishFeedStatusAsync(feed).ConfigureAwait(true);
        }
    }

    private async void OnPauseSenderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SidebarItem item }) await RequestPauseAsync(item).ConfigureAwait(true);
    }

    private async void OnResumeSenderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SidebarItem item }) await RequestResumeAsync(item).ConfigureAwait(true);
    }

    private async void OnEditSenderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SidebarItem item })
        {
            await OpenSenderProfileAsync(item).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Opens a sender's profile, and saves it if the operator confirms.
    /// </summary>
    /// <remarks>
    /// The draft is built from what the Host holds and the request is built from
    /// the draft, so the fields this dialog collects round-trip alongside the
    /// ones it does not. A settings write is a full replace, and assembling the
    /// request from the visible fields is how editing a note would silently
    /// erase a company.
    /// </remarks>
    public async Task OpenSenderProfileAsync(SidebarItem sender, CancellationToken cancellationToken = default)
    {
        if (_services is null) return;
        if (sender.PrincipalId is not { Length: > 0 } principalId) return;

        SenderSettingsResponse settings;
        IReadOnlyList<CompanyResponse> companies = [];

        try
        {
            settings = await _services.Client.GetSenderSettingsAsync(principalId, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                companies = (await _services.Client.GetCompaniesAsync(cancellationToken).ConfigureAwait(false))
                    .Companies;
            }
            catch (StyloMailApiException failure)
            {
                // The picker offers "no company" only. Reported rather than
                // swallowed: an operator who cannot see their companies needs to
                // know why before they file a sender nowhere.
                Console.Error.WriteLine($"[Companies] {failure.Failure}: {failure.Message}");
            }
        }
        catch (StyloMailApiException failure)
        {
            Console.Error.WriteLine($"[Profile] {failure.Failure}: {failure.Message}");

            await OnUiThreadAsync(() => _model.CompleteAction(
                $"Could not read this sender's profile. {failure.Detail ?? failure.Code}",
                failed: true)).ConfigureAwait(false);
            return;
        }

        var draft = SenderProfileDraft.From(settings);

        // Constructed and shown on the UI thread, explicitly.
        //
        // This is the fourth time the same rule has had to be applied in this
        // window, and the first time it applied to a Window rather than a
        // model. Constructing any Control verifies dispatcher access, so a
        // continuation that landed on the thread pool -- which is what
        // ConfigureAwait(false) on the awaits above arranges -- throws "Call
        // from invalid thread" before the dialog exists.
        var confirmed = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new SenderProfileDialog(draft, companies);
            await dialog.ShowDialog(this);
            return dialog.Saved;
        }).ConfigureAwait(false);

        // Cancelled: nothing read, nothing written.
        if (confirmed is null) return;

        string result;
        var failed = false;

        try
        {
            var saved = await _services.Client
                .SaveSenderSettingsAsync(principalId, draft.ToRequest(), cancellationToken)
                .ConfigureAwait(false);

            result = saved.IsDescribed
                ? $"Saved {saved.PrincipalId}. Recorded against {saved.UpdatedBy}."
                : $"Cleared the profile for {saved.PrincipalId}.";
        }
        catch (StyloMailApiException failure)
        {
            failed = true;
            result = Describe(failure);
        }

        await OnUiThreadAsync(() => _model.CompleteAction(result, failed)).ConfigureAwait(false);

        if (!failed) await RefreshSendersAsync().ConfigureAwait(true);
    }

    private async void OnReleaseClick(object? sender, RoutedEventArgs e)
    {
        if (_model.SelectedMessage is { } message) await RequestReleaseAsync(message).ConfigureAwait(true);
    }

    private void OnCancelActionClick(object? sender, RoutedEventArgs e) => _model.CancelAction();

    private void OnDismissResultClick(object? sender, RoutedEventArgs e) => _model.DismissActionResult();

    private async void OnConfirmActionClick(object? sender, RoutedEventArgs e)
        => await ConfirmActionAsync().ConfigureAwait(true);

    private async void OnSubmitFeedbackClick(object? sender, RoutedEventArgs e)
        => await SubmitFeedbackAsync().ConfigureAwait(true);

    /// <summary>
    /// Selecting a message loads the decision behind it.
    /// </summary>
    /// <remarks>
    /// Driven by the event rather than by the model's setter, because the model
    /// performs no I/O: it is the window's job to notice a selection and go and
    /// ask, which is what keeps the model testable without a Host.
    /// </remarks>
    private async void OnMessageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // The row's own button is mid-assignment and loads what it selected.
        if (_openingMessageFromRowButton) return;

        if (_model.SelectedMessage is null)
        {
            await OnUiThreadAsync(_model.NoDecisionsForMessage).ConfigureAwait(true);
            return;
        }

        await LoadDecisionForSelectedMessageAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the ledger row's decision in the detail pane.
    /// </summary>
    /// <remarks>
    /// A ledger row is a summary, so selecting one is a second round trip to
    /// <c>GET /v1/decisions/{id}</c> for the evidence the listing deliberately
    /// omits. A failure here leaves the pane's stated reason in place rather
    /// than clearing it, so the operator is not left looking at the previous
    /// row's explanation under a new selection.
    /// </remarks>
    private async void OnDecisionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_model.SelectedDecision is not { } row)
        {
            await OnUiThreadAsync(_model.ClearDecision).ConfigureAwait(true);
            return;
        }

        await OpenLedgerRowAsync(row).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens a ledger row's full decision, from a click or from the row's button.
    /// </summary>
    /// <remarks>
    /// Both routes end here rather than each fetching, because they are the
    /// same request made two ways: selecting the row and pressing its control
    /// both mean "show me this one". A row whose decision is already open is
    /// not fetched again.
    /// </remarks>
    private async Task OpenLedgerRowAsync(DecisionRow row)
    {
        if (_model.Decision?.AssessmentId == row.AssessmentId) return;

        if (!await OpenDecisionAsync(row.AssessmentId).ConfigureAwait(true))
        {
            Console.Error.WriteLine($"[Ledger] Could not open decision {row.AssessmentId}.");
        }
    }

    /// <summary>
    /// The row's own open control.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists at all.</b> The harness clicks a control by raising
    /// its Click event, and a ListBoxItem has no handler for one, so a click on
    /// a ledger row selects nothing and the harness reports it as success
    /// anyway. A control with a real Click handler is the only thing a script
    /// can drive, and it is also the honest affordance for a row that costs a
    /// round trip to open.
    /// </remarks>
    private async void OnOpenDecisionClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DecisionRow row) return;

        await OpenLedgerRowAsync(row).ConfigureAwait(true);
    }

    /// <summary>Loads one page of original assessed records for the open Slack conversation.</summary>
    private async void OnLoadConversationHistoryClick(object? sender, RoutedEventArgs e)
    {
        var panel = _model.Decision?.ConversationHistory;
        if (_services is null || panel?.Scope is not { } scope)
        {
            return;
        }

        var append = panel.HasLoaded && panel.HasMore;
        var cursor = append ? panel.NextCursor : null;
        await OnUiThreadAsync(() => panel.BeginLoad(append)).ConfigureAwait(false);

        try
        {
            var response = await _services.Client.GetConversationHistoryAsync(
                new ConversationHistoryQuery
                {
                    WorkspaceId = scope.WorkspaceId,
                    ChannelId = scope.ChannelId,
                    ThreadId = scope.ThreadId,
                    Limit = ConversationHistoryPageSize(),
                    After = cursor,
                }).ConfigureAwait(false);

            await OnUiThreadAsync(() =>
            {
                if (ReferenceEquals(_model.Decision?.ConversationHistory, panel))
                {
                    panel.ApplyPage(response, append);
                }
            }).ConfigureAwait(false);
        }
        catch (StyloMailApiException failure)
        {
            Console.Error.WriteLine($"[Conversation history] {failure.Failure}: {failure.Message}");
            await OnUiThreadAsync(() =>
            {
                if (ReferenceEquals(_model.Decision?.ConversationHistory, panel))
                {
                    panel.Fail(preservePageState: append);
                }
            }).ConfigureAwait(false);
        }
    }

    private static int ConversationHistoryPageSize()
    {
#if DEBUG
        var configured = Environment.GetEnvironmentVariable("STYLOMAIL_SMOKE_HISTORY_PAGE_SIZE");
        if (int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize)
            && pageSize is >= 1 and <= 100)
        {
            return pageSize;
        }
#endif
        return 25;
    }

    /// <summary>
    /// The message row's own open control.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists at all.</b> The same reason the ledger's does: the
    /// harness clicks a control by raising its Click event, and a ListBoxItem
    /// has no handler for one, so a click on the row does nothing while the
    /// run reports success. That made the message-to-decision join, which is
    /// implemented and wired, unreachable from a run for want of a control to
    /// press.
    ///
    /// <para>
    /// The work is the selection's work rather than a second implementation of
    /// it: selecting the row is exactly what the pane already does, and the
    /// same loader runs. A row that is already selected has no selection change
    /// to fire, so the flag below only stops the event from fetching a second
    /// time.
    /// </para>
    /// </remarks>
    private async void OnOpenMessageClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MessageRow row) return;

        _openingMessageFromRowButton = true;
        try
        {
            _model.SelectedMessage = row;
        }
        finally
        {
            _openingMessageFromRowButton = false;
        }

        await LoadDecisionForSelectedMessageAsync().ConfigureAwait(true);
    }
}
