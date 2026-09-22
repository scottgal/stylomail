namespace StyloMail.Core;

/// <summary>Result of one authentication check performed at a trusted boundary.</summary>
public sealed record AuthenticationResult
{
    /// <summary>The mechanism, e.g. <c>spf</c>, <c>dkim</c>, <c>dmarc</c>, <c>arc</c>.</summary>
    public required string Mechanism { get; init; }

    /// <summary>Outcome as reported, e.g. <c>pass</c>, <c>fail</c>, <c>none</c>, <c>temperror</c>.</summary>
    public required string Result { get; init; }

    /// <summary>The verifier that produced this result. Required for the result to be trusted at all.</summary>
    public string? VerifierId { get; init; }

    /// <summary>True only when the verifier is a configured trusted boundary verifier.</summary>
    public required bool FromTrustedVerifier { get; init; }

    /// <summary>
    /// Mechanism-specific detail, using the convention below. Only parsed for results whose
    /// <see cref="FromTrustedVerifier"/> is true.
    /// </summary>
    /// <remarks>
    /// <b>Agreed convention</b> — colon-separated <c>key=value</c> pairs, keys lowercase:
    /// <list type="bullet">
    /// <item><c>dkim</c> — <c>d=</c> the signing domain, <c>s=</c> the selector. Both come from the
    /// verifier's own analysis, never from the message's own DKIM-Signature header.</item>
    /// <item><c>spf</c> — <c>domain=</c> the checked domain, <c>ip=</c> the checked client address.</item>
    /// <item><c>dmarc</c> — <c>domain=</c> the policy domain, <c>policy=</c> the published policy.</item>
    /// </list>
    /// <para>
    /// The <c>dkim</c> <c>d=</c> value is what makes <b>alignment</b> computable: a passing DKIM
    /// signature whose signing domain does not align with the visible From domain is a real
    /// outbound-compromise cue, because a valid signature then proves only that the message is
    /// authentic to some <em>other</em> domain. Alignment is deliberately not computed here —
    /// <c>AuthenticationResult</c> records what a trusted verifier observed, and comparison against
    /// the From domain is a separate judgement that belongs with the policy layer.
    /// </para>
    /// <para>
    /// Unknown keys are ignored rather than rejected: a verifier may report more than this
    /// convention describes, and discarding a result because its detail string grew would be worse
    /// than ignoring the extra.
    /// </para>
    /// </remarks>
    public string? Detail { get; init; }
}

/// <summary>
/// Provenance and authentication facts for a message.
/// </summary>
/// <remarks>
/// <b>A passing SPF or DKIM result is not a benign verdict.</b> A compromised authorised
/// account authenticates correctly while sending abuse — that is precisely the outbound
/// compromise case this system exists to catch. Authentication answers "authorised to send
/// as this domain", never "safe to deliver".
///
/// <para>
/// Authentication results are only accepted from configured trusted boundary verifiers.
/// A result whose <see cref="AuthenticationResult.FromTrustedVerifier"/> is false is
/// recorded but carries no authority; a message cannot assert its own authentication.
/// </para>
/// </remarks>
public sealed record AuthenticationContext
{
    /// <summary>Original connecting IP, when supplied by a trusted boundary. Needed to evaluate SPF meaningfully.</summary>
    public string? ConnectingIp { get; init; }

    /// <summary>The authenticated account, for outbound submission.</summary>
    public string? AuthenticatedAccount { get; init; }

    public required IReadOnlyList<AuthenticationResult> Results { get; init; }

    /// <summary>Sender identities this authenticated account or connector is approved to use.</summary>
    public required IReadOnlyList<string> ApprovedSenderIdentities { get; init; }

    /// <summary>
    /// True when provenance could not be established — no trusted verifier, no connecting IP,
    /// no session telemetry. Recorded explicitly so downstream evidence can report incomplete
    /// coverage. Never treated as though provenance were clean.
    /// </summary>
    public required bool ProvenanceIncomplete { get; init; }
}
