using StyloMail.Core;

namespace StyloMail.Host.Assessors;

/// <summary>
/// The assessor that stands in when none is configured. It refuses every request.
/// </summary>
/// <remarks>
/// Nothing in the repository implements <see cref="IMailAssessor"/> yet, the composition of
/// MIME evidence, semantic classification, profile comparison and policy is a separate piece of
/// work. Until it exists the host must be able to run, be tested, and report honestly that it
/// cannot assess.
///
/// <para>
/// A permissive default was the alternative and it is the one option that is genuinely dangerous:
/// an unexamined message reported as "Allow" is worse than a visible outage, because everything
/// downstream treats an assessment as having happened.
/// </para>
/// </remarks>
public sealed class UnavailableMailAssessor : IMailAssessor
{
    public ValueTask<MailAssessment> AssessAsync(
        MailAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken)
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
