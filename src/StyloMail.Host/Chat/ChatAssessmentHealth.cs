namespace StyloMail.Host.Chat;

/// <summary>
/// Whether this deployment can assess chat at all, and whether that is costing it anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists to make an otherwise silent state legible.</b> A deployment with no profile master
/// key still starts, still accepts chat events, and still stores them, and the drain leaves every one
/// of them waiting because the assessor throws. Nothing is lost and nothing is wrong, but from the
/// outside that looks exactly like a drain that is merely busy, and an operator would have to infer
/// the cause from a log line.
/// </para>
/// <para>
/// The state is set once when the assessor is built, because it is a property of how the process was
/// configured rather than something that changes while it runs.
/// </para>
/// </remarks>
public sealed class ChatAssessmentHealth
{
    /// <summary>True when chat events are being accepted but cannot be assessed.</summary>
    public bool IsUnavailable { get; set; }
}
