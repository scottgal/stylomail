using System.Collections.ObjectModel;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Models;

/// <summary>
/// Whether a sidebar entry can do anything yet.
/// </summary>
/// <remarks>
/// The console shows this rather than hiding what is not built. An operator who
/// can see that an entry is waiting on a route knows the console is incomplete
/// and knows which piece is missing; one who sees an empty pane assumes the
/// system has nothing in it. In a component whose job is explaining why
/// something was held, a pane that quietly means two different things is the
/// failure the whole console exists to avoid.
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
    private bool _isPaused;
    private string? _detail;

    public SidebarItem(
        string title,
        SidebarItemState state = SidebarItemState.Available,
        string? detail = null,
        string? emptyDetail = null,
        MessageListState? queue = null)
    {
        Title = title;
        State = state;
        Detail = detail;
        EmptyDetail = emptyDetail;
        Queue = queue;
    }

    public string Title { get; }

    public SidebarItemState State { get; }

    /// <summary>
    /// Which disposition this entry lists, when it is a queue.
    /// </summary>
    /// <remarks>
    /// The typed enum rather than a wire string, so the sidebar cannot offer a
    /// destination the route would refuse.
    /// </remarks>
    public MessageListState? Queue { get; }

    /// <summary>Why it is in this state, or what it counts. Shown under the title.</summary>
    public string? Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

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

    /// <summary>Whether this principal's outbound delivery is currently stopped.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (!Set(ref _isPaused, value)) return;

            // The two controls are computed from this, so a pause applied
            // elsewhere has to move the buttons with it.
            Raise(nameof(CanPause));
            Raise(nameof(CanResume));
        }
    }

    /// <summary>
    /// Whether the pause control applies to this entry.
    /// </summary>
    /// <remarks>
    /// Only for principals, and only when they are not already stopped. Offering
    /// a control that would do nothing is worse than offering none: an operator
    /// who presses it and sees no change has to work out whether the console is
    /// broken or the account was already stopped.
    /// </remarks>
    public bool CanPause => IsSender && !IsPaused;

    /// <summary>The resume control, offered only where a pause is in force.</summary>
    public bool CanResume => IsSender && IsPaused;

    /// <summary>
    /// A stable identity for this row's pause control, for the UI harness.
    /// </summary>
    /// <remarks>
    /// The row's controls are generated from a data template, so they cannot
    /// have a unique <c>x:Name</c>: a name inside a template is per instance
    /// and unreachable from outside it. An automation id can be bound, so it
    /// can carry the principal and be unique.
    ///
    /// <para>
    /// <b>Found the hard way.</b> The harness's first attempt targeted the
    /// pause button by its text and reached a hidden one on a different row,
    /// because a locator matches controls that are not visible. Naming the
    /// target removes the ambiguity rather than depending on the order the
    /// visual tree happens to be in.
    /// </para>
    ///
    /// <para>
    /// Built from the principal, not the title. The title became the operator's
    /// label when senders gained one, so an id derived from it changed the
    /// moment somebody renamed a sender, and a harness target that moves when a
    /// display name changes is not an identity. This is the third place the
    /// same distinction had to be drawn: the row title, the selection restore,
    /// and here.
    /// </para>
    /// </remarks>
    public string PauseAutomationId => $"pause-sender-{PrincipalId ?? Title}";

    /// <summary>A stable identity for this row's resume control, for the UI harness.</summary>
    public string ResumeAutomationId => $"resume-sender-{PrincipalId ?? Title}";

    /// <summary>A stable identity for this row's profile control, for the UI harness.</summary>
    public string ProfileAutomationId => $"profile-sender-{PrincipalId ?? Title}";

    /// <summary>Whether this entry is a sending principal rather than a destination.</summary>
    public bool IsSender { get; init; }

    /// <summary>
    /// The principal this entry stands for, when it is a sender.
    /// </summary>
    /// <remarks>
    /// The title is the operator's label, which is not a stable identity: two
    /// principals could carry the same label, and a label is exactly the thing
    /// an operator renames. Restoring a selection by title after a reload would
    /// then select the wrong row, or none.
    /// </remarks>
    public string? PrincipalId { get; init; }

    /// <summary>
    /// Where this sender's authority came from, when it is a sender.
    /// </summary>
    /// <remarks>
    /// Carried so the profile screen can say which kind it is editing and
    /// refuse what does not apply, rather than offering a control the Host will
    /// reject. An unrecognised value is kept as-is: a row whose provenance the
    /// console cannot name is still a row that exists, and hiding it would
    /// reproduce the bug the field was added to fix.
    /// </remarks>
    public string? Source { get; init; }

    /// <summary>Whether this entry needs a route the Host does not have.</summary>
    /// <remarks>
    /// Bound to a visible marker rather than left to a tooltip. The marker is
    /// the point: a tooltip is only found by someone who already suspects
    /// something is missing.
    /// </remarks>
    public bool IsBlocked => State is SidebarItemState.AwaitingRoute;

    /// <summary>Sets the subtitle, announcing it so the pane header follows.</summary>
    public SidebarItem WithDetail(string? detail)
    {
        Detail = detail;
        return this;
    }
}

/// <summary>A titled group of sidebar entries.</summary>
public sealed class SidebarSection
{
    public SidebarSection(string title, IEnumerable<SidebarItem> items)
    {
        Title = title;
        Items = [.. items];
    }

    public string Title { get; }

    /// <summary>
    /// Observable because one of these sections is filled from the network
    /// after the window is on screen. The senders are not known at construction
    /// and replacing the collection instead of its contents would drop the
    /// selection and the bindings with it.
    /// </summary>
    public ObservableCollection<SidebarItem> Items { get; }
}
