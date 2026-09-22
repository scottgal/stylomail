namespace StyloMail.Desktop.Api.Contracts;

/// <summary>
/// The intervention the Host's policy chose. Mirrors <c>StyloMail.Core.MailAction</c>.
/// </summary>
/// <remarks>
/// <b>Mirrored rather than referenced, and this is the one place to say why.</b>
/// Referencing <c>StyloMail.Core</c> would compile this console against the
/// server's own assembly, which spec 10.1 rules out: it would let the two
/// drift into agreement at build time while disagreeing at runtime, and it
/// would be the first of many references.
///
/// <para>
/// The cost of mirroring is that a rename on the Host is not a compile error
/// here. The contract tests pay that back: they assert these names against
/// literal wire JSON, so a rename fails a test named after the contract rather
/// than surfacing to an operator as an unhandled exception.
/// </para>
///
/// <para>
/// Shadow is deliberately absent, exactly as it is in Core: shadow is a mode
/// that records the action it would have taken, not an action.
/// </para>
/// </remarks>
public enum MailAction
{
    /// <summary>Eligible for the configured next hop, subject to delivery quotas.</summary>
    Allow = 0,

    /// <summary>Durably retained until a bounded re-evaluation deadline.</summary>
    Hold = 1,

    /// <summary>Durably retained for authenticated review; not delivered.</summary>
    Quarantine = 2,

    /// <summary>Responsibility declined temporarily, before acceptance. The caller may retry.</summary>
    Defer = 3,

    /// <summary>Responsibility declined permanently, before acceptance.</summary>
    Reject = 4,
}

/// <summary>Where one recipient's copy of a message has got to.</summary>
public enum DeliveryState
{
    Queued = 0,
    Held = 1,
    Quarantined = 2,
    Delivering = 3,
    Delivered = 4,
    RetryScheduled = 5,
    TerminalFailure = 6,
}

/// <summary>Whether a signal was actually produced, and over what coverage.</summary>
/// <remarks>
/// <b>Unavailable is not zero.</b> The decision pane must render it as an
/// absence, not as a low score: a dimension nobody could measure is not a
/// dimension that scored nothing, and showing it as a bar of length zero would
/// tell an operator the opposite of the truth.
/// </remarks>
public enum EvidenceAvailability
{
    /// <summary>The signal was produced and its value is meaningful.</summary>
    Available = 0,

    /// <summary>Could not be produced: provider outage, timeout, budget exhausted. Not a negative result.</summary>
    Unavailable = 1,

    /// <summary>The question does not apply to this message at all.</summary>
    NotApplicable = 2,

    /// <summary>Produced, but over reduced input coverage. Real but weaker.</summary>
    ReducedCoverage = 3,
}

/// <summary>Where a piece of evidence came from. Origin decides how much authority it carries.</summary>
public enum EvidenceOrigin
{
    /// <summary>Computed locally from the message and envelope. Reproducible, no provider involved.</summary>
    Deterministic = 0,

    /// <summary>Produced by the semantic classifier. A model assessment, never a fact.</summary>
    Semantic = 1,

    /// <summary>Derived from profile comparison and temporal behaviour.</summary>
    Behavioural = 2,

    /// <summary>Produced by policy evaluation itself.</summary>
    Policy = 3,
}

/// <summary>What a feedback label binds to.</summary>
public enum FeedbackScope
{
    /// <summary>One recipient of one message. The narrowest and most common case.</summary>
    Recipient = 0,

    /// <summary>The relationship between one sender identity and one recipient.</summary>
    Relationship = 1,
}

/// <summary>The correction being asserted.</summary>
/// <remarks>
/// A scoped recipient preference such as <see cref="WantedPromotion"/> changes
/// what that recipient wants. It is not a statement about global truth, and the
/// console must not present it as one.
/// </remarks>
public enum FeedbackLabel
{
    Legitimate = 0,
    Suspicious = 1,

    /// <summary>A scoped recipient preference: this recipient wants this kind of traffic.</summary>
    WantedPromotion = 2,

    Unwanted = 3,
}
