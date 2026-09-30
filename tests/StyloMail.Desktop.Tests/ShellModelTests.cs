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

    /// <summary>Rows come from the ledger, in the Host's order, as summaries.</summary>
    [Fact]
    public void The_ledger_listing_populates_the_list()
    {
        var model = ShellModel.CreateDefault();

        model.ApplyDecisions(Json.Read<DecisionListingResponse>(Wire.DecisionListing));

        var row = Assert.Single(model.Decisions);
        Assert.True(model.HasDecisions);

        Assert.Equal("asm_0f4d2a", row.AssessmentId);
        Assert.Equal("msg_9c1b7e", row.InternalMessageId);
        Assert.Equal(MailAction.Quarantine, row.Action);
        Assert.Equal("Quarantine", row.ActionLabel);

        // The most significant reason, in policy's own words: the first, and
        // never resorted here.
        Assert.Equal(
            "Message requests credentials and the sender has no trusted history.",
            row.HeadlineReason);
    }

    /// <summary>
    /// The risk index keeps its caveat on a ledger row.
    /// </summary>
    /// <remarks>
    /// The ledger is where the number is most likely to be read alone, and it
    /// is the field most likely to be quoted as a probability. The caveat is
    /// therefore restated on the row rather than assumed to have been read in
    /// the detail pane.
    /// </remarks>
    [Fact]
    public void A_ledger_row_never_presents_the_risk_index_as_a_probability()
    {
        var model = ShellModel.CreateDefault();

        model.ApplyDecisions(Json.Read<DecisionListingResponse>(Wire.DecisionListing));

        var row = model.Decisions[0];

        Assert.Contains("an index, not a probability", row.RiskIndexLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("%", row.RiskIndexLabel, StringComparison.Ordinal);
    }

    /// <summary>
    /// A decision resting on reduced coverage can be seen while scanning.
    /// </summary>
    /// <remarks>
    /// The contract puts coverage on the summary row for exactly this reason: a
    /// decision taken over reduced coverage is a weaker one, and a reviewer
    /// should not have to open every row to find which ones those are.
    /// </remarks>
    [Fact]
    public void A_ledger_row_carries_its_coverage_flags()
    {
        var model = ShellModel.CreateDefault();

        model.ApplyDecisions(Json.Read<DecisionListingResponse>(Wire.DecisionListing));

        var row = model.Decisions[0];

        // The fixture's row has html and text disagreeing, which is a finding
        // rather than an absence.
        Assert.True(row.HasCoverage);
        Assert.Contains(row.Coverage, flag => flag.Name == "html_text_disagreement");
        Assert.Equal("1 coverage flag", row.CoverageLabel);
    }

    /// <summary>A row with nothing wrong with its coverage says nothing about it.</summary>
    /// <remarks>
    /// <c>bodyParsed</c> alone is the ordinary path and is not a finding, so a
    /// row that rendered a marker for it would put one on every row in the
    /// ledger, which is how a marker stops being read.
    /// </remarks>
    [Fact]
    public void An_ordinary_row_shows_no_coverage_marker()
    {
        var model = ShellModel.CreateDefault();

        model.ApplyDecisions(Json.Read<DecisionListingResponse>(Wire.DecisionListingCleanAndShadowed));

        var clean = model.Decisions[0];

        Assert.False(clean.HasCoverage);
        Assert.Empty(clean.Coverage);
    }

    /// <summary>
    /// A shadow decision keeps both actions, and says which one it would have
    /// been.
    /// </summary>
    /// <remarks>
    /// In shadow mode the recorded action is what happened and the proposed one
    /// is what would have happened: forwarding still occurred. A row that
    /// showed only the proposal would misreport what the platform did, and one
    /// that showed only the action would hide that policy disagreed.
    /// </remarks>
    [Fact]
    public void A_shadow_row_shows_both_the_action_and_the_proposal()
    {
        var model = ShellModel.CreateDefault();

        model.ApplyDecisions(Json.Read<DecisionListingResponse>(Wire.DecisionListingCleanAndShadowed));

        var shadowed = model.Decisions[1];

        Assert.Equal("Allow", shadowed.ActionLabel);
        Assert.True(shadowed.HasShadow);
        Assert.Equal("Would have been Quarantine", shadowed.ShadowLabel);

        // And its reduced coverage is visible while scanning, which is why the
        // contract puts coverage on the summary at all.
        Assert.True(shadowed.HasCoverage);
        Assert.Contains(shadowed.Coverage, flag => flag.Name == "truncated");

        Assert.False(model.Decisions[0].HasShadow);
    }

    /// <summary>
    /// Only one of the two listings is ever populated.
    /// </summary>
    /// <remarks>
    /// Both lists share one cell in the pane, so rows left behind in the hidden
    /// one are drawn the next time that destination is selected, under the
    /// heading of something else.
    /// </remarks>
    [Fact]
    public void Applying_one_listing_empties_the_other()
    {
        var model = ShellModel.CreateDefault();

        model.ApplyDecisions(Json.Read<DecisionListingResponse>(Wire.DecisionListing));
        Assert.True(model.HasDecisions);

        model.ApplyMessages(Json.Read<MessageListingResponse>(Wire.MessageListing));

        Assert.Empty(model.Decisions);
        Assert.False(model.HasDecisions);
        Assert.True(model.HasAnyRows);

        model.ApplyDecisions(Json.Read<DecisionListingResponse>(Wire.DecisionListing));

        Assert.Empty(model.Messages);
        Assert.False(model.HasMessages);
    }

    /// <summary>
    /// The empty state is driven by both lists, not just the message one.
    /// </summary>
    /// <remarks>
    /// This is the defect the two-list pane introduced: with the empty panel
    /// bound to <c>!HasMessages</c>, a populated ledger drew "Nothing to list"
    /// on top of its own rows.
    /// </remarks>
    [Fact]
    public void A_populated_ledger_hides_the_empty_pane()
    {
        var model = ShellModel.CreateDefault();
        var raised = Watch(model);

        model.ApplyDecisions(Json.Read<DecisionListingResponse>(Wire.DecisionListing));

        Assert.True(model.HasAnyRows);
        Assert.Contains(nameof(ShellModel.HasAnyRows), raised);
    }

    /// <summary>An unreadable ledger is not an empty one.</summary>
    /// <remarks>
    /// The ledger's emptiness is a claim about the whole system: it says
    /// nothing has been assessed on this Host. Rendering a failed listing as
    /// that claim would send an operator to look at the pipeline when the
    /// console is what could not ask.
    /// </remarks>
    [Fact]
    public void An_unreadable_ledger_says_so_rather_than_looking_empty()
    {
        var model = ShellModel.CreateDefault();

        model.SelectedItem = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Decisions");

        model.BeginLedgerLookup();
        Assert.Contains("Looking up", model.EmptyListDetail, StringComparison.Ordinal);

        model.LedgerUnavailable();
        Assert.Contains("could not be read", model.EmptyListDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The detail pane's empty text names a control that is on screen.
    /// </summary>
    /// <remarks>
    /// With the ledger selected there is no message list, so "select a message"
    /// would name something the operator cannot see.
    /// </remarks>
    [Fact]
    public void The_ledger_pane_asks_for_a_decision_not_a_message()
    {
        var model = ShellModel.CreateDefault();

        model.SelectedItem = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Ledger is not null);

        Assert.Contains("Select a decision", model.DecisionUnavailableReason, StringComparison.Ordinal);
        Assert.DoesNotContain("Select a message", model.DecisionUnavailableReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every entry names the route behind it, including the missing ones, so
    /// that "nothing here may be the only way to do something" stays checkable
    /// by looking at the window rather than only in review.
    /// </summary>
    [Fact]
    public void The_decisions_entry_lists_the_ledger_route_it_calls()
    {
        var model = ShellModel.CreateDefault();

        var decisions = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Decisions");

        Assert.Equal(SidebarItemState.Available, decisions.State);
        Assert.False(decisions.IsBlocked);

        // The route, named on the entry, so the sidebar stays checkable at a
        // glance against what the client actually sends.
        Assert.Equal("GET /v1/decisions", decisions.Detail);
        Assert.NotNull(decisions.Ledger);
        Assert.Null(decisions.Ledger!.Action);
    }

    /// <summary>
    /// Nothing is blocked any more, and this is the assertion that would have
    /// caught the stale marker.
    /// </summary>
    /// <remarks>
    /// "Decisions" sat marked <c>AwaitingRoute</c> claiming nothing enumerated
    /// the ledger, long after <c>GET /v1/decisions</c> shipped and while the
    /// client was already complete against it. A blocked marker outliving its
    /// blocker is a false statement about the system that survives review by
    /// looking deliberate, which is exactly what this test is for.
    ///
    /// <para>
    /// It asserts the whole set rather than the absence of one entry, so the
    /// next entry to be unblocked has to come here and say so.
    /// </para>
    /// </remarks>
    [Fact]
    public void Nothing_is_marked_blocked_because_every_listed_route_exists()
    {
        var model = ShellModel.CreateDefault();

        var blocked = model.Sections
            .SelectMany(section => section.Items)
            .Where(item => item.IsBlocked)
            .Select(item => item.Title)
            .ToList();

        Assert.Empty(blocked);
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
    /// <remarks>
    /// Built on a synthesised entry rather than one from
    /// <see cref="ShellModel.CreateDefault"/>, because nothing in the shipped
    /// sidebar is blocked any more. The mechanism stays covered: it is what
    /// made the stale ledger marker visible, and the next gap will use it.
    /// </remarks>
    [Fact]
    public void A_blocked_pane_explains_that_it_needs_a_route()
    {
        var model = ShellModel.CreateDefault();

        model.SelectedItem = new SidebarItem(
            "Traffic",
            SidebarItemState.AwaitingRoute,
            "needs a route that does not exist yet");

        Assert.Contains("needs a route that does not exist yet", model.EmptyListDetail, StringComparison.Ordinal);
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
    /// An empty queue names both of its causes, because the listing cannot tell
    /// them apart and only one of them is a fault.
    /// </summary>
    [Fact]
    public void An_empty_queue_names_both_of_its_causes()
    {
        var model = ShellModel.CreateDefault();

        model.SelectedItem = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Awaiting decision");

        // The entry's own sentence, which says what an empty listing means.
        Assert.Contains("Nothing is awaiting a decision", model.EmptyListDetail, StringComparison.Ordinal);

        // The second cause, which the entry cannot know on its own: nothing could
        // be queued rather than nothing was. Both ways of being unable to assess
        // are named, because they fail alike.
        Assert.Contains("nothing could be", model.EmptyListDetail, StringComparison.Ordinal);
        Assert.Contains("no semantic provider", model.EmptyListDetail, StringComparison.Ordinal);
        Assert.Contains("cannot reach the one it has", model.EmptyListDetail, StringComparison.Ordinal);

        // Nothing has reported a failure yet, so the text must not point at a
        // list that is not on screen.
        Assert.DoesNotContain("failed checks below", model.EmptyListDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second cause belongs to the queues and is not pasted onto every pane.
    /// </summary>
    [Fact]
    public void An_empty_ledger_does_not_borrow_the_queue_cause()
    {
        var model = ShellModel.CreateDefault();

        model.SelectedItem = model.Sections
            .SelectMany(section => section.Items)
            .Single(item => item.Title == "Decisions");

        Assert.DoesNotContain("no semantic provider", model.EmptyListDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The queue's empty text points at the failed checks only when the Host
    /// reported some: the sentence and the list are one claim, and the pane
    /// would be lying if it pointed at a list that is not on screen.
    /// </summary>
    [Fact]
    public void The_empty_queue_points_at_the_failed_checks_only_when_there_are_any()
    {
        var model = ShellModel.CreateDefault();

        model.SelectedItem = model.Sections
            .SelectMany(section => section.Items)
            .First(item => item.Queue is not null);

        // Ready: nothing to point at.
        model.Status = Ready;
        Assert.DoesNotContain("failed checks below", model.EmptyListDetail, StringComparison.Ordinal);

        // Not ready. The pointer appears without the destination being selected
        // again, which is the notification the sentence depends on: readiness
        // arrives after the pane is already on screen.
        model.Status = HostStatus.From(new ReadinessResponse
        {
            Status = "not_ready",
            FailedChecks = ["assessor_unavailable"],
        });

        Assert.Contains("failed checks below", model.EmptyListDetail, StringComparison.Ordinal);
        Assert.True(model.HasFailedChecks);
        Assert.Equal(["assessor_unavailable"], model.FailedChecks);
    }

    /// <summary>
    /// The failed checks reached the model, which is where the middle pane reads
    /// them from: it prints them under the sentence that promises them.
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

    /// <summary>
    /// The status bar's feed line is notified, not merely assigned.
    /// </summary>
    /// <remarks>
    /// This is the defect class the whole file exists for: a value that is
    /// right in the model at the moment it is set, and absent from the window
    /// because nothing announced it. The feed's state arrives from a transport
    /// thread long after the window is up, so a missing notification here would
    /// leave the operator looking at whatever the feed was doing when the
    /// console started.
    /// </remarks>
    [Fact]
    public void The_feed_state_is_announced_when_it_changes()
    {
        var model = ShellModel.CreateDefault();
        var raised = Watch(model);

        model.LiveFeed = LiveFeedStatus.From(TrafficFeedState.Live, screenMayBeStale: false);

        Assert.Contains(nameof(ShellModel.LiveFeedHeadline), raised);
        Assert.Contains(nameof(ShellModel.LiveFeedDetail), raised);
        Assert.Contains(nameof(ShellModel.ScreenMayBeStale), raised);
        Assert.Equal("Live", model.LiveFeedHeadline);
        Assert.False(model.ScreenMayBeStale);
    }

    /// <summary>
    /// A screen that may have fallen behind is reported as such by the model.
    /// </summary>
    /// <remarks>
    /// The transition that matters is Live to Dropped, because that is the one
    /// where the operator was watching something current and is now watching
    /// the last thing it said.
    /// </remarks>
    [Fact]
    public void A_feed_that_stops_is_reported_as_a_screen_that_may_be_stale()
    {
        var model = ShellModel.CreateDefault();
        var raised = Watch(model);

        model.LiveFeed = LiveFeedStatus.From(TrafficFeedState.Live, screenMayBeStale: false);
        raised.Clear();

        model.LiveFeed = LiveFeedStatus.From(TrafficFeedState.Dropped, screenMayBeStale: true);

        Assert.Contains(nameof(ShellModel.ScreenMayBeStale), raised);
        Assert.True(model.ScreenMayBeStale);
        Assert.Contains("may be out of date", model.LiveFeedHeadline, StringComparison.Ordinal);
    }

    /// <summary>
    /// A deployment with no feed never claims the screen is stale.
    /// </summary>
    /// <remarks>
    /// The claim has to survive the trip through the model, not only through
    /// <see cref="LiveFeedStatus"/>: this is the state most deployments are in,
    /// and a console that warned here would have taught its operator to ignore
    /// the warning before it ever mattered.
    /// </remarks>
    [Fact]
    public void A_deployment_with_no_feed_does_not_claim_the_screen_is_stale()
    {
        var model = ShellModel.CreateDefault();

        model.LiveFeed = LiveFeedStatus.From(TrafficFeedState.NoFeed, screenMayBeStale: true);

        Assert.False(model.ScreenMayBeStale);
    }

    /// <summary>
    /// A console that has not asked yet still says something.
    /// </summary>
    /// <remarks>
    /// The initial value, and the one every headless render and screenshot
    /// shows. An empty status bar would read as "no feed problem" rather than
    /// "not asked", which is the opposite of what is true.
    /// </remarks>
    [Fact]
    public void An_unstarted_feed_has_a_headline_rather_than_a_blank()
    {
        var model = ShellModel.CreateDefault();

        Assert.False(string.IsNullOrWhiteSpace(model.LiveFeedHeadline));
        Assert.False(string.IsNullOrWhiteSpace(model.LiveFeedDetail));
        Assert.False(model.ScreenMayBeStale);
    }
}
