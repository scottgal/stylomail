using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Signals;
using StyloMail.Chat;
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

        var duplicate = NearDuplicate(input, context);
        if (duplicate is { } duplicateOutcome)
        {
            return duplicateOutcome;
        }

        var lure = Links(input);
        if (lure is { } lureOutcome)
        {
            return lureOutcome;
        }

        return Behaviour(input, context);
    }

    /// <summary>
    /// Attaches the author's and the conversation's behavioural evidence. Never decides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not a gate, and calling it a check was the error.</b> The stopping rule's vocabulary
    /// of "settles a message" does not apply to it: it has no dismiss, it never decides locally, and
    /// every path out of it escalates. It is the point at which behavioural evidence is attached, and
    /// its disposition is escalate because that is what sits last.
    /// </para>
    /// <para>
    /// <b>Which way it fails, and why there is no dismiss.</b> Deciding "ordinary" about a
    /// compromised account is a missed detection, and it is the interesting one: a compromised
    /// account behaves ordinarily right up until it does not, so the fifty-first message that differs
    /// where it matters is exactly what hides inside "ordinary". Escalating pays for an assessment.
    /// The safer error is to escalate, and a contributor whose safer error is escalate, sitting last,
    /// means escalate.
    /// </para>
    /// <para>
    /// <b>A condition that only starts to matter once something acts.</b> In observe-only, a false
    /// escalation costs an operator's attention. The moment an action exists it costs a person's
    /// account being restricted on probabilistic evidence, so the bar for escalating should rise.
    /// That is a posture-dependent threshold rather than a disposition, it is not differentiated here,
    /// and it is a condition on the interventions plan rather than on this one.
    /// </para>
    /// </remarks>
    private static TriageOutcome Behaviour(ChatAnalysisInput input, TriageContext context)
    {
        if (context.Observations is not { } observations)
        {
            // Nothing to read, which is not the same as reading and finding nothing.
            return new TriageOutcome
            {
                Disposition = TriageDisposition.Escalate,
                DecidedBy = null,
                NotRun = [TriageCheck.Behaviour],
                Evidence = [],
            };
        }

        var evaluator = new BehaviouralEvidenceEvaluator(context.TimeProvider);

        var evidence = new List<Evidence>();
        foreach (var key in observations.KeysFor(input, context.TenantId))
        {
            evidence.AddRange(evaluator.Evaluate(observations.Read(key), key.Scope.ToString()));
        }

        // Named only when the behaviour is why the message is going further, so an escalation nobody
        // can attribute stays distinct from one this contributed to.
        var aloneSufficient = evidence.Any(item =>
            item.Availability == EvidenceAvailability.Available
            && (item.Attributes ?? []).Any(attribute =>
                attribute.Name == BehaviouralEvidence.AloneSufficientAttribute
                && attribute.Value == "true"));

        return new TriageOutcome
        {
            Disposition = TriageDisposition.Escalate,
            DecidedBy = aloneSufficient ? TriageCheck.Behaviour : null,

            // Nothing sits behind this one, which is what makes it a contributor rather than a gate.
            NotRun = [],
            Evidence = evidence,
        };
    }

    /// <summary>
    /// Does the message contain a lure: a label that disagrees with its destination, or a host that
    /// reads as one it is not?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which way it fails: missing a lure is a missed detection, and that is the only error that
    /// matters here.</b> Escalating a message with a harmless link pays for the next check.
    /// </para>
    /// <para>
    /// <b>This check has no dismiss disposition, and that is the consequence of the asymmetry rather
    /// than a separate decision.</b> Clean links continue to check 4, because clean is not informative
    /// enough to settle: a message with an honest link can still be a compromised account. "Links
    /// that are all clean" is a finding that stops nothing.
    /// </para>
    /// </remarks>
    private static TriageOutcome? Links(ChatAnalysisInput input)
    {
        var evidence = ChatEvidenceProducer.Produce(input);

        // A positive count on either signal, read from the shared producer rather than recomputed,
        // so this check and the assessment that follows it agree about what a lure is.
        var lure = evidence.Any(item =>
            item is { Availability: EvidenceAvailability.Available, Value: > 0 }
            && item.SignalId is ChatSignals.LinkDisplayMismatch or ChatSignals.LinkIdnHomograph);

        return lure
            ? new TriageOutcome
            {
                Disposition = TriageDisposition.Escalate,
                DecidedBy = TriageCheck.Links,
                NotRun = [TriageCheck.Behaviour],
                Evidence = evidence,
            }
            : null;
    }

    /// <summary>
    /// Is this a near-duplicate of something already seen?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which way it fails: dismissing something that is not really a duplicate is a missed
    /// detection, and it is the interesting one, because repetition is how an attack hides.</b> Fifty
    /// near-identical messages followed by a fifty-first that differs precisely where it matters is
    /// the pattern this check could be talked into dismissing. Escalating a true duplicate pays for an
    /// assessment already made. **So the safer error is to escalate, and by a wide margin.**
    /// </para>
    /// <para>
    /// <b>Which is why the check starts at exact agreement rather than at nearness.</b> Exactness is
    /// the conservative end of the same axis: a message that differs at all is not a duplicate and
    /// escalates. Every step from exact toward near is a step toward dismissing things that differ,
    /// and that step needs evidence rather than judgement. **"Near" is deliberately not attempted
    /// yet**, and loosening needs the dismissal counts behind it.
    /// </para>
    /// </remarks>
    private static TriageOutcome? NearDuplicate(ChatAnalysisInput input, TriageContext context)
    {
        if (context.Campaign is not { } campaign)
        {
            // Nothing to compare against, which is not the same as comparing and finding nothing.
            return null;
        }

        // The message's own time, because triage has no clock of its own and the platform's
        // timestamp is the only one it has. Stated rather than assumed: a deployment whose messages
        // arrive long after they were sent would want this to come from the caller instead.
        var now = input.OccurredAt;

        var evidence = campaign.ObserveAndEvaluate(
            context.TenantId,
            assessmentId: input.EventId,
            internalMessageId: input.EventId,
            now,
            DimensionVector.Create(),
            ChatFingerprint.Of(input),
            senderScope: input.Membership.AuthorId);

        // The window reports a match as Available, and reports everything else (including no
        // comparable message) as Unavailable. Reading the availability rather than the presence of a
        // signal is what keeps "we looked and it was clean" apart from "we did not look".
        if (!evidence.Any(item => item.Availability == EvidenceAvailability.Available))
        {
            return null;
        }

        return new TriageOutcome
        {
            Disposition = TriageDisposition.Dismiss,
            DecidedBy = TriageCheck.NearDuplicate,
            NotRun = [TriageCheck.Links, TriageCheck.Behaviour],
            Evidence = evidence,
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
