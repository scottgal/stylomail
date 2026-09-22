namespace StyloMail.Desktop.Models;

/// <summary>
/// Whether a sidebar entry can do anything yet.
/// </summary>
/// <remarks>
/// The console shows this rather than hiding what is not built. An operator who
/// can see that "Senders" is waiting on a route knows the console is incomplete
/// and knows which piece is missing; one who sees an empty pane assumes the
/// system has no senders. In a component whose job is explaining why something
/// was held, a pane that quietly means two different things is the failure the
/// whole console exists to avoid.
/// </remarks>
public enum SidebarItemState
{
    /// <summary>Backed by a route that exists and works.</summary>
    Available,

    /// <summary>Waiting on a Host route that has not been written yet.</summary>
    AwaitingRoute,

    /// <summary>Not built at this end. No route would help.</summary>
    NotBuilt,
}

/// <summary>One entry in the sidebar.</summary>
public sealed class SidebarItem : ObservableObject
{
    private bool _isSelected;

    public SidebarItem(
        string title,
        SidebarItemState state = SidebarItemState.Available,
        string? detail = null,
        string? emptyDetail = null)
    {
        Title = title;
        State = state;
        Detail = detail;
        EmptyDetail = emptyDetail;
    }

    public string Title { get; }

    public SidebarItemState State { get; }

    /// <summary>Why it is in this state, or what it counts. Shown under the title.</summary>
    public string? Detail { get; private set; }

    /// <summary>
    /// What the middle pane says when this entry is selected and has nothing to list.
    /// </summary>
    /// <remarks>
    /// Per entry rather than one generic sentence, because "nothing here" means
    /// opposite things in different panes. Against a pane that cannot look it
    /// reads as "the queue is empty", which is exactly the confusion this
    /// console exists to prevent.
    /// </remarks>
    public string? EmptyDetail { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>
    /// Whether this entry needs a route the Host does not have.
    /// </summary>
    /// <remarks>
    /// Bound to a visible marker rather than left to a tooltip. The marker is
    /// the point: a tooltip is only found by someone who already suspects
    /// something is missing.
    /// </remarks>
    public bool IsBlocked => State is SidebarItemState.AwaitingRoute;

    public SidebarItem WithDetail(string? detail)
    {
        Detail = detail;
        Raise(nameof(Detail));
        return this;
    }
}

/// <summary>A titled group of sidebar entries.</summary>
public sealed class SidebarSection(string title, IReadOnlyList<SidebarItem> items)
{
    public string Title { get; } = title;

    public IReadOnlyList<SidebarItem> Items { get; } = items;
}
