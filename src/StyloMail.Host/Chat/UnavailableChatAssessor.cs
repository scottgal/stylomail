using StyloMail.Core;

namespace StyloMail.Host.Chat;

/// <summary>
/// The chat assessor a deployment gets when it cannot pseudonymise.
/// </summary>
/// <remarks>
/// <para>
/// <b>It throws rather than returning an assessment, and that is deliberate.</b> An assessment
/// returned without evidence would enter the ledger looking like a decision, and a decision made
/// without the profile master key is not a weaker decision, it is not one. Throwing keeps the failure
/// at the boundary where it can be seen.
/// </para>
/// <para>
/// <b>What the caller does with the throw is the rest of the design.</b> The intake drain catches it
/// per event and leaves the event waiting, so nothing is silently consumed: the events accumulate
/// where an operator can see them, and they are assessed as soon as the key is configured. Accepting
/// events and quietly not assessing them is the lossy path the durable intake exists to prevent.
/// </para>
/// <para>
/// The mail path degrades the same way when its credentials are absent, for the same reason: a
/// deployment running without its secrets should look broken, not busy.
/// </para>
/// </remarks>
public sealed class UnavailableChatAssessor : IChatAssessor
{
    /// <summary>Names the missing setting, because an operator needs to know what to set.</summary>
    public const string Cause =
        "The chat assessment path needs the profile master key, because every chat profile key is a "
        + "pseudonym. Without it nothing can be assessed, and a deployment that ran without one would "
        + "key profiles on an author's platform identifier.";

    public ValueTask<MailAssessment> AssessAsync(
        ChatAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Cause);
}
