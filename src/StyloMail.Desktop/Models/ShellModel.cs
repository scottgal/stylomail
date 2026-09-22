using System.Collections.ObjectModel;
using System.ComponentModel;
using StyloMail.Desktop.Api;
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

    /// <summary>
    /// The sections filled from <c>GET /v1/senders</c>, one per company.
    /// </summary>
    /// <remarks>
    /// A list rather than one section, because senders group by company and the
    /// grouping is the point of the management surface. Rebuilt in place on
    /// every load, and the old ones removed first, so the sidebar cannot
    /// accumulate a company that was renamed or emptied.
    /// </remarks>
    private readonly List<SidebarSection> _senderSections = [];

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
    public string HostAddress { get; private set; } = string.Empty;

    /// <summary>Records a new Host address, after the connection screen changes it.</summary>
    public void SetHostAddress(string address)
    {
        HostAddress = address;
        Raise(nameof(HostAddress));
    }

    public ShellModel()
    {
        // ObservableCollection raises for its own members, not for the
        // properties computed from its count. Without this the list pane keeps
        // showing its empty state after the first page arrives, because nothing
        // ever told the binding that HasMessages had changed.
        Messages.CollectionChanged += (_, _) => Raise(nameof(HasMessages));

        // The Record button binds to CanSubmitFeedback, which is computed from
        // this draft. Without forwarding the draft's own changes the button
        // stays disabled however much is typed into it, and every test of the
        // draft still passes because they assert the draft's property rather
        // than the one the button is bound to.
        Feedback.PropertyChanged += (_, _) => Raise(nameof(CanSubmitFeedback));
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
    /// Four different answers with four different remedies, and the pane says
    /// which one it is. "Nothing here" would be true of all four and useful for
    /// none.
    /// </remarks>
    public string DecisionUnavailableReason => SelectedMessage switch
    {
        null => "Select a message to see the decision behind it.",

        { InternalMessageId.Length: 0 } =>
            "This message carries no internal message id, so the ledger cannot be asked about it. "
            + "That is a Host that stopped sending the join key rather than a message without a "
            + "decision.",

        _ when _decisionLookup is DecisionLookup.Loading => "Looking up the decisions for this message.",

        _ when _decisionLookup is DecisionLookup.None =>
            "No decisions are recorded for this message. Assessment may not be configured on this "
            + "Host, or the message may have been listed before it was assessed.",

        _ => "The decisions for this message could not be read.",
    };

    /// <summary>What the last decision lookup for the selected message produced.</summary>
    private DecisionLookup _decisionLookup = DecisionLookup.Unknown;

    /// <summary>
    /// How many decisions are recorded against the selected message.
    /// </summary>
    /// <remarks>
    /// A message can be assessed more than once, and a re-assessment after a
    /// policy change is a real thing to have on the record. The pane shows the
    /// newest and says how many there are, because showing only the newest
    /// without saying so would hide that anything changed.
    /// </remarks>
    /// <summary>Delegates to the view, which is what the pane is bound to.</summary>
    public int DecisionCount => Decision?.DecisionCount ?? 0;

    public string? DecisionHistoryNote => Decision?.HistoryNote;

    public bool HasDecisionHistory => Decision?.HasHistory ?? false;

    /// <summary>Marks the pane as waiting for a lookup.</summary>
    public void BeginDecisionLookup()
    {
        _decisionLookup = DecisionLookup.Loading;
        Decision = null;
        Raise(nameof(DecisionCount));
        Raise(nameof(DecisionHistoryNote));
        Raise(nameof(HasDecisionHistory));
        Raise(nameof(DecisionUnavailableReason));
    }

    /// <summary>The ledger answered, and had nothing for this message.</summary>
    public void NoDecisionsForMessage()
    {
        _decisionLookup = DecisionLookup.None;
        Decision = null;
        Raise(nameof(DecisionCount));
        Raise(nameof(DecisionUnavailableReason));
    }

    /// <summary>The lookup failed.</summary>
    public void DecisionLookupFailed()
    {
        _decisionLookup = DecisionLookup.Unavailable;
        Decision = null;
        Raise(nameof(DecisionCount));
        Raise(nameof(DecisionUnavailableReason));
    }

    /// <summary>What a decision lookup produced, for the pane's empty state.</summary>
    private enum DecisionLookup
    {
        Unknown,
        Loading,
        None,
        Unavailable,
    }

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

        // Filled from GET /v1/senders once the window opens, one section per
        // company. A placeholder rather than an empty section, so the entry does
        // not appear and vanish.
        model._senderSections.Add(new SidebarSection("Senders",
        [
            new SidebarItem("Loading", SidebarItemState.NotBuilt, "GET /v1/senders"),
        ]));

        foreach (var placeholder in model._senderSections) model.Sections.Add(placeholder);

        // Spec 2 names an Operator / tenant admin whose job is configuring the
        // system, and 10.2's five areas are all review work. This is where that
        // second job lives.
        model.Sections.Add(new SidebarSection("Management",
        [
            new SidebarItem(
                "Connection",
                SidebarItemState.Available,
                "Enter or replace this console's API key",
                "The console holds one credential: the API key for the Host it is pointed at. "
                    + "It is stored in your keychain and cannot be shown again after it is saved."),

            new SidebarItem(
                "Companies",
                SidebarItemState.Available,
                "Group senders into companies",
                "A company organises senders in this console. Nothing in the assessment pipeline "
                    + "reads one, so this groups your view rather than changing how mail is judged."),
        ]));

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
    public void ApplySenders(SenderListingResponse listing, IReadOnlyList<CompanyResponse>? companies = null)
    {
        ArgumentNullException.ThrowIfNull(listing);

        var selected = SelectedItem;
        var names = (companies ?? [])
            .ToDictionary(company => company.CompanyId, company => company.Name, StringComparer.Ordinal);

        RemoveSenderSections();

        // With no company list, one plain section rather than a group per id.
        //
        // Grouping by an id the console cannot name would put every sender
        // under a heading reading "co_7f3a (unknown company)", which is a
        // worse answer than not grouping: the companies were not unknown, the
        // console just could not read them.
        var groups = companies is null
            ? [new SenderGroup("Senders", [.. listing.Senders
                .OrderBy(sender => string.IsNullOrWhiteSpace(sender.Label) ? sender.PrincipalId : sender.Label,
                    StringComparer.OrdinalIgnoreCase)])]
            : GroupSenders(listing.Senders, names);

        foreach (var group in groups)
        {
            var section = new SidebarSection(group.Title, []);
            _senderSections.Add(section);

            if (group.Senders.Count == 0)
            {
                section.Items.Add(new SidebarItem(
                    "No senders",
                    SidebarItemState.NotBuilt,
                    "This tenant has no configured sending principals."));
                continue;
            }

            foreach (var sender in group.Senders)
            {
                // The label is the operator's name for this principal, which is
                // what makes the list navigable. The address stays as the
                // fallback, because a principal nobody has described still has
                // to be clickable.
                var title = string.IsNullOrWhiteSpace(sender.Label) ? sender.PrincipalId : sender.Label!;

                // Provenance travels with the row, because it changes what the
                // console can offer: a store principal was minted here and is
                // revocable from the key CLI, an environment one is a
                // configuration entry this host does not own.
                var detail = $"{DescribeControl(sender.Control)} \u00b7 "
                    + PrincipalSource.Describe(sender.Source);

                var item = new SidebarItem(title, SidebarItemState.Available, detail)
                {
                    IsPaused = sender.Control.Paused,
                    IsSender = true,
                    PrincipalId = sender.PrincipalId,
                    Source = sender.Source,
                };

                section.Items.Add(item);

                if (selected is not null && selected.PrincipalId == sender.PrincipalId)
                {
                    SelectedItem = item;
                }
            }
        }

        InsertSenderSections();
    }

    /// <summary>
    /// Says why the senders could not be listed, in place of a placeholder that
    /// would otherwise read as still loading.
    /// </summary>
    /// <remarks>
    /// A failure here used to be swallowed, on the reasoning that the status bar
    /// already said why. It does not: a 403 from a missing privilege and a 500
    /// look identical behind a status bar reading "Connected", and the sidebar
    /// sat on "Loading" forever, which reads as a console still working.
    /// </remarks>
    public void SendersUnavailable(StyloMailApiException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        RemoveSenderSections();

        _senderSections.Add(new SidebarSection("Senders",
        [
            new SidebarItem(
                "Senders unavailable",
                SidebarItemState.NotBuilt,
                failure.Code is null ? failure.Message : $"the Host said {failure.Code}"),
        ]));

        InsertSenderSections();
    }

    /// <summary>
    /// Groups senders by company, named companies first and ungrouped last.
    /// </summary>
    /// <remarks>
    /// A pure function of the listing, so the grouping can be tested without a
    /// window, and so the ordering is a decision written down rather than
    /// whatever a dictionary happened to produce.
    ///
    /// <para>
    /// <b>Null and unknown are different.</b> A null company id means nobody has
    /// described this sender; an id with no matching company means somebody
    /// filed it under a company since deleted. Both need to be visible rather
    /// than dropped, and they are labelled differently because the remedies
    /// differ.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SenderGroup> GroupSenders(
        IReadOnlyList<SenderResponse> senders,
        IReadOnlyDictionary<string, string> companyNames)
    {
        ArgumentNullException.ThrowIfNull(senders);
        ArgumentNullException.ThrowIfNull(companyNames);

        var groups = new List<SenderGroup>();

        foreach (var byCompany in senders
            .GroupBy(sender => sender.CompanyId ?? UngroupedCompanyId)
            .OrderBy(group => group.Key == UngroupedCompanyId ? 1 : 0)
            .ThenBy(group => TitleFor(group.Key, companyNames), StringComparer.OrdinalIgnoreCase))
        {
            var ordered = byCompany
                .OrderBy(sender => string.IsNullOrWhiteSpace(sender.Label) ? sender.PrincipalId : sender.Label,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            groups.Add(new SenderGroup(TitleFor(byCompany.Key, companyNames), ordered));
        }

        return groups;
    }

    /// <summary>The section title for a company id.</summary>
    private static string TitleFor(string companyId, IReadOnlyDictionary<string, string> names) => companyId switch
    {
        UngroupedCompanyId => "Ungrouped",
        _ when names.TryGetValue(companyId, out var name) => name,

        // Filed under a company the listing no longer contains. Shown by id
        // rather than folded into "Ungrouped", because those are different
        // problems: one is undescribed, the other describes something gone.
        _ => $"{companyId} (unknown company)",
    };

    /// <summary>Stands in for "no company" in a grouping key, which must not be null.</summary>
    private const string UngroupedCompanyId = "\u0000ungrouped";

    private void RemoveSenderSections()
    {
        foreach (var section in _senderSections) Sections.Remove(section);
        _senderSections.Clear();
    }

    private void InsertSenderSections()
    {
        if (_senderSections.Count == 0) return;

        // Anchored before Management, which is where the senders section has
        // always sat. Recomputed rather than remembered, because the sections
        // before it can change.
        var anchor = Sections.Count;

        for (var index = 0; index < Sections.Count; index++)
        {
            if (Sections[index].Title == "Management")
            {
                anchor = index;
                break;
            }
        }

        foreach (var section in _senderSections) Sections.Insert(anchor++, section);
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

            // The principal, not the title. The title is the operator's label,
            // which is a display name: a request built from it would address a
            // sender called "Acme outbound" rather than the principal that
            // label describes.
            PrincipalId = sender.PrincipalId ?? sender.Title,
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
            PrincipalId = sender.PrincipalId ?? sender.Title,
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

    /// <summary>
    /// The label being written against the open decision.
    /// </summary>
    /// <remarks>
    /// One draft per open decision, reset whenever a different one is shown, so
    /// a label cannot be carried over and sent against a decision other than
    /// the one the operator was reading when they wrote it.
    /// </remarks>
    public FeedbackDraft Feedback { get; } = new();

    /// <summary>Shows a decision in the detail pane.</summary>
    /// <param name="decision">The full decision to render.</param>
    /// <param name="decisionCount">
    /// How many decisions the ledger holds for this message. More than one
    /// means it has been assessed more than once, which the pane says.
    /// </param>
    public void ShowDecision(DecisionResponse decision, int decisionCount = 1)
    {
        ArgumentNullException.ThrowIfNull(decision);

        _decisionLookup = DecisionLookup.Unknown;
        Decision = DecisionView.From(decision, decisionCount);

        // Reset here rather than after a successful send, so switching
        // decisions mid-draft cannot leave a half-written label pointing at the
        // wrong one.
        Feedback.Reset();

        Raise(nameof(CanSubmitFeedback));
        Raise(nameof(DecisionCount));
        Raise(nameof(DecisionHistoryNote));
        Raise(nameof(HasDecisionHistory));
    }

    /// <summary>Whether the feedback draft can be sent against the open decision.</summary>
    public bool CanSubmitFeedback =>
        Decision is not null && Feedback.CanSubmitFor(Decision.AssessmentId);

    /// <summary>Re-evaluates whether feedback can be sent, after the draft changes.</summary>
    public void RefreshFeedbackState() => Raise(nameof(CanSubmitFeedback));

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
/// Senders that belong together, by the company an operator filed them under.
/// </summary>
/// <remarks>
/// A group rather than a section, so the grouping can be computed and asserted
/// without building any UI: what belongs with what, and in what order, is a
/// decision worth testing directly rather than through a rendered sidebar.
/// </remarks>
public sealed record SenderGroup(string Title, IReadOnlyList<SenderResponse> Senders);

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

    /// <summary>
    /// The key the ledger can be filtered by to find this message's decisions.
    /// </summary>
    /// <remarks>
    /// Empty when the Host did not send one, which the console treats as "the
    /// join is not available" rather than falling back to the queue id: those
    /// are different identifiers and guessing between them would look up the
    /// wrong message's reasoning.
    /// </remarks>
    public string InternalMessageId { get; init; } = string.Empty;

    public required DeliveryState State { get; init; }

    public required int Attempts { get; init; }

    public required IReadOnlyList<string> Recipients { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ReEvaluateBy { get; init; }

    public static MessageRow From(SubmissionStatusResponse message) => new()
    {
        QueueId = message.QueueId,

        // The join key to this message's decisions. Carried on the row because
        // it is what makes "why was this held" answerable from the list.
        InternalMessageId = message.InternalMessageId,
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
