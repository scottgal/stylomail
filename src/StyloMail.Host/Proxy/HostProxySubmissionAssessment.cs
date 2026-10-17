using StyloMail.AccessProxy.Submission;

namespace StyloMail.Host.Proxy;

/// <summary>
/// A deterministic submission finding. Only this seam may refuse outbound mail.
/// </summary>
/// <remarks>
/// Semantic absence is not a finding. A provider that is unreachable, slow or unconfigured must
/// never block a submission: spec §9.8 makes availability a property of the evidence, not a veto.
/// </remarks>
public sealed record ProxySubmissionPolicyResult
{
    public required bool Refuse { get; init; }

    public string? Reason { get; init; }

    public static ProxySubmissionPolicyResult Allow { get; } = new() { Refuse = false };
}

/// <summary>Host-owned deterministic checks over a staged submission.</summary>
public interface IProxySubmissionPolicy
{
    ValueTask<ProxySubmissionPolicyResult> EvaluateAsync(
        IStagedProxySubmission submission,
        CancellationToken cancellationToken);
}

/// <summary>The default policy: no deterministic finding, so the submission relays.</summary>
/// <remarks>
/// This is deliberately an allow, not an absence of policy. The SMTP session's only refusal is a
/// deterministic policy result, and a deployment with no local rule has found nothing to refuse.
/// </remarks>
public sealed class AllowAllProxySubmissionPolicy : IProxySubmissionPolicy
{
    public ValueTask<ProxySubmissionPolicyResult> EvaluateAsync(
        IStagedProxySubmission submission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ProxySubmissionPolicyResult.Allow);
    }
}

/// <summary>
/// The default pre-delivery gate: deterministic findings may refuse, semantic unavailability relays.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ProxySubmissionDisposition.SemanticUnavailable"/> is an allow with its evidence
/// stated.</b> The SMTP session relays every disposition except a deterministic refusal, and this
/// gate returns that disposition rather than <see cref="ProxySubmissionDisposition.Allow"/> when no
/// semantic provider answered. Returning a bare Allow would claim a measurement that never
/// happened, which is the same false-confidence defect as a not-produced point drawn as zero.
/// </para>
/// <para>
/// The staged bytes are owned by the caller (<c>SmtpAccessProxySession</c>) and disposed on every
/// terminal path. This type never retains or copies them.
/// </para>
/// </remarks>
public sealed class HostProxySubmissionAssessment : IProxySubmissionAssessment
{
    private readonly IProxySubmissionPolicy _policy;

    public HostProxySubmissionAssessment(IProxySubmissionPolicy policy)
        => _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public async ValueTask<ProxySubmissionAssessmentResult> AssessAsync(
        IStagedProxySubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var finding = await _policy.EvaluateAsync(submission, cancellationToken).ConfigureAwait(false);
        if (finding.Refuse)
        {
            return new ProxySubmissionAssessmentResult
            {
                Disposition = ProxySubmissionDisposition.Refuse,
                IsDeterministic = true,
                SubmissionId = NewId("sub"),
                DecisionId = NewId("dec"),
            };
        }

        return new ProxySubmissionAssessmentResult
        {
            Disposition = ProxySubmissionDisposition.SemanticUnavailable,
            // No semantic provider is composed for proxied submission in this deployment. The
            // disposition states that; it is not a refusal and must not become one.
            IsDeterministic = false,
            SubmissionId = NewId("sub"),
        };
    }

    private static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");
}
