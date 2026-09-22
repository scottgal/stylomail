using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Models;

/// <summary>
/// A label being written against a decision.
/// </summary>
/// <remarks>
/// The console's fifth and narrowest area (spec 10.2). These labels feed the
/// Host's <b>trusted baseline</b>, which is what the adaptive engine learns
/// legitimate behaviour from, so this is the one surface where a wrong entry
/// teaches the system something nobody asserted.
///
/// <para>
/// Two distinctions the Host draws and this must keep:
/// </para>
///
/// <list type="number">
/// <item>
/// A label always has a scope. The Host refuses one without, because a label
/// with no scope is an unbounded one, and this type simply has no such state.
/// </item>
/// <item>
/// A scoped recipient preference ("this recipient wants this kind of traffic")
/// is not a verdict about the message. It changes THAT recipient's preference
/// and is not global truth. <see cref="IsRecipientPreference"/> says which kind
/// a label is, so the view can present them differently rather than as one list
/// of verdicts.
/// </item>
/// </list>
/// </remarks>
public sealed class FeedbackDraft : ObservableObject
{
    private FeedbackLabel _label = FeedbackLabel.Legitimate;
    private FeedbackScope _scope = FeedbackScope.Recipient;
    private string? _recipient;
    private string? _note;

    /// <summary>
    /// The labels an operator may choose, in the order they are offered.
    /// </summary>
    /// <remarks>
    /// Explicit rather than <c>Enum.GetValues</c>, so the order is a decision
    /// rather than whatever order the enum happens to be declared in. The two
    /// verdicts come first because they are the common case, and the two
    /// preferences after them, which is also the order in which they should be
    /// read.
    /// </remarks>
    public static IReadOnlyList<FeedbackLabel> AvailableLabels { get; } =
    [
        FeedbackLabel.Legitimate,
        FeedbackLabel.Suspicious,
        FeedbackLabel.WantedPromotion,
        FeedbackLabel.Unwanted,
    ];

    /// <summary>
    /// The scopes offered.
    /// </summary>
    /// <remarks>
    /// Both are always present rather than optional, because a label without a
    /// scope is one the Host refuses: a label with no scope is an unbounded
    /// one, and the absence of a scope must not be expressible.
    /// </remarks>
    public static IReadOnlyList<FeedbackScope> AvailableScopes { get; } =
        [FeedbackScope.Recipient, FeedbackScope.Relationship];

    public FeedbackLabel Label
    {
        get => _label;
        set
        {
            if (!Set(ref _label, value)) return;
            Raise(nameof(IsRecipientPreference));
            Raise(nameof(Description));
        }
    }

    public FeedbackScope Scope
    {
        get => _scope;
        set
        {
            if (!Set(ref _scope, value)) return;
            Raise(nameof(CanSubmit));
            Raise(nameof(ScopeDescription));
        }
    }

    /// <summary>The recipient or sender identity the scope binds to.</summary>
    public string? Recipient
    {
        get => _recipient;
        set
        {
            if (!Set(ref _recipient, value)) return;
            Raise(nameof(CanSubmit));
        }
    }

    public string? Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    /// <summary>
    /// Whether this label changes a recipient's preference rather than asserting
    /// a verdict about the message.
    /// </summary>
    /// <remarks>
    /// The distinction the Host is explicit about: a recipient's preference
    /// changes what that recipient wants, not global truth. Presenting the two
    /// as one list of verdicts would be a statement about the system that the
    /// data does not support.
    /// </remarks>
    public bool IsRecipientPreference =>
        Label is FeedbackLabel.WantedPromotion or FeedbackLabel.Unwanted;

    /// <summary>What the chosen label means, said in the operator's terms.</summary>
    public string Description => Label switch
    {
        FeedbackLabel.Legitimate => "This message is legitimate and should be treated as trusted.",
        FeedbackLabel.Suspicious => "This message is suspicious and the decision should be corrected.",
        FeedbackLabel.WantedPromotion => "This recipient wants this kind of traffic. A preference, not a verdict.",
        FeedbackLabel.Unwanted => "This recipient does not want this kind of traffic. A preference, not a verdict.",
        _ => string.Empty,
    };

    public string ScopeDescription => Scope switch
    {
        FeedbackScope.Recipient => "Applies to one recipient of this message. The narrowest and most common case.",
        FeedbackScope.Relationship => "Applies to the relationship between one sender and one recipient.",
        _ => string.Empty,
    };

    /// <summary>
    /// Whether there is enough here to send.
    /// </summary>
    /// <remarks>
    /// Both scopes the Host accepts bind to a recipient or a sender identity,
    /// so a blank one is always a refusal. The scope itself is never absent:
    /// this type cannot represent a label without one.
    /// </remarks>
    public bool CanSubmit => CanSubmitFor(Recipient);

    /// <summary>Whether this draft could be submitted against a given decision.</summary>
    /// <remarks>
    /// Takes the decision id so a caller cannot submit a label that binds to
    /// nothing. The Host requires one and refuses feedback without it, because
    /// a label that binds to no decision cannot be reconciled with anything.
    /// </remarks>
    public bool CanSubmitFor(string? decisionId)
        => !string.IsNullOrWhiteSpace(decisionId) && !string.IsNullOrWhiteSpace(Recipient);

    /// <summary>Builds the wire request. Call only when <see cref="CanSubmit"/> is true.</summary>
    public FeedbackRequest ToRequest(string decisionId) => new()
    {
        DecisionId = decisionId,
        Label = Label,
        Scope = Scope,
        Recipient = Recipient?.Trim(),

        // Null rather than an empty string for a blank note. The Host treats
        // the two alike, and sending "" would put a meaningless field on the
        // wire that reads, later, like someone meant to write something.
        Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
    };

    /// <summary>
    /// Clears the draft after a successful submission.
    /// </summary>
    /// <remarks>
    /// The recipient is cleared too, and that is deliberate: keeping it would
    /// make the next label easy to send against a recipient the operator is no
    /// longer looking at, which is exactly the mis-scoped label this type
    /// exists to prevent.
    /// </remarks>
    public void Reset()
    {
        Label = FeedbackLabel.Legitimate;
        Scope = FeedbackScope.Recipient;
        Recipient = null;
        Note = null;
    }
}
