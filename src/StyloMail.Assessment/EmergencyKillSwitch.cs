namespace StyloMail.Assessment;

/// <summary>
/// Whether an operator has engaged the emergency stop.
/// </summary>
/// <remarks>
/// <para>
/// <b>A system-wide control, read by every path that can act.</b> A channel-shaped exemption would
/// make it something other than a system-wide control: an operator who pulls the stop believes the
/// system has stopped, and one that quietly does not reach a surface is the claim this project
/// keeps rooting out. The mail path and the chat path both read this.
/// </para>
/// <para>
/// <b>It is a port of its own rather than a field on
/// <see cref="IAssessmentPolicyContextSource"/> because that port is typed on a mail input</b>, and
/// the switch does not depend on the message at all. A channel with a different input would
/// otherwise have to reach the switch through a signature about mail, which is how a control ends
/// up with a channel that cannot read it.
/// </para>
/// <para>
/// <b>Read it per assessment rather than caching it.</b> The whole value of an emergency stop is
/// that engaging it stops the next message rather than the next restart, so a cached answer is a
/// stop that has not stopped yet.
/// </para>
/// </remarks>
public interface IEmergencyKillSwitch
{
    /// <summary>True while an operator has engaged the stop.</summary>
    bool IsEngaged { get; }
}

/// <summary>
/// A switch nobody has engaged.
/// </summary>
/// <remarks>
/// The default for a deployment that has not wired operator state in, and for tests. It is a
/// deliberate no-op rather than a null: every reader asks the same question and gets an answer,
/// which is what keeps the reading sites free of null checks that a later wiring could forget.
/// </remarks>
public sealed class NeverEngagedKillSwitch : IEmergencyKillSwitch
{
    public static NeverEngagedKillSwitch Instance { get; } = new();

    public bool IsEngaged => false;
}
