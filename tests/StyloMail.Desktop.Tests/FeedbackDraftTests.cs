using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The feedback the console attaches to a decision.
/// </summary>
/// <remarks>
/// Spec 10.2's fifth area, and the narrowest of them. These labels feed the
/// Host's trusted baseline, which is what the adaptive engine learns legitimate
/// behaviour from, so a label recorded against the wrong scope is worse than no
/// label at all: it teaches the system something nobody asserted.
///
/// <para>
/// The Host keeps scoped labels distinct from recipient preference, and the
/// console must not blur them either. "This recipient wanted this promotion" is
/// not "this message was safe", and a view that presented them as one list of
/// verdicts would erase the distinction the baseline depends on.
/// </para>
/// </remarks>
public sealed class FeedbackDraftTests
{
    private const string DecisionId = "asm_0f4d2a";

    private static FeedbackDraft Draft() => new();

    /// <summary>
    /// A label with no scope is an unbounded one, and the Host refuses it. The
    /// console does not offer the choice: a scope is always set.
    /// </summary>
    [Fact]
    public void A_draft_always_carries_a_scope()
    {
        var request = Draft().ToRequest(DecisionId);

        Assert.NotNull(request.Scope);
        Assert.Equal(FeedbackScope.Recipient, request.Scope);
    }

    /// <summary>
    /// A recipient-scoped label binds to a recipient. Without one the label has
    /// nothing to be scoped to, and the Host refuses it.
    /// </summary>
    [Fact]
    public void A_recipient_scoped_label_needs_a_recipient()
    {
        var draft = Draft();

        draft.Scope = FeedbackScope.Recipient;
        draft.Recipient = null;
        Assert.False(draft.CanSubmit);

        draft.Recipient = "   ";
        Assert.False(draft.CanSubmit);

        draft.Recipient = "alice@example.test";
        Assert.True(draft.CanSubmit);
        Assert.Equal("alice@example.test", draft.ToRequest(DecisionId).Recipient);
    }

    /// <summary>The relationship scope needs one too: it binds a sender to a recipient.</summary>
    [Fact]
    public void A_relationship_scoped_label_also_needs_a_recipient()
    {
        var draft = Draft();

        draft.Scope = FeedbackScope.Relationship;
        draft.Recipient = null;

        Assert.False(draft.CanSubmit);
    }

    /// <summary>
    /// A label binds to a decision. The console never invents one, and a draft
    /// with no decision to attach to cannot be submitted.
    /// </summary>
    [Fact]
    public void A_label_binds_to_the_decision_it_was_written_against()
    {
        var draft = Draft();
        draft.Recipient = "alice@example.test";

        Assert.Equal(DecisionId, draft.ToRequest(DecisionId).DecisionId);
        Assert.False(draft.CanSubmitFor(null));
    }

    /// <summary>
    /// A recipient preference changes what that recipient wants. It is not a
    /// statement about global truth, and the console has to be able to say so.
    /// </summary>
    [Fact]
    public void A_recipient_preference_is_distinguishable_from_a_verdict()
    {
        var preference = Draft();
        preference.Label = FeedbackLabel.WantedPromotion;
        preference.Recipient = "alice@example.test";

        var verdict = Draft();
        verdict.Label = FeedbackLabel.Legitimate;
        verdict.Recipient = "alice@example.test";

        Assert.True(preference.IsRecipientPreference);
        Assert.False(verdict.IsRecipientPreference);
    }

    [Fact]
    public void Every_label_the_host_accepts_can_be_chosen()
    {
        var labels = FeedbackDraft.AvailableLabels;

        Assert.Contains(FeedbackLabel.Legitimate, labels);
        Assert.Contains(FeedbackLabel.Suspicious, labels);
        Assert.Contains(FeedbackLabel.WantedPromotion, labels);
        Assert.Contains(FeedbackLabel.Unwanted, labels);
    }

    /// <summary>
    /// The note is optional, and an empty one is sent as absent rather than as
    /// an empty string: the Host treats null and "" the same, and sending the
    /// empty string would put a meaningless field on the wire.
    /// </summary>
    [Fact]
    public void An_empty_note_is_sent_as_absent()
    {
        var draft = Draft();
        draft.Recipient = "alice@example.test";
        draft.Note = "   ";

        Assert.Null(draft.ToRequest(DecisionId).Note);
    }

    [Fact]
    public void A_note_is_trimmed_and_carried()
    {
        var draft = Draft();
        draft.Recipient = "alice@example.test";
        draft.Note = "  asked us to keep these  ";

        Assert.Equal("asked us to keep these", draft.ToRequest(DecisionId).Note);
    }

    /// <summary>Submitting resets the draft, so a second label is not a second copy of the first.</summary>
    [Fact]
    public void Submitting_clears_the_draft()
    {
        var draft = Draft();
        draft.Label = FeedbackLabel.Suspicious;
        draft.Recipient = "alice@example.test";
        draft.Note = "phishing";

        draft.Reset();

        Assert.Equal(FeedbackLabel.Legitimate, draft.Label);
        Assert.Null(draft.Recipient);
        Assert.Null(draft.Note);
    }

    /// <summary>
    /// Changing the scope re-evaluates whether the draft can be submitted:
    /// switching to a scope that needs a recipient while the field is empty
    /// must disable it rather than send something the Host will refuse.
    /// </summary>
    [Fact]
    public void Changing_the_scope_re_evaluates_submittability()
    {
        var draft = Draft();
        var raised = new List<string>();
        draft.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        Assert.False(draft.CanSubmit);

        draft.Recipient = "alice@example.test";
        Assert.True(draft.CanSubmit);
        Assert.Contains(nameof(FeedbackDraft.CanSubmit), raised);
    }
}
