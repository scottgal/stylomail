using StyloMail.Core;

namespace StyloMail.Cascade;

/// <summary>Which dimensions a stronger model is asked, and why each one is asked.</summary>
/// <remarks>
/// <para>
/// <b>The decision is data, so nothing about it is implicit in a call site.</b> The rule that produces
/// it is deterministic code with no I/O, which is what makes it testable condition by condition and
/// what makes "escalate only when a stated condition holds" a property a test can hold it to.
/// </para>
/// <para>
/// <b>Per dimension, not per message.</b> A message whose twelfth dimension is undecided does not
/// justify re-asking the eleven the local model answered decisively, and the strong model's cost is
/// the request rather than the dimension. The escalated set is what the hosted arm is asked for, so an
/// escalation here is also a statement about how much of the question set left the local model.
/// </para>
/// </remarks>
public sealed record CascadeDecision
{
    /// <summary>Every dimension whose local answer is not taken, in the order it was asked.</summary>
    public required IReadOnlyList<SemanticDimension> Escalated { get; init; }

    /// <summary>The conditions that fired, per escalated dimension id. Never empty for an escalated id.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<EscalationReason>> Reasons { get; init; }

    /// <summary>True when at least one dimension is escalated, so the strong model is asked.</summary>
    public bool Escalates => Escalated.Count > 0;

    /// <summary>A decision to ask nobody, used when nothing needs the strong model.</summary>
    public static CascadeDecision KeepLocal { get; } = new()
    {
        Escalated = [],
        Reasons = new Dictionary<string, IReadOnlyList<EscalationReason>>(StringComparer.Ordinal),
    };

    /// <summary>The reason tokens for one dimension, comma separated, for a row's site.</summary>
    public string TokenFor(string dimensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimensionId);

        return Reasons.TryGetValue(dimensionId, out var reasons)
            ? string.Join(',', reasons.Select(reason => reason.Token()))
            : string.Empty;
    }
}
