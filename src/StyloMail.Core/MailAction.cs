namespace StyloMail.Core;

/// <summary>
/// The intervention that policy authorises for a message or recipient.
/// </summary>
/// <remarks>
/// Probabilistic components produce evidence; only versioned deterministic policy
/// selects one of these. No semantic score may select an action directly.
///
/// <para>
/// Shadow is deliberately <em>not</em> a member here, shadow is a mode that records
/// the action it would have taken while still forwarding. Modelling it as an action
/// would conflate "what we decided" with "whether we were allowed to act".
/// </para>
/// </remarks>
public enum MailAction
{
    /// <summary>Eligible for the configured next hop, subject to delivery quotas.</summary>
    Allow = 0,

    /// <summary>Durably retained until a bounded re-evaluation deadline. An observation window, not a soft reject.</summary>
    Hold = 1,

    /// <summary>Durably retained for authenticated review; not delivered.</summary>
    Quarantine = 2,

    /// <summary>Decline responsibility <em>temporarily</em>, before acceptance. The caller may retry.</summary>
    Defer = 3,

    /// <summary>Decline responsibility <em>permanently</em>, before acceptance, under explicit policy.</summary>
    Reject = 4,
}
