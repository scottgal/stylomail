namespace StyloMail.Desktop.Api.Contracts;

/// <summary>
/// <c>GET /health/ready</c>: can this Host durably accept mail right now?
/// </summary>
/// <remarks>
/// <b>Not ready is not an error.</b> The Host answers 503 with this body, and
/// that is a successful answer to the question asked. Modelling it as a thrown
/// exception would make the one case the console most needs to describe
/// clearly, a Host that is up and refusing mail, arrive as a generic failure
/// with the failed checks discarded.
///
/// <para>
/// The route is unauthenticated by necessity, and the compensation is that
/// nothing in this body describes a message, an identity or a tenant. The
/// check names are capability names, and the console renders them as such.
/// </para>
/// </remarks>
public sealed record ReadinessResponse
{
    public required string Status { get; init; }

    /// <summary>
    /// Which capabilities failed, when the Host is not ready. Null when it is.
    /// </summary>
    public IReadOnlyList<string>? FailedChecks { get; init; }

    /// <summary>
    /// Whether the Host can durably accept mail.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="Status"/> rather than from a separate member,
    /// because the Host sends no boolean: a second source of truth is a second
    /// thing that can disagree with the first.
    /// </remarks>
    public bool Ready => string.Equals(Status, "ready", StringComparison.OrdinalIgnoreCase);
}
