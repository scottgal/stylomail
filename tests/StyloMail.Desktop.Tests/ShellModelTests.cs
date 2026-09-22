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
        Assert.Contains("Awaiting decision", titles);
        Assert.Contains("Decisions", titles);

        Assert.Contains(model.Sections, section => section.Title == "Senders");
    }

    /// <summary>
    /// The queues offered are the three dispositions the route enumerates, and
    /// the list is closed for the same reason the client's state type is.
    /// </summary>
    /// <remarks>
    /// The shell originally offered "Queued" and "Delivered", which
    /// <c>GET /v1/messages</c> answers with a named 400: the queue lists what
    /// needs attention, not what has been accepted. Offering a destination that
    /// cannot be requested is worse than not offering it, because the operator
    /// reads the refusal as a fault rather than as a filter that does not
    /// exist.
    /// </remarks>
    [Fact]
    public void The_queues_are_exactly_the_dispositions_the_route_lists()
    {
        var model = ShellModel.CreateDefault();

        var queues = model.Sections
            .Single(section => section.Title == "Queues")
            .Items;

        Assert.Equal(
            [MessageListState.AwaitingDecision, MessageListState.Held, MessageListState.Quarantined],
            queues.Select(item => item.Queue));

        Assert.DoesNotContain(queues, item => item.Title.Contains("Queued", StringComparison.Ordinal));
        Assert.DoesNotContain(queues, item => item.Title.Contains("Delivered", StringComparison.Ordinal));
    }

    /// <summary>The senders section is real data, replacing the placeholder.</summary>
    [Fact]
    public void The_sender_listing_populates_the_sidebar()
    {
        var model = ShellModel.CreateDefault();

        model.ApplySenders(Json.Read<SenderListingResponse>(Wire.SenderListing));

        var senders = model.Sections.Single(section => section.Title == "Senders").Items;

        Assert.Equal(3, senders.Count);
        Assert.Contains(senders, item => item.PrincipalId == "compromised@example.test");
    }

    /// <summary>
    /// Provenance travels with the row, because it changes what can be offered.
    /// </summary>
    /// <remarks>
    /// A <c>store</c> principal was minted here and is revocable from the key
    /// CLI; an <c>environment</c> one is a configuration entry this host does
    /// not own. A console that could not tell them apart could not say which
    /// it was looking at.
    /// </remarks>
    [Fact]
    public void A_row_carries_where_its_authority_came_from()
    {
        var model = ShellModel.CreateDefault();
        model.ApplySenders(Json.Read<SenderListingResponse>(Wire.SenderListing));

        var rows = model.Sections.SelectMany(section => section.Items).ToList();

        var minted = rows.Single(row => row.PrincipalId == "compromised@example.test");
        Assert.Equal(PrincipalSource.Store, minted.Source);
        Assert.Contains("minted on this host", minted.Detail!, StringComparison.Ordinal);

        var configured = rows.Single(row => row.PrincipalId == "untouched@example.test");
        Assert.Equal(PrincipalSource.Environment, configured.Source);
        Assert.Contains("configured in the host", configured.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A provenance this build does not know is shown, never folded into a
    /// default.
    /// </summary>
    /// <remarks>
    /// The Host has been through one bug already where a principal vanished
    /// from this listing entirely. A console that hid a row it could not
    /// classify would be that bug arriving by a different route.
    /// </remarks>
    [Theory]
    [InlineData("store", "minted on this host")]
    [InlineData("environment", "configured in the host")]
    [InlineData("federated", "not recognised")]
    [InlineData(null, "not reported")]
    public void An_unrecognised_provenance_is_shown_rather_than_defaulted(string? source, string expected)
        => Assert.Contains(expected, PrincipalSource.Describe(source), StringComparison.OrdinalIgnoreCase);

    // ===================== grouping senders into companies =====================

    private static readonly IReadOnlyDictionary<string, string> CompanyNames =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["co_7f3a"] = "Acme" };

    private static IReadOnlyList<SenderGroup> Grouped(string json = Wire.SenderListing)
        => ShellModel.GroupSenders(Json.Read<SenderListingResponse>(json).Senders, CompanyNames);

    /// <summary>
    /// A sender is filed under the company an operator put it in, named by the
    /// company list rather than by its id.
    /// </summary>
    [Fact]
    public void Senders_group_under_their_company()
    {
        var groups = Grouped();

        var acme = groups.Single(group => group.Title == "Acme");
        Assert.Equal(["compromised@example.test"], acme.Senders.Select(sender => sender.PrincipalId));
    }

    /// <summary>
    /// Ungrouped last, and named for what it is.
    /// </summary>
    /// <remarks>
    /// The order is a decision rather than whatever a dictionary produced: a
    /// company an operator created should not be pushed below the catch-all
    /// simply because it sorts later.
    /// </remarks>
    [Fact]
    public void Named_companies_come_first_and_ungrouped_is_last()
    {
        var groups = Grouped();

        Assert.Equal(["Acme", "Ungrouped"], groups.Select(group => group.Title));
        Assert.Equal(2, groups[^1].Senders.Count);
    }

    [Fact]
    public void Senders_within_a_group_are_ordered_by_their_display_name()
    {
        var groups = Grouped();

        // Neither carries a label, so the principal is the display name and the
        // order is alphabetical rather than the order the Host sent.
        Assert.Equal(
            ["quiet@example.test", "untouched@example.test"],
            groups[^1].Senders.Select(sender => sender.PrincipalId));
    }

    /// <summary>
    /// A company id nobody can name is shown by id, and is not folded into
    /// "Ungrouped".
    /// </summary>
    /// <remarks>
    /// Those are different problems with different remedies. "Ungrouped" means
    /// nobody described the sender; an unknown id means somebody filed it under
    /// a company the listing no longer contains, which is a thing to go and
    /// look at.
    /// </remarks>
    [Fact]
    public void A_company_the_listing_does_not_contain_is_not_folded_into_ungrouped()
    {
        var groups = ShellModel.GroupSenders(
            Json.Read<SenderListingResponse>(Wire.SenderListing).Senders,
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Contains(groups, group => group.Title.Contains("unknown company", StringComparison.Ordinal));
        Assert.DoesNotContain(groups, group => group.Title == "Acme");
    }

    /// <summary>The sidebar is rebuilt, not appended to, on every load.</summary>
    [Fact]
    public void Loading_twice_does_not_duplicate_a_company_section()
    {
        var model = ShellModel.CreateDefault();
        var companies = Json.Read<CompanyListingResponse>(Wire.CompanyListing).Companies;

        model.ApplySenders(Json.Read<SenderListingResponse>(Wire.SenderListing), companies);
        model.ApplySenders(Json.Read<SenderListingResponse>(Wire.SenderListing), companies);

        Assert.Equal(2, model.Sections.Count(section => section.Title is "Acme" or "Ungrouped"));
    }

    /// <summary>
    /// With no company list, one plain section rather than a heading per
    /// unreadable id.
    /// </summary>
    /// <remarks>
    /// Grouping by an id the console cannot name would put every sender under
    /// "co_7f3a (unknown company)", which is a worse answer than not grouping:
    /// the companies were not unknown, the console just could not read them.
    /// </remarks>
    [Fact]
    public void Without_a_company_list_the_senders_stay_in_one_section()
    {
        var model = ShellModel.CreateDefault();

        model.ApplySenders(Json.Read<SenderListingResponse>(Wire.SenderListing));

        var senders = model.Sections.Single(section => section.Title == "Senders");
        Assert.Equal(3, senders.Items.Count);
    }

    /// <summary>The label is the row's title when there is one, and the principal when there is not.</summary>
    [Fact]
    public void A_row_shows_the_operators_label_in_preference_to_the_address()
    {
        var model = ShellModel.CreateDefault();
        model.ApplySenders(Json.Read<SenderListingResponse>(Wire.SenderListing));

        var rows = model.Sections.SelectMany(section => section.Items).ToList();

        Assert.Contains(rows, row => row.Title == "Acme outbound" && row.PrincipalId == "compromised@example.test");
        Assert.Contains(rows, row => row.Title == "quiet@example.test");
    }

    // ===================== the join from a message to its decision =====================

    private static ShellModel WithMessages()
    {
        var model = ShellModel.CreateDefault();
        model.ApplyMessages(Json.Read<Api.Contracts.MessageListingResponse>(Wire.MessageListing));
        return model;
    }

    /// <summary>
    /// Every message row carries the key the ledger is filtered by, which is
    /// what makes "why was this held" answerable from the list.
    /// </summary>
    [Fact]
    public void A_message_row_carries_the_join_key_to_its_decisions()
    {
        var model = WithMessages();

        Assert.Equal("msg_9c1b7e", model.Messages[0].InternalMessageId);
        Assert.Equal("msg_7d2c", model.Messages[1].InternalMessageId);
    }

    /// <summary>
    /// The lookup states are four different facts with four different remedies.
    /// </summary>
    /// <remarks>
    /// "Nothing here" would be true of all of them and useful for none. The one
    /// that matters most is the third: a Host that stopped sending the join key
    /// is a contract change, and rendering that as "this message has no
    /// decisions" would be a confident wrong answer about the ledger.
    /// </remarks>
    [Fact]
    public void Each_empty_decision_state_says_which_one_it_is()
    {
        var model = WithMessages();

        Assert.Contains("Select a message", model.DecisionUnavailableReason, StringComparison.Ordinal);

        model.SelectedMessage = model.Messages[0];
        model.BeginDecisionLookup();
        Assert.Contains("Looking up", model.DecisionUnavailableReason, StringComparison.Ordinal);

        model.NoDecisionsForMessage();
        Assert.Contains("No decisions are recorded", model.DecisionUnavailableReason, StringComparison.Ordinal);

        model.DecisionLookupFailed();
        Assert.Contains("could not be read", model.DecisionUnavailableReason, StringComparison.Ordinal);
    }

    /// <summary>A row with no join key is not a row without a decision.</summary>
    [Fact]
    public void A_message_without_a_join_key_says_the_host_stopped_sending_it()
    {
        var model = WithMessages();
        model.SelectedMessage = new MessageRow
        {
            QueueId = "q_1",
            State = Api.Contracts.DeliveryState.Held,
            Attempts = 1,
            Recipients = [],
            InternalMessageId = string.Empty,
        };

        model.DecisionLookupFailed();

        Assert.Contains("internal message id", model.DecisionUnavailableReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A message assessed more than once says so, because a re-assessment after
    /// a policy change is a real thing to have on the record and showing only
    /// the newest would hide that anything changed.
    /// </summary>
    [Fact]
    public void A_message_assessed_more_than_once_says_so()
    {
        var model = ShellModel.CreateDefault();

        var decision = Json.Read<Api.Contracts.DecisionResponse>(Wire.Decision);

        model.ShowDecision(decision);
        Assert.False(model.HasDecisionHistory);
        Assert.Equal(1, model.DecisionCount);

        model.ShowDecision(decision, decisionCount: 2);
        Assert.True(model.HasDecisionHistory);
        Assert.Contains("assessed 2 times", model.DecisionHistoryNote!, StringComparison.Ordinal);
    }

    /// <summary>The history note moves with the decision, not with the last one shown.</summary>
    [Fact]
    public void Showing_a_single_decision_clears_a_previous_history_note()
    {
        var model = ShellModel.CreateDefault();
        var decision = Json.Read<Api.Contracts.DecisionResponse>(Wire.Decision);

        model.ShowDecision(decision, decisionCount: 3);
        Assert.True(model.HasDecisionHistory);

        model.ShowDecision(decision);
        Assert.False(model.HasDecisionHistory);
    }

    /// <summary>
    /// A draft that becomes submittable has to say so on the model.
    /// </summary>
    /// <remarks>
    /// The Record button binds to <see cref="ShellModel.CanSubmitFeedback"/>,
    /// which is computed from the draft. The draft raises its own change, and
    /// without forwarding it the button stays disabled however much is typed
    /// into the recipient field.
    ///
    /// <para>
    /// The live UI harness found this: a script typed a recipient and then
    /// expected Record to enable, and it did not. Every unit test here passed
    /// throughout, because they asserted the draft's own property rather than
    /// the one the button is bound to.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_feedback_draft_that_becomes_submittable_announces_it_on_the_model()
    {
        var model = ShellModel.CreateDefault();
        model.ShowDecision(Json.Read<Api.Contracts.DecisionResponse>(Wire.Decision));

        var raised = Watch(model);

        Assert.False(model.CanSubmitFeedback);

        model.Feedback.Recipient = "alice@example.test";

        Assert.True(model.CanSubmitFeedback);
        Assert.Contains(nameof(ShellModel.CanSubmitFeedback), raised);
    }

    /// <summary>
    /// Applying the same listing again replaces the section rather than adding
    /// to it.
    /// </summary>
    /// <remarks>
    /// The guard for a defect the screenshot found: the senders section
    /// rendered three rows for one principal, because two loads ran
    /// concurrently and <c>ObservableCollection</c> is not thread-safe. The
    /// concurrency itself is fixed in the window, which marshals every model
    /// update to the UI thread. This asserts the property that made the symptom
    /// so confusing, which is that loading is not cumulative.
    /// </remarks>
    [Fact]
    public void Applying_a_listing_twice_does_not_accumulate()
    {
        var model = ShellModel.CreateDefault();

        model.ApplySenders(Json.Read<SenderListingResponse>(Wire.SenderListing));
        model.ApplySenders(Json.Read<SenderListingResponse>(Wire.SenderListing));

        Assert.Equal(3, model.Sections.Single(section => section.Title == "Senders").Items.Count);

        model.ApplyMessages(Json.Read<MessageListingResponse>(Wire.MessageListing));
        model.ApplyMessages(Json.Read<MessageListingResponse>(Wire.MessageListing));

        Assert.Equal(2, model.Messages.Count);
    }

    /// <summary>
    /// A paused principal is called out in the sidebar, and the reason is on the
    /// entry rather than behind a click: "why is this account stopped" is the
    /// question the sentence answers.
    /// </summary>
    [Fact]
    public void A_paused_sender_says_so_and_carries_the_reason()
    {
        var model = ShellModel.CreateDefault();

        model.ApplySenders(Json.Read<SenderListingResponse>(Wire.SenderListing));

        var senders = model.Sections.Single(section => section.Title == "Senders").Items;

        var paused = senders.Single(item => item.PrincipalId == "compromised@example.test");
        Assert.True(paused.IsPaused);
        Assert.Contains("credential stuffing", paused.Detail, StringComparison.Ordinal);

        var untouched = senders.Single(item => item.PrincipalId == "untouched@example.test");
        Assert.False(untouched.IsPaused);
    }

    /// <summary>
    /// A principal paused and later resumed keeps its audit trail: the console
    /// reports the history rather than "unpaused", because those are not the
    /// same thing to someone asking what happened to this account.
    /// </summary>
    [Fact]
    public void A_resumed_sender_still_shows_that_it_was_stopped()
    {
        var model = ShellModel.CreateDefault();

        model.ApplySenders(Json.Read<SenderListingResponse>(Wire.SenderListing));

        var resumed = model.Sections
            .Single(section => section.Title == "Senders")
            .Items
            .Single(item => item.PrincipalId == "quiet@example.test");

        Assert.False(resumed.IsPaused);
        Assert.Contains("resumed", resumed.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rows come from the listing, in the Host's order.</summary>
    [Fact]
    public void The_message_listing_populates_the_list()
    {
        var model = ShellModel.CreateDefault();

        model.ApplyMessages(Json.Read<MessageListingResponse>(Wire.MessageListing));

        Assert.Equal(2, model.Messages.Count);
        Assert.True(model.HasMessages);

        var first = model.Messages[0];
        Assert.Equal("q_5a2f", first.QueueId);
        Assert.Equal(DeliveryState.Held, first.State);
        Assert.Equal(["alice@example.test", "bob@example.test"], first.Recipients);
    }

    /// <summary>
    /// Paging state is held so the next page can be fetched with the Host's own
    /// cursor rather than one the console reconstructs.
    /// </summary>
    [Fact]
    public void Paging_state_is_kept_for_the_next_page()
    {
        var model = ShellModel.CreateDefault();

        model.ApplyMessages(Json.Read<MessageListingResponse>(Wire.MessageListing));

        Assert.True(model.HasMore);
        Assert.Equal("cursor_page_2", model.NextCursor);

        model.ApplyMessages(Json.Read<MessageListingResponse>(Wire.MessageListingLastPage));

        Assert.False(model.HasMore);
        Assert.Null(model.NextCursor);
        Assert.Empty(model.Messages);
    }

    /// <summary>
    /// Every entry names the route behind it, including the missing ones, so
    /// that "nothing here may be the only way to do something" stays checkable
    /// by looking at the window rather than only in review.
    /// </summary>
    [Fact]
    public void The_entries_awaiting_a_route_say_what_is_missing()
    {
        var model = ShellModel.CreateDefault();

        var decisions = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Decisions");

        Assert.Equal(SidebarItemState.AwaitingRoute, decisions.State);
        Assert.True(decisions.IsBlocked);
        Assert.Contains("enumerates the ledger", decisions.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every route the console is built on exists, so nothing else is blocked.
    /// </summary>
    /// <remarks>
    /// The counterpart to the test above, and the reason it is worth having:
    /// a blocked marker that appears on entries whose routes do exist would
    /// make the marker meaningless, and the marker is the only thing telling an
    /// operator that a pane is not merely empty.
    /// </remarks>
    [Fact]
    public void Nothing_whose_route_exists_is_marked_blocked()
    {
        var model = ShellModel.CreateDefault();

        var blocked = model.Sections
            .SelectMany(section => section.Items)
            .Where(item => item.IsBlocked)
            .Select(item => item.Title)
            .ToList();

        Assert.Equal(["Decisions"], blocked);
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
            .Single(item => item.Title == "Decisions");

        Assert.Contains("enumerates the ledger", model.EmptyListDetail, StringComparison.Ordinal);
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
