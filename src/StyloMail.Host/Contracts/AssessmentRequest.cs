using StyloMail.Core;

namespace StyloMail.Host.Contracts;

/// <summary>
/// Wire shape for <c>POST /v1/assessments</c> and <c>POST /v1/submissions</c>.
/// </summary>
/// <remarks>
/// <see cref="TenantId"/> is accepted but never trusted: it is honoured only when it agrees with
/// the authenticated principal's tenant, and is otherwise a 403. It exists so a client that
/// restates its own tenant is not forced to omit it, not so that a client can choose one.
/// </remarks>
public sealed record MessageSubmissionRequest
{
    public string? TenantId { get; init; }

    public MailDirection Direction { get; init; } = MailDirection.Inbound;

    /// <summary>SMTP <c>MAIL FROM</c>.</summary>
    public string MailFrom { get; init; } = string.Empty;

    /// <summary>SMTP <c>RCPT TO</c>. Each recipient is recorded and progressed separately.</summary>
    public IReadOnlyList<string> RcptTo { get; init; } = [];

    /// <summary>The original message bytes, Base64. Never modified — this is the transport artefact.</summary>
    public string RawMime { get; init; } = string.Empty;

    public string? AuthenticatedAccount { get; init; }

    /// <summary>Original connecting IP from a trusted boundary. Required for SPF to mean anything.</summary>
    public string? ConnectingIp { get; init; }

    public IReadOnlyList<string> ApprovedSenderIdentities { get; init; } = [];

    public IReadOnlyList<AuthenticationResultRequest> AuthenticationResults { get; init; } = [];

    /// <summary>
    /// Requested shadow mode. Honoured only for a principal holding <c>Administer</c>; a request
    /// for shadow from anyone else is refused rather than quietly ignored, because silently
    /// dropping it would tell the caller mail was forwarded when it was not.
    /// </summary>
    public bool ShadowMode { get; init; }

    public IReadOnlyList<string>? ConversationContext { get; init; }
}

/// <summary>An authentication result as reported by a boundary verifier.</summary>
public sealed record AuthenticationResultRequest
{
    public string Mechanism { get; init; } = string.Empty;

    public string Result { get; init; } = string.Empty;

    public string? VerifierId { get; init; }

    /// <summary>Whether the named verifier is a configured trusted boundary. Defaults to false —
    /// a message may not authenticate itself.</summary>
    public bool FromTrustedVerifier { get; init; }

    public string? Detail { get; init; }
}
