using StyloMail.Core;

namespace StyloMail.Assessment.Triage;

/// <summary>What triage concluded about a message.</summary>
/// <remarks>
/// <b>Three answers and no fourth.</b> There is deliberately no "unsure", because a check that cannot
/// answer has not settled the message and the next check runs. <b>And no score</b>: this project is
/// post-Bayesian, and a numeric "spamminess" invented here would be the first step back toward the
/// vocabulary this system exists to replace.
/// </remarks>
public enum TriageDisposition
{
    /// <summary>Clearly fine, stop. The overwhelming majority of channel traffic.</summary>
    Dismiss = 0,

    /// <summary>Local evidence settles it without the expensive path.</summary>
    DecideLocally = 1,

    /// <summary>This warrants the full assessment.</summary>
    Escalate = 2,
}

/// <summary>The checks, in the order they run.</summary>
/// <remarks>
/// Ordered by cost, ascending. The order is part of the contract rather than an implementation
/// detail, because <see cref="TriageOutcome.NotRun"/> is expressed in these terms and a cheap check
/// that stops early is one whose error the later checks never correct.
/// </remarks>
public enum TriageCheck
{
    /// <summary>Is this a scope this deployment watches at all?</summary>
    Scope = 0,

    /// <summary>Is this a near-duplicate of something already seen?</summary>
    NearDuplicate = 1,

    /// <summary>Links and internationalised hosts.</summary>
    Links = 2,

    /// <summary>The author's own behaviour: fan-out, novelty, velocity and drift.</summary>
    Behaviour = 3,
}

/// <summary>What triage decided, and what it did not look at.</summary>
/// <remarks>
/// <para>
/// <b><see cref="NotRun"/> is the point of this record.</b> A message dismissed on scope must not read
/// as one that passed the checks behind it: absence of a finding is a distinct state from a check that
/// looked and found nothing, and this is that rule applied to triage's own output rather than to its
/// input.
/// </para>
/// <para>
/// <see cref="DecidedBy"/> is null exactly when the disposition is
/// <see cref="TriageDisposition.Escalate"/>, because escalation is what happens when nothing settled
/// the message rather than a decision some check made.
/// </para>
/// </remarks>
public sealed record TriageOutcome
{
    public required TriageDisposition Disposition { get; init; }

    /// <summary>The check that settled it, or null when nothing did and the message escalated.</summary>
    public required TriageCheck? DecidedBy { get; init; }

    /// <summary>The checks that did not run, named rather than left to be inferred from the evidence.</summary>
    public required IReadOnlyList<TriageCheck> NotRun { get; init; }

    /// <summary>What the checks that did run produced. Evidence, never a score.</summary>
    public required IReadOnlyList<Evidence> Evidence { get; init; }
}
