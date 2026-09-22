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

    /// <summary>Where this console points, shown at the right of the status bar.</summary>
    public string HostAddress { get; private init; } = string.Empty;

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
        }
    }

    public bool HasSelectedMessage => SelectedMessage is not null;

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

        model.Sections.Add(new SidebarSection("Review",
        [
            new SidebarItem("Senders", SidebarItemState.AwaitingRoute, "needs GET /v1/senders"),
            new SidebarItem("Messages", SidebarItemState.AwaitingRoute, "needs GET /v1/messages"),
            new SidebarItem("Quarantine", SidebarItemState.AwaitingRoute, "needs GET /v1/messages"),
            new SidebarItem(
                "Decisions",
                SidebarItemState.Available,
                "GET /v1/decisions/{id}",
                "A decision is opened by its identifier. The sidebar cannot list them until the "
                + "Host can enumerate the ledger."),
        ]));

        model.Sections.Add(new SidebarSection("Queues",
        [
            new SidebarItem("Queued", SidebarItemState.AwaitingRoute, "needs GET /v1/messages"),
            new SidebarItem("Held", SidebarItemState.AwaitingRoute, "needs GET /v1/messages"),
            new SidebarItem("Delivered", SidebarItemState.AwaitingRoute, "needs GET /v1/messages"),
        ]));

        model.SelectedItem = host;

        return model;
    }
}

/// <summary>
/// One row in the middle pane.
/// </summary>
/// <remarks>
/// Carries the two things an operator triages on: who it is from, and what the
/// system did about it. Deliberately not a subject line first: this is not a
/// mailbox, and the action is the reason the row is in this list at all.
/// </remarks>
public sealed class MessageRow
{
    public required string Identifier { get; init; }

    public required string Sender { get; init; }

    public required string Recipients { get; init; }

    public required MailAction Action { get; init; }

    public required DeliveryState DeliveryState { get; init; }

    /// <summary>Null when no decision is recorded for this message.</summary>
    public string? AssessmentId { get; init; }

    public DateTimeOffset? AssessedAt { get; init; }

    public string ActionLabel => Action.ToString();

    public string DeliveryLabel => DeliveryState.ToString();
}
