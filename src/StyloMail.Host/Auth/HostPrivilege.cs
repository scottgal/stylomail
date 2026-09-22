namespace StyloMail.Host.Auth;

/// <summary>
/// The distinct privileges the operator surface recognises.
/// </summary>
/// <remarks>
/// These are deliberately separate rather than one "authenticated" flag. The whole point is that
/// the party who sends mail, the party who reads the decision ledger, the party who supplies
/// feedback and the party who releases quarantine are not assumed to be the same principal.
/// A sender holding <see cref="Send"/> cannot release their own quarantine, that is the case
/// this separation exists to prevent.
/// </remarks>
[Flags]
public enum HostPrivilege
{
    None = 0,

    /// <summary>May submit messages for assessment.</summary>
    Assess = 1 << 0,

    /// <summary>May submit messages for durable delivery.</summary>
    Send = 1 << 1,

    /// <summary>May read the decision ledger and release quarantine. Review is always a separate grant from sending.</summary>
    Review = 1 << 2,

    /// <summary>May attach trusted labels and corrections to a decision.</summary>
    Feedback = 1 << 3,

    /// <summary>May operate controls such as pausing a sending principal.</summary>
    Administer = 1 << 4,
}
