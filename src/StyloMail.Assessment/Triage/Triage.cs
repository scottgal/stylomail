using StyloMail.Core;

namespace StyloMail.Assessment.Triage;

/// <summary>
/// Decides whether a message is worth the full assessment, and says what it did not look at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every check is specified by which way it fails.</b> Dismissing something that should have been
/// looked at is a missed detection; escalating something that did not need it is money. Each check
/// below names which of those is the safer error for it and is built to fail that way, because a check
/// that does not say which way it fails is one nobody has decided the cost of.
/// </para>
/// <para>
/// <b>Checks run in ascending cost and stop at the first that settles the message.</b> "Settles"
/// means the check produced a disposition, not that it found something.
/// </para>
/// </remarks>
public static class TriageEngine
{
    /// <summary>
    /// Runs the checks in order and stops at the first that settles the message.
    /// </summary>
    /// <remarks>
    /// Only the scope check exists so far. The remaining checks are added one at a time, each with
    /// its own tests, and every one of them lands in <see cref="TriageOutcome.NotRun"/> until it does,
    /// which is what keeps "not implemented" from reading as "looked at and clean".
    /// </remarks>
    public static TriageOutcome Evaluate(ChatAnalysisInput input, TriageContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var scope = Scope(input, context);
        if (scope is { } decided)
        {
            return new TriageOutcome
            {
                Disposition = decided,
                DecidedBy = TriageCheck.Scope,

                // Named rather than omitted, so a dismissal here cannot read as a message that passed
                // the checks behind it.
                NotRun = [TriageCheck.NearDuplicate, TriageCheck.Links, TriageCheck.Behaviour],
                Evidence = [],
            };
        }

        return new TriageOutcome
        {
            Disposition = TriageDisposition.Escalate,
            DecidedBy = null,
            NotRun = [TriageCheck.NearDuplicate, TriageCheck.Links, TriageCheck.Behaviour],
            Evidence = [],
        };
    }

    /// <summary>
    /// Is this a scope this deployment watches at all?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which way it fails: dismissing something we should have watched is a missed detection, and
    /// it is total</b>, because nothing downstream ever sees the message. Escalating something
    /// outside our scope costs a little traversal. So the safer error is to escalate.
    /// </para>
    /// <para>
    /// <b>But this check is configuration rather than judgement</b>, so the real risk is not the
    /// choice of error, it is that the configuration has been silently wrong for months and nothing
    /// about the traffic will reveal it. That is why its dismissal count has to be visible, and why a
    /// silent scope filter is worse than a wrong one.
    /// </para>
    /// </remarks>
    private static TriageDisposition? Scope(ChatAnalysisInput input, TriageContext context) =>
        context.WatchedChannels.Contains(input.Channel.ChannelId ?? string.Empty)
            ? null
            : TriageDisposition.Dismiss;
}
