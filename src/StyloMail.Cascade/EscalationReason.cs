namespace StyloMail.Cascade;

/// <summary>Why a dimension's local answer was not taken.</summary>
/// <remarks>
/// <para>
/// <b>This is the "why it was asked" half of the cascade's site.</b> A row taken from the strong model
/// carries the token of whichever condition fired, so a reader of a decision can tell a strong-model
/// answer that was needed from one that was paid for by a threshold nobody has checked.
/// </para>
/// <para>
/// The tokens are short, stable and lowercase on purpose: they are read by a console, by a
/// measurement harness, and by a person comparing two runs, and all three want to filter on them.
/// </para>
/// </remarks>
public enum EscalationReason
{
    /// <summary>
    /// The local model answered a dimension it was asked with <c>Unavailable</c>, or answered nothing
    /// at all for it. UNKNOWN is a reason to escalate, never an answer and never a zero.
    /// </summary>
    LocalUnavailable = 0,

    /// <summary>The local model produced a value over reduced input coverage.</summary>
    LocalReducedCoverage = 1,

    /// <summary>
    /// The local model answered over text that was cut before the request was sent. The row is
    /// <c>Available</c> and carries a reason, so availability alone does not distinguish it.
    /// </summary>
    PartialRead = 2,

    /// <summary>
    /// The local value sits inside the decisiveness band, so the model is reporting a genuinely
    /// balanced yes/no rather than a finding.
    /// </summary>
    IndecisiveValue = 3,

    /// <summary>
    /// A prior local answer for the same input, supplied by the deployment, differs from this one by
    /// more than the tolerance: the model is not a function of the message alone.
    /// </summary>
    RunDisagreement = 4,

    /// <summary>
    /// The dimension is on the trust table's always-escalate list, which is populated from measured
    /// per-dimension agreement rather than from a guess.
    /// </summary>
    UntrustedDimension = 5,
}

/// <summary>The wire and log token for each reason.</summary>
public static class EscalationReasonTokens
{
    public const string LocalUnavailable = "local_unavailable";

    public const string LocalReducedCoverage = "local_reduced_coverage";

    public const string PartialRead = "partial_read";

    public const string IndecisiveValue = "indecisive_value";

    public const string RunDisagreement = "run_disagreement";

    public const string UntrustedDimension = "untrusted_dimension";

    /// <summary>Suffix appended when the escalation was attempted and produced nothing usable.</summary>
    public const string HostedUnavailable = "hosted:unavailable";

    public static string Token(this EscalationReason reason) => reason switch
    {
        EscalationReason.LocalUnavailable => LocalUnavailable,
        EscalationReason.LocalReducedCoverage => LocalReducedCoverage,
        EscalationReason.PartialRead => PartialRead,
        EscalationReason.IndecisiveValue => IndecisiveValue,
        EscalationReason.RunDisagreement => RunDisagreement,
        EscalationReason.UntrustedDimension => UntrustedDimension,
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason),
            reason,
            "a new escalation reason needs a token here, because a row that names no reason is a "
            + "strong-model call nobody can account for"),
    };
}
