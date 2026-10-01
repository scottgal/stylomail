using StyloMail.Core;

namespace StyloMail.Host.Assessors;

/// <summary>
/// The assessor that stands in when the host cannot build a real one. It refuses every request.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the composition's answer to an unconfigured host, not a placeholder for a missing
/// implementation.</b> <see cref="Assessment.MailAssessor"/> is the assessor a configured host runs;
/// <c>HostServices.BuildAssessor</c> returns this type instead, and only, when the credential
/// resolution comes back <c>CredentialState.NotConfigured</c>. An earlier version of this comment
/// said nothing in the repository implemented <see cref="IMailAssessor"/> yet, which stopped being
/// true when the pipeline landed.
/// </para>
///
/// <para>
/// A permissive default was the alternative and it is the one option that is genuinely dangerous:
/// an unexamined message reported as "Allow" is worse than a visible outage, because everything
/// downstream treats an assessment as having happened.
/// </para>
///
/// <para>
/// Throwing rather than returning a verdict is what makes the refusal visible: the caller cannot
/// mistake an unassessed message for an assessed one, and the exception's message is a sentence an
/// operator can act on.
/// </para>
/// </remarks>
public sealed class UnavailableMailAssessor : IMailAssessor
{
    /// <remarks>
    /// <c>callerSuppliedRawMessage</c> is ignored: this type never parses anything, so there is no
    /// view for bytes to stand in for.
    /// </remarks>
    public ValueTask<MailAssessment> AssessAsync(
        MailAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte>? callerSuppliedRawMessage = null)
        => throw new AssessorUnavailableException(
            "No IMailAssessor is configured on this host, so no assessment can be produced.");
}

/// <summary>Raised when the host has no assessor configured.</summary>
public sealed class AssessorUnavailableException : Exception
{
    public AssessorUnavailableException(string message)
        : base(message)
    {
    }
}
