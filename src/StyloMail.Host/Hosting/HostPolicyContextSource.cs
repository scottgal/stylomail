using StyloMail.Assessment;
using StyloMail.Core;

namespace StyloMail.Host.Hosting;

/// <summary>
/// Supplies the policy context an assessment reads, from host state rather than from nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The emergency stop is the reason this type exists.</b> Before it, the only implementation was
/// the static one that supplies nothing, so <c>EmergencyKillSwitchEngaged</c> was false on every
/// assessment in every deployment and the control the specification lists second in policy
/// precedence could not be engaged at all. A spec that names a control reads as a system that has
/// one, and this host did not.
/// </para>
/// <para>
/// <b>Everything else is still the conservative default, deliberately.</b> No quota exhaustion, no
/// verified rule violations, no allowlist, no recipient preference, and no traffic class, which stays
/// absent rather than assumed because an unnamed class would have fan-out judged against an
/// expectation belonging to nobody. Each of those is a fact some other component would have to
/// supply, and inventing one here to look complete is how a policy engine comes to decide on a value
/// nothing measured.
/// </para>
/// </remarks>
public sealed class HostPolicyContextSource : IAssessmentPolicyContextSource
{
    private readonly IEmergencyKillSwitch _killSwitch;

    public HostPolicyContextSource(IEmergencyKillSwitch killSwitch)
    {
        ArgumentNullException.ThrowIfNull(killSwitch);
        _killSwitch = killSwitch;
    }

    /// <summary>
    /// A context whose only supplied fact is the emergency stop, read at the moment it is asked.
    /// </summary>
    /// <remarks>
    /// The read is per assessment rather than cached, because a stop that takes effect at the next
    /// restart is not an emergency stop. It is one indexed lookup against a table that holds one row
    /// per transition, which is affordable against an assessment.
    /// </remarks>
    public ValueTask<PolicyContextInput> GetAsync(
        MailAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PolicyContextInput
        {
            EmergencyKillSwitchEngaged = _killSwitch.IsEngaged,
        });
}
