using System.Security.Claims;
using StyloMail.Core;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Endpoints;
using StyloMail.Mime;

namespace StyloMail.Host.Hosting;

/// <summary>A message that passed ingress checks and produced an analysis view.</summary>
internal sealed record PreparedMessage(byte[] RawBytes, MailAnalysisInput Analysis);

/// <summary>
/// The checks and translations every message-bearing route performs before any real work happens.
/// </summary>
/// <remarks>
/// Shared between assessment and submission deliberately. If the two paths could disagree about
/// whose tenant a message belongs to, or about who may request shadow mode, the stricter of the
/// two would be the only one that mattered and it would be an accident which one that was.
/// </remarks>
internal static class MessageIngress
{
    /// <summary>
    /// Rejects a body that names a tenant other than the caller's own.
    /// </summary>
    /// <remarks>
    /// An empty or absent tenant is fine — the caller simply did not repeat itself. A
    /// <em>conflicting</em> tenant is refused outright rather than overridden. Silently ignoring it
    /// would teach an integrating system that its tenant field works, and the day the check moved
    /// or was refactored away, that field would quietly become an authority grant.
    /// </remarks>
    internal static IResult? RejectTenantMismatch(ClaimsPrincipal user, string? claimedTenant)
    {
        if (string.IsNullOrEmpty(claimedTenant))
        {
            return null;
        }

        return string.Equals(claimedTenant, user.TenantId(), StringComparison.Ordinal)
            ? null
            : EndpointResults.Forbidden(
                "tenant_mismatch",
                "The tenant in the request body does not match the authenticated principal.");
    }

    /// <summary>Shadow mode is an operator mode; it is not a thing a sending principal may select.</summary>
    internal static IResult? RejectUnpermittedShadow(ClaimsPrincipal user, bool shadowRequested)
        => !shadowRequested || user.HasPrivilege(HostPrivilege.Administer)
            ? null
            : EndpointResults.Forbidden(
                "shadow_mode_not_permitted",
                "Shadow mode is an operator observation mode and is not selectable by a sending principal.");

    /// <summary>
    /// Decodes, envelopes and analyses a message.
    /// </summary>
    /// <remarks>
    /// A message that cannot be parsed within limits produces an explicit disposition and no
    /// analysis view — never a fragment analysed as though it were a complete message.
    /// </remarks>
    internal static (PreparedMessage? Prepared, IResult? Error) Prepare(
        ClaimsPrincipal user,
        MessageSubmissionRequest request,
        IMimeMessageAnalyzer analyzer,
        TimeProvider clock,
        string payloadReference,
        string internalMessageId,
        string? correlationId = null)
    {
        byte[] rawBytes;
        try
        {
            rawBytes = Convert.FromBase64String(request.RawMime);
        }
        catch (FormatException)
        {
            return (null, EndpointResults.Invalid("invalid_base64", "rawMime must be Base64-encoded bytes."));
        }

        var tenantId = user.TenantId()!;

        var envelope = new MailEnvelope
        {
            InternalMessageId = internalMessageId,
            TenantId = tenantId,
            Direction = request.Direction,
            TrustedPrincipalId = user.PrincipalId() ?? "unknown",
            MailFrom = request.MailFrom,
            RcptTo = request.RcptTo,
            ReceivedAt = clock.GetUtcNow(),
            MimeDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rawBytes)),
            PayloadReference = payloadReference,
            UntrustedMessageIdHeader = null,
        };

        var authentication = BuildAuthenticationContext(request);

        var result = analyzer.Analyze(new MimeAnalysisRequest
        {
            Envelope = envelope,
            RawMessage = rawBytes,
            Authentication = authentication,
            ConversationContext = request.ConversationContext,
            TimeProvider = clock,
        });

        if (!result.IsAnalysable || result.Message is null)
        {
            var disposition = result.Disposition.ToString().ToLowerInvariant();
            return (null, EndpointResults.Unprocessable(
                "unprocessable_message",
                "The message could not be analysed as a complete message and was not assessed.",
                disposition,
                result.Rejection?.Reason,
                result.Rejection?.LimitName));
        }

        return (new PreparedMessage(rawBytes, result.Message), null);
    }

    /// <summary>
    /// Builds the authentication context from what the boundary reported.
    /// </summary>
    /// <remarks>
    /// A message cannot assert its own authentication: results are recorded as-is, and any result
    /// the caller did not mark as coming from a trusted verifier carries no authority. Provenance
    /// is marked incomplete when there is neither a trusted verifier result nor a connecting IP,
    /// so downstream evidence reports a gap rather than assuming the best.
    /// </remarks>
    internal static AuthenticationContext BuildAuthenticationContext(MessageSubmissionRequest request)
    {
        var results = request.AuthenticationResults
            .Select(r => new AuthenticationResult
            {
                Mechanism = r.Mechanism,
                Result = r.Result,
                VerifierId = r.VerifierId,
                FromTrustedVerifier = r.FromTrustedVerifier,
                Detail = r.Detail,
            })
            .ToList();

        var provenanceIncomplete =
            !results.Any(r => r.FromTrustedVerifier) && string.IsNullOrEmpty(request.ConnectingIp);

        return new AuthenticationContext
        {
            ConnectingIp = request.ConnectingIp,
            AuthenticatedAccount = request.AuthenticatedAccount,
            Results = results,
            ApprovedSenderIdentities = request.ApprovedSenderIdentities,
            ProvenanceIncomplete = provenanceIncomplete,
        };
    }

    internal static AssessmentContext BuildAssessmentContext(
        ClaimsPrincipal user,
        bool shadowMode,
        bool assessmentOnly,
        TimeProvider clock,
        string? clientIdempotencyKey = null)
        => new()
        {
            TenantId = user.TenantId()!,
            ShadowMode = shadowMode,
            AssessmentOnly = assessmentOnly,
            CorrelationId = $"cor_{Guid.NewGuid():N}",

            // The client's key is handed to the assessor rather than acted on here. Acceptance
            // happens inside the pipeline, so the key has to travel with it — the host minting or
            // managing its own key would guarantee two accepts under two different keys, which is
            // exactly how one message becomes two deliveries.
            ClientIdempotencyKey = clientIdempotencyKey,
            TimeProvider = clock,
        };
}
