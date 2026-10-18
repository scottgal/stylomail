using StyloMail.Core;

namespace StyloMail.Policy;

/// <summary>
/// Composes the legacy decision with the pack's verdict by protectiveness.
/// </summary>
/// <remarks>
/// <para>
/// <b>A pack never relaxes the legacy decision.</b> The result is the more protective of the two,
/// and a pack <see cref="PolicyVerdict.Permit"/> cannot turn a legacy Hold, Quarantine, Defer or
/// Reject into an Allow. The table is deliberately small and total.
/// </para>
/// <para>
/// <b>Review beats Hold and loses to Quarantine.</b> Both Review and Hold withhold delivery, but
/// Review additionally guarantees that a person looks and carries a stored deadline. Quarantine
/// contains the message rather than waiting for a person, so it is more protective than either.
/// Defer and Reject are pre-acceptance outcomes and are never replaced by a pack.
/// </para>
/// </remarks>
public static class PackPolicyComposer
{
    /// <summary>The action the pack's verdict would select, before composition.</summary>
    public static MailAction ToAction(PolicyVerdict verdict) => verdict switch
    {
        PolicyVerdict.Permit or PolicyVerdict.Observe => MailAction.Allow,
        PolicyVerdict.Review => MailAction.Review,
        PolicyVerdict.Hold => MailAction.Hold,
        PolicyVerdict.Quarantine => MailAction.Quarantine,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown policy verdict."),
    };

    /// <summary>
    /// The composed action. <paramref name="packVerdict"/> null means no pack was active, and the
    /// legacy decision stands unchanged.
    /// </summary>
    public static MailAction Compose(MailAction legacy, PolicyVerdict? packVerdict)
    {
        if (packVerdict is null)
        {
            return legacy;
        }

        // Pre-acceptance outcomes are never replaced: a pack cannot accept a message the pipeline
        // declined, and it cannot convert a reject into a hold.
        if (legacy is MailAction.Defer or MailAction.Reject)
        {
            return legacy;
        }

        var packAction = ToAction(packVerdict.Value);
        return ProtectionRank(packAction) > ProtectionRank(legacy) ? packAction : legacy;
    }

    /// <summary>
    /// Protection order for the actions a pack may choose: Quarantine, then Review, then Hold, then
    /// Allow. Defer and Reject are outside this comparison and are handled before it.
    /// </summary>
    private static int ProtectionRank(MailAction action) => action switch
    {
        MailAction.Allow => 0,
        MailAction.Hold => 1,
        MailAction.Review => 2,
        MailAction.Quarantine => 3,
        MailAction.Defer => 4,
        MailAction.Reject => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown mail action."),
    };
}
