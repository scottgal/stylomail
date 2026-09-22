namespace StyloMail.Core;

/// <summary>
/// Whether a signal was actually produced, and over what coverage.
/// </summary>
/// <remarks>
/// <b>Unknown is a distinct state, not a zero score and not evidence of innocence.</b>
/// A missing semantic signal must never be silently coerced to <c>0.0</c> and fed into
/// a baseline comparison, that would fabricate a "calm, transactional, no-lure"
/// message out of an outage. Consumers are expected to mask unavailable dimensions
/// and record the reduced coverage, not fill them.
/// </remarks>
public enum EvidenceAvailability
{
    /// <summary>The signal was produced and its value is meaningful.</summary>
    Available = 0,

    /// <summary>
    /// The signal could not be produced, provider outage, timeout, budget exhausted,
    /// circuit breaker open. Explicitly <em>not</em> a negative result.
    /// </summary>
    Unavailable = 1,

    /// <summary>The question does not apply to this message at all (e.g. no attachments present).</summary>
    NotApplicable = 2,

    /// <summary>
    /// Produced, but over reduced input coverage, encrypted or password-protected
    /// content, an unparseable part, a truncated body. The value is real but weaker.
    /// </summary>
    ReducedCoverage = 3,
}

/// <summary>Where a piece of evidence came from. Origin determines how much authority it may carry.</summary>
public enum EvidenceOrigin
{
    /// <summary>Computed locally from the message and envelope, reproducible, no provider involved.</summary>
    Deterministic = 0,

    /// <summary>Produced by the semantic classifier (Jev). A model assessment, never a fact.</summary>
    Semantic = 1,

    /// <summary>Derived from profile comparison and temporal behaviour (drift, velocity, acceleration).</summary>
    Behavioural = 2,

    /// <summary>Produced by policy evaluation itself.</summary>
    Policy = 3,
}
