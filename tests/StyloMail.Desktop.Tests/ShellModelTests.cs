using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// What the window shows, tested without a window.
/// </summary>
/// <remarks>
/// <see cref="ShellModel"/> exists so that the three panes can be reasoned
/// about, and these tests exist because two of the defects below were found by
/// looking at a rendered screenshot rather than by any test: a pane header that
/// disagreed with the status bar, and a status bar whose right-hand side was
/// silently empty. Both are binding-notification bugs, and both are invisible
/// to anything that only checks the value is right at the moment it is set.
/// </remarks>
public sealed class ShellModelTests
{
    private static HostStatus Ready => HostStatus.From(new ReadinessResponse { Status = "ready" });

    /// <summary>Records which properties a model announced a change for.</summary>
    private static List<string> Watch(ShellModel model)
    {
        var raised = new List<string>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        return raised;
    }

    [Fact]
    public void The_sidebar_offers_every_area_the_design_names()
    {
        var model = ShellModel.CreateDefault();

        var titles = model.Sections
            .SelectMany(section => section.Items)
            .Select(item => item.Title)
            .ToList();

        Assert.Contains("Host", titles);
        Assert.Contains("Senders", titles);
        Assert.Contains("Messages", titles);
        Assert.Contains("Quarantine", titles);
        Assert.Contains("Decisions", titles);
    }

    /// <summary>
    /// Every entry names the route behind it, including the missing ones, so
    /// that "nothing here may be the only way to do something" stays checkable
    /// by looking at the window rather than only in review.
    /// </summary>
    [Fact]
    public void The_entries_awaiting_a_route_say_which_route()
    {
        var model = ShellModel.CreateDefault();

        var senders = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Senders");

        Assert.Equal(SidebarItemState.AwaitingRoute, senders.State);
        Assert.True(senders.IsBlocked);
        Assert.Contains("/v1/senders", senders.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Selecting_an_item_puts_its_title_in_the_pane_header()
    {
        var model = ShellModel.CreateDefault();

        var decisions = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Decisions");

        model.SelectedItem = decisions;

        Assert.Equal("Decisions", model.SelectedPaneTitle);
    }

    /// <summary>
    /// The first defect the screenshot found.
    /// </summary>
    /// <remarks>
    /// The selected item's subtitle is what the pane header displays, and the
    /// status arrives after selection. Updating the item's detail without
    /// announcing it on the model leaves the header showing whatever was true
    /// when the item was selected: in the captured frame, "Not checked yet" sat
    /// directly above a status bar reading "Connected".
    /// </remarks>
    [Fact]
    public void A_status_change_reaches_the_pane_header_for_the_selected_item()
    {
        var model = ShellModel.CreateDefault();
        var raised = Watch(model);

        model.Status = Ready;

        Assert.Equal("Connected", model.SelectedPaneDetail);
        Assert.Contains(nameof(ShellModel.SelectedPaneDetail), raised);
    }

    /// <summary>
    /// The second defect. The binding existed and the property did not, so the
    /// right-hand side of the status bar rendered empty with no error anywhere.
    /// </summary>
    [Fact]
    public void The_host_address_is_available_to_the_status_bar()
    {
        var model = ShellModel.CreateDefault(new Uri("https://stylomail.example.test:8443"));

        Assert.Equal("https://stylomail.example.test:8443/", model.HostAddress);
    }

    /// <summary>
    /// An empty pane has to say which of two opposite things is true: there is
    /// nothing to show, or the console cannot look. "Nothing here" against a
    /// pane that is blocked reads as the first, which is the wrong one.
    /// </summary>
    [Fact]
    public void A_blocked_pane_explains_that_it_needs_a_route()
    {
        var model = ShellModel.CreateDefault();

        model.SelectedItem = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Senders");

        Assert.Contains("/v1/senders", model.EmptyListDetail, StringComparison.Ordinal);
        Assert.Contains("does not read the database", model.EmptyListDetail, StringComparison.Ordinal);
    }

    /// <summary>A pane that is not blocked still says something specific to itself.</summary>
    [Fact]
    public void An_available_pane_explains_what_it_is_for()
    {
        var model = ShellModel.CreateDefault();

        model.SelectedItem = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Host");

        Assert.NotEqual("Nothing here.", model.EmptyListDetail);
        Assert.Contains("status bar", model.EmptyListDetail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Not ready carries the failed checks, and the sidebar entry is where an
    /// operator sees them without opening anything.
    /// </summary>
    [Fact]
    public void Failed_checks_are_exposed_and_selected_state_is_single()
    {
        var model = ShellModel.CreateDefault();
        var decisions = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Decisions");

        model.SelectedItem = decisions;

        Assert.True(decisions.IsSelected);
        Assert.False(model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Host")
            .IsSelected);

        model.Status = HostStatus.From(new ReadinessResponse
        {
            Status = "not_ready",
            FailedChecks = ["spool"],
        });

        Assert.True(model.HasFailedChecks);
        Assert.Equal(["spool"], model.FailedChecks);
    }
}
