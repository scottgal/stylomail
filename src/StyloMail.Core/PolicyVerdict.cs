namespace StyloMail.Core;

/// <summary>
/// The typed outcome of a deterministic policy evaluation.
/// </summary>
/// <remarks>
/// <para>
/// These are the actions the engine may choose. There is deliberately no <c>Block</c>: blocking a
/// sender or domain is tier 4 on the authority ladder and is human authority or a separately
/// approved rule (spec 9.13). A model or a score cannot reach it, and making it unreachable in the
/// type is stronger than refusing it at a call site.
/// </para>
/// <para>
/// <see cref="Review"/> is an outcome, not a failure. A gray-zone score, conflicting evidence or a
/// missing dimension goes to the review queue with the evidence attached rather than choosing the
/// nearest action (spec 9.13, guardrail 5).
/// </para>
/// <para>
/// The vocabulary lives in Core rather than in <c>StyloMail.Policy</c> because
/// <see cref="MailAssessment"/> carries the decision record and Core cannot reference the policy
/// library. Moving it after a consumer exists would be a breaking change; it was moved while the
/// enum had no consumer outside the policy slice.
/// </para>
/// </remarks>
public enum PolicyVerdict
{
    /// <summary>No rule fired and nothing was missing: no side effect.</summary>
    Permit = 0,

    /// <summary>Scored and recorded, no side effect. The tier-0 outcome.</summary>
    Observe = 1,

    /// <summary>Goes to a human with the evidence attached. No automatic accusation.</summary>
    Review = 2,

    /// <summary>Delay and re-check. Reversible, within the pack's hold window.</summary>
    Hold = 3,

    /// <summary>Removed from the inbox pending authenticated review. The furthest automatic rung.</summary>
    Quarantine = 4,
}
