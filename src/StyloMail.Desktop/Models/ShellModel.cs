using System.Collections.ObjectModel;
using System.ComponentModel;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Models;

/// <summary>
/// What the console window shows.
/// </summary>
/// <remarks>
/// The three panes of the Apple Mail shape the console is modelled on, and
/// nothing else: a sidebar of destinations, a list of items, and one item's
/// detail. The model holds no client and performs no calls, so the window can
/// be built and photographed with no Host, no keychain and no network.
/// </remarks>
public sealed class ShellModel : ObservableObject
{
    private HostStatus _status = HostStatus.Unknown;
    private SidebarItem? _selectedItem;
    private MessageRow? _selectedMessage;
    private bool _isCheckingHost;
    private string? _nextCursor;
    private bool _hasMore;
    private DecisionView? _decision;
    private ActionRequest? _pendingAction;
    private string _pendingReason = string.Empty;
    private string? _lastActionResult;
    private bool _lastActionFailed;

    /// <summary>The section filled from <c>GET /v1/senders</c> after the window opens.</summary>
    public SidebarSection SendersSection { get; } = new("Senders", []);

    /// <summary>
    /// The cursor for the next page, as the Host gave it.
    /// </summary>
    /// <remarks>
    /// Held and echoed back, never reconstructed. It is opaque and carries no
    /// tenant, so deriving it from a row's fields would be inventing a paging
    /// scheme on the wrong side of the wire.
    /// </remarks>
    public string? NextCursor
    {
        get => _nextCursor;
        private set => Set(ref _nextCursor, value);
    }

    public bool HasMore
    {
        get => _hasMore;
        private set => Set(ref _hasMore, value);
    }

    /// <summary>Where this console points, shown at the right of the status bar.</summary>
    public string HostAddress { get; private init; } = string.Empty;

    public ShellModel()
    {
        // ObservableCollection raises for its own members, not for the
        // properties computed from its count. Without this the list pane keeps
        // showing its empty state after the first page arrives, because nothing
        // ever told the binding that HasMessages had changed.
        Messages.CollectionChanged += (_, _) => Raise(nameof(HasMessages));
    }

    public ObservableCollection<SidebarSection> Sections { get; } = [];

    public ObservableCollection<MessageRow> Messages { get; } = [];

    /// <summary>What the console knows about its Host. Drives the status bar and the Host entry.</summary>
    public HostStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;

            Raise(nameof(StatusHeadline));
            Raise(nameof(StatusDetail));
            Raise(nameof(HasFailedChecks));
            SelectedItem?.WithDetail(value.Headline);
        }
    }

    public string StatusHeadline => Status.Headline;

    public string StatusDetail => Status.Detail;

    public bool HasFailedChecks => Status.FailedChecks.Count > 0;

    public IReadOnlyList<string> FailedChecks => Status.FailedChecks;

    public bool IsCheckingHost
    {
        get => _isCheckingHost;
        set => Set(ref _isCheckingHost, value);
    }

    public SidebarItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            var previous = _selectedItem;
            if (!Set(ref _selectedItem, value)) return;

            if (previous is not null)
            {
                previous.IsSelected = false;
                previous.PropertyChanged -= OnSelectedItemChanged;
            }

            if (value is not null)
            {
                value.IsSelected = true;

                // Subscribed rather than recomputed, because the pane header
                // reads the selected item's detail and that detail changes
                // later: the Host entry's subtitle is the connection status,
                // which arrives after the item is selected. Raising the header
                // properties only when the selection changes leaves the header
                // showing whatever was true at the moment of selection, which
                // is how a captured frame came to read "Not checked yet"
                // directly above a status bar reading "Connected".
                value.PropertyChanged += OnSelectedItemChanged;
            }

            Raise(nameof(SelectedPaneTitle));
            Raise(nameof(SelectedPaneDetail));
            Raise(nameof(EmptyListDetail));
        }
    }

    private void OnSelectedItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(SelectedPaneTitle));
        Raise(nameof(SelectedPaneDetail));
        Raise(nameof(EmptyListDetail));
    }

    public MessageRow? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (!Set(ref _selectedMessage, value)) return;

            Raise(nameof(HasSelectedMessage));
            Raise(nameof(CanReleaseSelected));
        }
    }

    public bool HasSelectedMessage => SelectedMessage is not null;

    /// <summary>
    /// Whether the selected message can be released from quarantine.
    /// </summary>
    /// <remarks>
    /// Only for a message that is actually quarantined. Offering release on a
    /// delivered message would be offering an action the Host would either
    /// refuse or, worse, treat as a no-op while reporting success.
    /// </remarks>
    public bool CanReleaseSelected =>
        SelectedMessage is { State: DeliveryState.Quarantined };

    /// <summary>
    /// The decision being shown, when one has been opened.
    /// </summary>
    /// <remarks>
    /// Null on a message that was listed rather than opened by its assessment
    /// id, and that is a real gap rather than a loading state: no route connects
    /// a listed message to its decision today, so the pane cannot fill itself
    /// from the list. See <see cref="DecisionUnavailableReason"/>.
    /// </remarks>
    public DecisionView? Decision
    {
        get => _decision;
        private set
        {
            if (!Set(ref _decision, value)) return;

            Raise(nameof(HasDecision));
            Raise(nameof(DecisionUnavailableReason));
        }
    }

    public bool HasDecision => Decision is not null;

    /// <summary>
    /// Why the decision pane is empty, when it is.
    /// </summary>
    /// <remarks>
    /// Three different answers with three different remedies, and the pane says
    /// which one it is. "Nothing here" would be true of all three and useful for
    /// none.
    /// </remarks>
    public string DecisionUnavailableReason => SelectedMessage is null
        ? "Select a message to see the decision behind it."
        : "No route connects a listed message to its decision. The listing rows carry no "
            + "assessment id and neither does the submission detail, so this pane cannot fill "
            + "itself from the list. Opening a decision by its own id works.";

    public bool HasMessages => Messages.Count > 0;

    /// <summary>The middle pane's title: whatever the sidebar has selected.</summary>
    public string SelectedPaneTitle => SelectedItem?.Title ?? "Messages";

    public string SelectedPaneDetail => SelectedItem?.Detail ?? string.Empty;

    /// <summary>
    /// The middle pane's explanation when it is empty.
    /// </summary>
    /// <remarks>
    /// Names the missing route rather than saying "no results". The two mean
    /// opposite things to an operator: one says the queue is empty, the other
    /// says the console cannot look.
    /// </remarks>
    public string EmptyListDetail => SelectedItem switch
    {
        null => "Nothing here.",

        // Names the route rather than saying "a route". The operator reading
        // this is the person who has to ask for it, and the sidebar entry
        // already spells it out, so the pane repeats it instead of making them
        // look back up.
        { State: SidebarItemState.AwaitingRoute } item =>
            $"This pane needs a Host route that does not exist yet ({item.Detail}). "
            + "The console does not read the database to work around it.",

        { State: SidebarItemState.NotBuilt } =>
            "This pane has not been built yet.",

        { EmptyDetail: { Length: > 0 } detail } => detail,

        _ => "Nothing here.",
    };

    /// <summary>
    /// Builds the sidebar.
    /// </summary>
    /// <remarks>
    /// Every entry names the Host route behind it, including the ones that are
    /// missing. Constraint 1 of the console's design is that nothing here may be
    /// the only way to do something, and listing the routes is how that stays
    /// checkable at a glance rather than only in review.
    /// </remarks>
    public static ShellModel CreateDefault(Uri? hostAddress = null)
    {
        var model = new ShellModel { HostAddress = hostAddress?.ToString() ?? string.Empty };

        var host = new SidebarItem(
            "Host",
            SidebarItemState.Available,
            HostStatus.Unknown.Headline,
            "This console's connection to the Host. The status bar below shows whether it is reachable.");

        model.Sections.Add(new SidebarSection("StyloMail", [host]));

        // Only the dispositions GET /v1/messages enumerates. The shell
        // previously offered "Queued" and "Delivered", which the route answers
        // with a named 400: the queue lists what is waiting for attention, not
        // what has been accepted. A destination that cannot be requested is
        // worse than an absent one, because the refusal reads as a fault.
        model.Sections.Add(new SidebarSection("Queues",
        [
            new SidebarItem(
                "Awaiting decision",
                SidebarItemState.Available,
                "GET /v1/messages?state=awaiting_decision",
                "Nothing is awaiting a decision. This lists what has been accepted and not yet "
                    + "decided on; mail already in normal delivery is not enumerable here.",
                MessageListState.AwaitingDecision),

            new SidebarItem(
                "Held",
                SidebarItemState.Available,
                "GET /v1/messages?state=held",
                "Nothing is held. A held message is retained to a bounded re-evaluation deadline, "
                    + "which is an observation window rather than a soft reject.",
                MessageListState.Held),

            new SidebarItem(
                "Quarantined",
                SidebarItemState.Available,
                "GET /v1/messages?state=quarantined",
                "Nothing is quarantined. Releasing one is audited and requires the review privilege.",
                MessageListState.Quarantined),
        ]));

        // Filled from GET /v1/senders once the window opens. A placeholder
        // rather than an empty section, so the entry does not appear and vanish.
        model.SendersSection.Items.Add(
            new SidebarItem("Loading", SidebarItemState.NotBuilt, "GET /v1/senders"));

        model.Sections.Add(model.SendersSection);

        model.Sections.Add(new SidebarSection("Review",
        [
            // Reading one decision works (GET /v1/decisions/{id}), but nothing
            // enumerates the ledger, so this pane has no rows to show and no
            // amount of client work would give it any. Marked blocked rather
            // than left to look empty, because the two read very differently to
            // an operator: one says the console cannot look, the other says
            // there is nothing to find.
            new SidebarItem(
                "Decisions",
                SidebarItemState.AwaitingRoute,
                "needs a route that enumerates the ledger",
                "A decision is opened by its identifier, but no route enumerates the ledger, so "
                    + "this pane cannot list them. The console does not read the database to work "
                    + "around it."),
        ]));

        model.SelectedItem = host;

        return model;
    }

    /// <summary>
    /// Replaces the senders section with the principals the Host reports.
    /// </summary>
    /// <remarks>
    /// The audit trail is carried into the subtitle for each principal, because
    /// the two cases that arrive identically as <c>paused: false</c> are not
    /// the same: one was stopped and lifted, the other has never been touched.
    /// Collapsing them would erase the answer to "why was this account stopped
    /// for six hours", which is asked after the pause is gone.
    /// </remarks>
    public void ApplySenders(SenderListingResponse listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        var selected = SelectedItem;
        SendersSection.Items.Clear();
        if (listing.Senders.Count == 0)
        {
            SendersSection.Items.Add(new SidebarItem(
                "No senders",
                SidebarItemState.NotBuilt,
                "This tenant has no configured sending principals."));
            return;
        }

        foreach (var sender in listing.Senders)
        {
            var item = new SidebarItem(
                sender.PrincipalId,
                SidebarItemState.Available,
                DescribeControl(sender.Control))
            {
                IsPaused = sender.Control.Paused,
                IsSender = true,
            };

            SendersSection.Items.Add(item);

            // Keep the selection pointing at the same principal across a
            // refresh, rather than at the instance that has just been replaced.
            if (selected is not null && selected.Title == sender.PrincipalId)
            {
                SelectedItem = item;
            }
        }
    }

    private static string DescribeControl(SenderControlResponse control)
    {
        if (control.Paused)
        {
            return string.IsNullOrWhiteSpace(control.Reason)
                ? "Paused. No reason was recorded."
                : $"Paused: {control.Reason}";
        }

        return control is { ResumedAt: not null }
            ? $"Resumed by {control.ResumedBy ?? "unknown"}"
            : "Active";
    }

    // ===================== write actions =====================

    /// <summary>
    /// The action the operator has asked for and not yet confirmed.
    /// </summary>
    /// <remarks>
    /// Held here rather than executed, so that asking and doing stay separate.
    /// A new request replaces a pending one instead of queueing: a confirmation
    /// that produced two actions would be worse than one that produced none.
    /// </remarks>
    public ActionRequest? PendingAction
    {
        get => _pendingAction;
        private set
        {
            if (!Set(ref _pendingAction, value)) return;

            Raise(nameof(HasPendingAction));
            Raise(nameof(CanConfirmAction));

            // A new request starts with no reason: it is collected in the
            // dialog, against the consequence being shown.
            PendingReason = string.Empty;
        }
    }

    public bool HasPendingAction => PendingAction is not null;

    /// <summary>What the last action did, or why it did not.</summary>
    public string? LastActionResult
    {
        get => _lastActionResult;
        private set
        {
            if (!Set(ref _lastActionResult, value)) return;
            Raise(nameof(HasLastActionResult));
        }
    }

    public bool HasLastActionResult => LastActionResult is not null;

    /// <summary>Clears the result banner, once it has been read.</summary>
    public void DismissActionResult() => LastActionResult = null;

    /// <summary>
    /// Whether the last action failed.
    /// </summary>
    /// <remarks>
    /// <b>Separate from <see cref="LastActionResult"/> on purpose.</b> The
    /// message alone is not enough to render: a console that showed the Host's
    /// failure text in the same neutral style as a success would leave an
    /// operator believing a quarantined message had been released when it is
    /// still sitting in the queue.
    /// </remarks>
    public bool LastActionFailed
    {
        get => _lastActionFailed;
        private set => Set(ref _lastActionFailed, value);
    }

    /// <summary>Asks to stop a principal's outbound delivery.</summary>
    public void RequestPause(SidebarItem sender)
    {
        ArgumentNullException.ThrowIfNull(sender);

        PendingAction = new ActionRequest
        {
            Kind = ActionKind.PauseSender,
            Title = $"Pause {sender.Title}",
            Consequence =
                "Outbound mail from this account will stop being delivered until it is resumed. "
                + "The pause is recorded against your principal.",
            Reason = string.Empty,
            PrincipalId = sender.Title,
        };
    }

    /// <summary>Asks to lift a pause.</summary>
    public void RequestResume(SidebarItem sender)
    {
        ArgumentNullException.ThrowIfNull(sender);

        PendingAction = new ActionRequest
        {
            Kind = ActionKind.ResumeSender,
            Title = $"Resume {sender.Title}",
            Consequence =
                "Outbound mail from this account will be delivered again. "
                + "The resume is recorded against your principal.",
            Reason = string.Empty,
            PrincipalId = sender.Title,
        };
    }

    /// <summary>Asks to release a quarantined message.</summary>
    public void RequestRelease(MessageRow message)
    {
        ArgumentNullException.ThrowIfNull(message);

        PendingAction = new ActionRequest
        {
            Kind = ActionKind.ReleaseQuarantine,
            Title = $"Release {message.QueueId}",
            Consequence =
                "The message will be delivered to its recipients. Releasing is recorded against "
                + "your principal, and delivering it cannot be undone.",
            Reason = string.Empty,
            QueueId = message.QueueId,
        };
    }

    /// <summary>
    /// The reason the operator has typed into the confirmation.
    /// </summary>
    /// <remarks>
    /// On the pending request rather than passed in, because the reason is
    /// collected in the same dialog that shows the consequence. Requiring it
    /// before the dialog opens would mean the operator agreed to something
    /// before being told what it was.
    ///
    /// <para>
    /// The Host accepts an empty reason on the pause and resume routes. The
    /// console does not, for the reason in <see cref="ActionRequest.Reason"/>.
    /// </para>
    /// </remarks>
    public string PendingReason
    {
        get => _pendingReason;
        set
        {
            if (!Set(ref _pendingReason, value)) return;
            Raise(nameof(CanConfirmAction));
        }
    }

    /// <summary>Whether the pending action can be carried out.</summary>
    public bool CanConfirmAction => PendingAction is not null && !string.IsNullOrWhiteSpace(PendingReason);

    public void CancelAction() => PendingAction = null;

    /// <summary>
    /// The request with the typed reason applied, ready to be carried out.
    /// </summary>
    /// <remarks>
    /// Null when nothing is pending or the reason is blank, so a caller cannot
    /// execute an unexplained action by forgetting to check
    /// <see cref="CanConfirmAction"/> first.
    /// </remarks>
    public ActionRequest? ConfirmedAction =>
        CanConfirmAction ? PendingAction! with { Reason = PendingReason.Trim() } : null;

    /// <summary>
    /// Records what an action did, and clears the request.
    /// </summary>
    /// <remarks>
    /// <paramref name="failed"/> is a required argument rather than something
    /// inferred from the message. Inferring it, by looking for words like
    /// "could not" in the Host's prose, is how a console ends up rendering a
    /// failure as a success the day the Host rewords a sentence.
    /// </remarks>
    public void CompleteAction(string result, bool failed)
    {
        PendingAction = null;
        LastActionFailed = failed;
        LastActionResult = result;
    }

    /// <summary>Shows a decision in the detail pane.</summary>
    public void ShowDecision(DecisionResponse decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Decision = DecisionView.From(decision);
    }

    /// <summary>Replaces the list pane's contents with one page of messages.</summary>
    public void ApplyMessages(MessageListingResponse listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        Messages.Clear();

        foreach (var message in listing.Messages)
        {
            Messages.Add(MessageRow.From(message));
        }

        NextCursor = listing.NextCursor;
        HasMore = listing.HasMore;

        SelectedMessage = null;
        Decision = null;
    }
}

/// <summary>
/// One row in the middle pane.
/// </summary>
/// <remarks>
/// <b>Built from what the listing actually sends, and nothing else.</b> A
/// queued message carries a queue id, its state, an attempt count and its
/// recipients. It carries no subject and no sender, and inventing columns for
/// those would mean rendering empty ones: this is not a mailbox, and a row that
/// looks like a mail row while containing none of the fields of one is a
/// promise the console cannot keep.
///
/// <para>
/// The recipients are summarised rather than counted away, because a message
/// whose copies are in different states is the case the per-recipient model
/// exists for.
/// </para>
/// </remarks>
public sealed class MessageRow
{
    public required string QueueId { get; init; }

    public required DeliveryState State { get; init; }

    public required int Attempts { get; init; }

    public required IReadOnlyList<string> Recipients { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ReEvaluateBy { get; init; }

    public static MessageRow From(SubmissionStatusResponse message) => new()
    {
        QueueId = message.QueueId,
        State = message.State,
        Attempts = message.Attempts,
        Recipients = [.. message.Recipients.Select(recipient => recipient.Recipient)],
        CreatedAt = message.CreatedAt,

        // The soonest re-evaluation across recipients, because that is the one
        // that will change first.
        ReEvaluateBy = message.Recipients
            .Where(recipient => recipient.ReEvaluateBy is not null)
            .Select(recipient => recipient.ReEvaluateBy)
            .Min(),
    };

    public string StateLabel => State.ToString();

    public string RecipientsLabel => Recipients.Count switch
    {
        0 => "no recipients",
        1 => Recipients[0],
        _ => $"{Recipients[0]} +{Recipients.Count - 1}",
    };

    public string AttemptsLabel => Attempts == 1 ? "1 attempt" : $"{Attempts} attempts";
}
