using StyloMail.Core;

namespace StyloMail.Policy.Tests;

/// <summary>
/// The composition table: a pack never relaxes the legacy decision.
/// </summary>
public sealed class PackPolicyComposerTests
{
    [Theory]
    [InlineData(MailAction.Allow, PolicyVerdict.Permit, MailAction.Allow)]
    [InlineData(MailAction.Allow, PolicyVerdict.Observe, MailAction.Allow)]
    [InlineData(MailAction.Allow, PolicyVerdict.Hold, MailAction.Hold)]
    [InlineData(MailAction.Allow, PolicyVerdict.Quarantine, MailAction.Quarantine)]
    [InlineData(MailAction.Allow, PolicyVerdict.Review, MailAction.Review)]
    [InlineData(MailAction.Hold, PolicyVerdict.Permit, MailAction.Hold)]
    [InlineData(MailAction.Hold, PolicyVerdict.Observe, MailAction.Hold)]
    [InlineData(MailAction.Hold, PolicyVerdict.Hold, MailAction.Hold)]
    [InlineData(MailAction.Hold, PolicyVerdict.Quarantine, MailAction.Quarantine)]
    [InlineData(MailAction.Hold, PolicyVerdict.Review, MailAction.Review)]
    [InlineData(MailAction.Quarantine, PolicyVerdict.Permit, MailAction.Quarantine)]
    [InlineData(MailAction.Quarantine, PolicyVerdict.Observe, MailAction.Quarantine)]
    [InlineData(MailAction.Quarantine, PolicyVerdict.Hold, MailAction.Quarantine)]
    [InlineData(MailAction.Quarantine, PolicyVerdict.Review, MailAction.Quarantine)]
    [InlineData(MailAction.Quarantine, PolicyVerdict.Quarantine, MailAction.Quarantine)]
    public void The_composed_action_is_the_more_protective_one(
        MailAction legacy,
        PolicyVerdict pack,
        MailAction expected)
    {
        Assert.Equal(expected, PackPolicyComposer.Compose(legacy, pack));
    }

    [Theory]
    [InlineData(MailAction.Defer, PolicyVerdict.Permit)]
    [InlineData(MailAction.Defer, PolicyVerdict.Review)]
    [InlineData(MailAction.Defer, PolicyVerdict.Quarantine)]
    [InlineData(MailAction.Reject, PolicyVerdict.Permit)]
    [InlineData(MailAction.Reject, PolicyVerdict.Review)]
    [InlineData(MailAction.Reject, PolicyVerdict.Quarantine)]
    public void Pre_acceptance_outcomes_are_never_replaced_by_a_pack(
        MailAction legacy,
        PolicyVerdict pack)
    {
        Assert.Equal(legacy, PackPolicyComposer.Compose(legacy, pack));
    }

    [Fact]
    public void No_pack_means_the_legacy_decision_stands()
    {
        Assert.Equal(MailAction.Hold, PackPolicyComposer.Compose(MailAction.Hold, null));
        Assert.Equal(MailAction.Allow, PackPolicyComposer.Compose(MailAction.Allow, null));
    }

    [Fact]
    public void Review_is_withheld_for_a_human_and_is_not_quarantine()
    {
        Assert.Equal(MailAction.Review, PackPolicyComposer.ToAction(PolicyVerdict.Review));
        Assert.NotEqual(MailAction.Quarantine, PackPolicyComposer.ToAction(PolicyVerdict.Review));
        Assert.NotEqual(MailAction.Hold, PackPolicyComposer.ToAction(PolicyVerdict.Review));
    }
}
