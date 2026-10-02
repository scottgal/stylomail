using StyloMail.Core;

namespace StyloMail.Cascade;

/// <summary>
/// The rule that decides when a stronger model is asked. Deterministic code, no I/O, no clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Probabilistic components produce evidence; only deterministic policy authorises side effects.</b>
/// So the escalation decision is not a model's judgement and not a score: it is code, it is
/// reproducible, and its result is carried onto the returned rows as a site naming which model
/// answered and which condition asked it.
/// </para>
/// <para>
/// <b>UNKNOWN escalates rather than counting as an answer.</b> A local <c>Unavailable</c> row, and a
/// dimension that was asked and came back with no row at all, both mean the local model did not
/// answer. Neither is a zero and neither is a weak negative, so neither is passed through.
/// </para>
/// <para>
/// <b>A question that was never askable is never escalated.</b> A <c>NotApplicable</c> row is the
/// message saying the question has no referent (no conversation window, so nothing to continue). It
/// is not re-asked, because re-asking would convert "this does not apply" into a provider call, and a
/// row that came back <c>Unavailable</c> from that call would arm the policy engine's
/// asked-and-unanswered gate on a message where nothing was ever asked.
/// </para>
/// <para>
/// <b>Two of the conditions are narrowed by the policy weights, and the rest are not.</b> An
/// indecisive value and a run disagreement are conditions about a NUMBER, and a number on a dimension
/// the engine does not score cannot move any decision. An unavailable row or a partial read is a
/// condition about the answer's worth, and worth does not depend on the weight: a dimension that
/// carries no weight today can carry one after a policy change, and an answer nobody can read is not
/// a better answer on a dimension that is currently unweighted.
/// </para>
/// </remarks>
public static class CascadeEscalationRule
{
    /// <summary>
    /// The attribute name a producer writes when the row's value was produced over cut input.
    /// </summary>
    /// <remarks>
    /// <b>Read by name and by presence, never by text.</b> The local adapter's own shortening reasons
    /// are private constants in another assembly, and a copy of their text here would be a second
    /// definition that drifts silently when the producer rewords one. Matching the NAME and the
    /// presence of a value means any future producer that attaches a reason to an <c>Available</c> row
    /// also escalates, which is the fail-closed direction: the cost is a strong-model call, and the
    /// alternative is a partial read passing as a whole one.
    /// </remarks>
    public const string AvailabilityReasonAttribute = "reason";

    /// <summary>Decides which dimensions, if any, are asked of the stronger model.</summary>
    /// <param name="input">The question set as the caller asked it.</param>
    /// <param name="localEvidence">What the local arm returned for it.</param>
    /// <param name="options">The thresholds, the trust table and the injected policy weights.</param>
    /// <param name="priorLocalValues">
    /// A previous local answer for the same input, by dimension id, when the deployment holds one.
    /// Null means no prior is available, which is the ordinary case for a deployment with a single
    /// local endpoint; it does NOT mean the local answer is unvaried, only that nothing here can see
    /// a variation. See <see cref="CascadeOptions.DefaultDisagreementTolerance"/> for why a repeat call
    /// on one host cannot stand in for it.
    /// </param>
    public static CascadeDecision Decide(
        SemanticMailInput input,
        IReadOnlyList<Evidence> localEvidence,
        CascadeOptions options,
        IReadOnlyDictionary<string, double>? priorLocalValues = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(localEvidence);
        ArgumentNullException.ThrowIfNull(options);

        var byId = new Dictionary<string, Evidence>(StringComparer.Ordinal);
        foreach (var row in localEvidence)
        {
            // First occurrence wins, matching how the adaptive engine reads a duplicated list. The
            // classifier answers each question once, so a duplicate means a caller concatenated two
            // evidence lists, and taking the first is arbitrary but deterministic.
            byId.TryAdd(row.SignalId, row);
        }

        var escalated = new List<SemanticDimension>();
        var reasons = new Dictionary<string, IReadOnlyList<EscalationReason>>(StringComparer.Ordinal);

        foreach (var dimension in input.Dimensions)
        {
            var fired = new List<EscalationReason>();

            if (byId.TryGetValue(dimension.Id, out var row)
                && row.Availability == EvidenceAvailability.NotApplicable)
            {
                // Never asked, so never escalated, and the trust table does not override this. A
                // dimension the message cannot support is one the strong model cannot answer either,
                // so asking it would spend a call to be told the same thing, and a NotApplicable row
                // that came back Unavailable would arm the policy engine's asked-and-unanswered gate
                // on a message where nothing was ever asked.
                continue;
            }

            if (options.Trust.AlwaysEscalates(dimension.Id))
            {
                fired.Add(EscalationReason.UntrustedDimension);
            }

            if (row is null)
            {
                // Asked and answered with nothing. Not a zero, and not silence either: the row set the
                // local arm returns is one row per dimension it was asked.
                fired.Add(EscalationReason.LocalUnavailable);
            }
            else
            {
                fired.AddRange(ConditionsFor(dimension.Id, row, options, priorLocalValues));
            }

            if (fired.Count == 0)
            {
                continue;
            }

            escalated.Add(dimension);
            reasons[dimension.Id] = fired;
        }

        return escalated.Count == 0
            ? CascadeDecision.KeepLocal
            : new CascadeDecision { Escalated = escalated, Reasons = reasons };
    }

    /// <summary>Every condition that fires on one answered row.</summary>
    private static IEnumerable<EscalationReason> ConditionsFor(
        string dimensionId,
        Evidence row,
        CascadeOptions options,
        IReadOnlyDictionary<string, double>? priorLocalValues)
    {
        switch (row.Availability)
        {
            case EvidenceAvailability.Unavailable:
                yield return EscalationReason.LocalUnavailable;
                yield break;

            case EvidenceAvailability.ReducedCoverage:
                yield return EscalationReason.LocalReducedCoverage;
                yield break;
        }

        // Available from here. A value is not optional on an available row: the port's own consumer
        // reads an available row with no value as unavailable rather than repairing it, and the same
        // reading is taken here.
        if (row.Value is not { } value)
        {
            yield return EscalationReason.LocalUnavailable;
            yield break;
        }

        if (CarriesReason(row))
        {
            yield return EscalationReason.PartialRead;
        }

        if (options.CarriesWeight(dimensionId))
        {
            if (options.Trust.IsIndecisive(dimensionId, value))
            {
                yield return EscalationReason.IndecisiveValue;
            }

            if (priorLocalValues is { } prior
                && prior.TryGetValue(dimensionId, out var priorValue)
                && Math.Abs(priorValue - value) > options.DisagreementTolerance)
            {
                yield return EscalationReason.RunDisagreement;
            }
        }
    }

    private static bool CarriesReason(Evidence row)
        => row.Attributes is { Count: > 0 } attributes
        && attributes.Any(attribute =>
            string.Equals(attribute.Name, AvailabilityReasonAttribute, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(attribute.Value));
}
