using StyloMail.Desktop.Api;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The console's write actions: what is asked, what is confirmed, what is reported after.
/// </summary>
/// <remarks>
/// Two things make these different from every other call the console makes.
/// They are <b>consequential</b>: a pause stops an account's outbound mail and a
/// release delivers mail that was deliberately held. And they are
/// <b>audited</b>: the Host records who did it, so the console must not imply an
/// action happened when it did not.
///
/// <para>
/// The rule these tests exist to hold is that a failure is never reported as a
/// success. Showing "Released" over a call that returned 503 tells an operator a
/// quarantined message is on its way when it is still sitting in the queue, and
/// the next thing they do is stop looking at it.
/// </para>
/// </remarks>
public sealed class ActionTests
{
    private static ShellModel WithSenders()
    {
        var model = ShellModel.CreateDefault();
        model.ApplySenders(Json.Read<Api.Contracts.SenderListingResponse>(Wire.SenderListing));
        return model;
    }

    private static SidebarItem Sender(ShellModel model, string principalId)
        => model.Sections
            .Single(section => section.Title == "Senders")
            .Items
            .Single(item => item.Title == principalId);

    [Fact]
    public void Nothing_is_pending_on_a_fresh_model()
    {
        Assert.False(ShellModel.CreateDefault().HasPendingAction);
    }

    /// <summary>
    /// Asking for an action does not perform it. The request carries what will
    /// happen and to whom, which is what a confirmation has to show.
    /// </summary>
    [Fact]
    public void Pausing_a_sender_states_what_it_will_do_before_doing_it()
    {
        var model = WithSenders();
        var sender = Sender(model, "untouched@example.test");

        model.RequestPause(sender);

        Assert.True(model.HasPendingAction);
        Assert.NotNull(model.PendingAction);
        Assert.Contains("untouched@example.test", model.PendingAction.Title, StringComparison.Ordinal);
        Assert.Contains("stop", model.PendingAction.Consequence, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The reason is collected against the consequence being shown, and nothing
    /// can be carried out until it is given.
    /// </summary>
    /// <remarks>
    /// The reason is the audit record for the intervention. A pause applied
    /// without one is, months later, indistinguishable from one nobody
    /// explained, and the operator is the only person who can still know why.
    /// The Host accepts an empty reason; the console does not.
    /// </remarks>
    [Fact]
    public void An_action_cannot_be_confirmed_until_a_reason_is_given()
    {
        var model = WithSenders();
        model.RequestPause(Sender(model, "untouched@example.test"));

        Assert.False(model.CanConfirmAction);
        Assert.Null(model.ConfirmedAction);

        model.PendingReason = "   ";
        Assert.False(model.CanConfirmAction);

        model.PendingReason = "credential stuffing from this account";
        Assert.True(model.CanConfirmAction);
        Assert.Equal("credential stuffing from this account", model.ConfirmedAction!.Reason);
    }

    /// <summary>A new request does not inherit the previous one's reason.</summary>
    [Fact]
    public void A_new_request_starts_with_no_reason()
    {
        var model = WithSenders();

        model.RequestPause(Sender(model, "untouched@example.test"));
        model.PendingReason = "the first reason";
        model.RequestPause(Sender(model, "quiet@example.test"));

        Assert.False(model.CanConfirmAction);
        Assert.Equal(string.Empty, model.PendingReason);
    }

    /// <summary>
    /// Releasing a quarantined message is audited, and the consequence says
    /// what it actually is: the message is delivered.
    /// </summary>
    [Fact]
    public void Releasing_a_quarantine_says_the_message_will_be_delivered()
    {
        var model = ShellModel.CreateDefault();
        model.ApplyMessages(Json.Read<Api.Contracts.MessageListingResponse>(Wire.MessageListing));
        var quarantined = model.Messages.First(message => message.State == Api.Contracts.DeliveryState.Quarantined);

        model.RequestRelease(quarantined);

        Assert.True(model.HasPendingAction);
        Assert.Contains("delivered", model.PendingAction!.Consequence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_successful_action_is_reported_and_clears_the_request()
    {
        var model = WithSenders();
        model.RequestPause(Sender(model, "untouched@example.test"));

        model.CompleteAction("Paused untouched@example.test.", failed: false);

        Assert.False(model.HasPendingAction);
        Assert.Null(model.PendingAction);
        Assert.False(model.LastActionFailed);
        Assert.Contains("Paused", model.LastActionResult!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one that matters. A failed call must not read as a success, and it
    /// must carry the Host's own words rather than a generic apology.
    /// </summary>
    [Fact]
    public void A_failed_action_is_reported_as_a_failure_with_the_hosts_reason()
    {
        var model = WithSenders();
        model.RequestPause(Sender(model, "untouched@example.test"));

        model.CompleteAction(
            "The pause could not be durably recorded, so it was not applied.",
            failed: true);

        Assert.True(model.LastActionFailed);
        Assert.Contains("was not applied", model.LastActionResult!, StringComparison.Ordinal);

        // And the request is gone, so the operator cannot press it again
        // believing the first press did nothing.
        Assert.False(model.HasPendingAction);
    }

    [Fact]
    public void Cancelling_clears_the_request_and_records_nothing()
    {
        var model = WithSenders();
        model.RequestPause(Sender(model, "untouched@example.test"));
        model.PendingReason = "reason";

        model.CancelAction();

        Assert.False(model.HasPendingAction);
        Assert.False(model.CanConfirmAction);
        Assert.Null(model.LastActionResult);
    }

    /// <summary>
    /// A second request replaces the first rather than queueing behind it.
    /// Queueing would mean one confirmation producing two actions.
    /// </summary>
    [Fact]
    public void A_new_request_replaces_the_pending_one()
    {
        var model = WithSenders();

        model.RequestPause(Sender(model, "untouched@example.test"));
        model.RequestPause(Sender(model, "quiet@example.test"));

        Assert.Contains("quiet@example.test", model.PendingAction!.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resuming is the audited mirror, and it is offered only where a pause is
    /// in force. Offering it on an account that is running would be a control
    /// that does nothing, and an operator who pressed it and saw no change
    /// could not tell whether the console was broken.
    /// </summary>
    [Fact]
    public void Resuming_is_offered_only_for_a_paused_sender()
    {
        var model = WithSenders();

        var paused = Sender(model, "compromised@example.test");
        var running = Sender(model, "untouched@example.test");

        Assert.True(paused.CanResume);
        Assert.False(paused.CanPause);

        Assert.True(running.CanPause);
        Assert.False(running.CanResume);

        model.RequestResume(paused);

        Assert.Contains("resume", model.PendingAction!.Title, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The console never names the actor. The Host takes it from the
    /// authenticated principal, so a client cannot record someone else as
    /// having released a message.
    /// </summary>
    [Fact]
    public void An_action_never_carries_an_actor()
    {
        var model = WithSenders();
        model.RequestPause(Sender(model, "untouched@example.test"));

        Assert.DoesNotContain("by ", model.PendingAction!.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", model.PendingAction.Consequence, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A destination is not a principal. Offering pause on the Host entry or a
    /// queue would be offering a control with no meaning.
    /// </summary>
    [Fact]
    public void A_non_sender_entry_offers_no_sender_controls()
    {
        var model = ShellModel.CreateDefault();

        var host = model.Sections.SelectMany(section => section.Items).Single(item => item.Title == "Host");

        Assert.False(host.CanPause);
        Assert.False(host.CanResume);
    }
}
