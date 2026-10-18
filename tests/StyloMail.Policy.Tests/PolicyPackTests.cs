using StyloMail.Core;

namespace StyloMail.Policy.Tests;

/// <summary>
/// The pack contract: what a pack must declare, and the shapes validation must refuse.
/// </summary>
/// <remarks>
/// The hard-floor cases are the ones that matter most. Each is asserted on the violation <em>code</em>
/// rather than merely on <c>IsValid</c>, so a pack refused for an unrelated typo cannot be read as
/// evidence that the floor was enforced.
/// </remarks>
public sealed class PolicyPackTests
{
    [Fact]
    public void A_pack_that_declares_every_required_field_is_valid()
    {
        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack());

        Assert.True(validation.IsValid, Describe(validation));
    }

    [Theory]
    [InlineData(PolicyCategories.ChildSafetyGrooming)]
    [InlineData(PolicyCategories.CredibleThreatsOfViolence)]
    [InlineData(PolicyCategories.IllegalContent)]
    public void A_hard_floor_cannot_be_switched_off(string category)
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[category] = ToleranceLevel.Off;

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(levels: levels));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.HardFloorBelowStrict);
    }

    [Theory]
    [InlineData(PolicyCategories.ChildSafetyGrooming)]
    [InlineData(PolicyCategories.CredibleThreatsOfViolence)]
    [InlineData(PolicyCategories.IllegalContent)]
    public void A_hard_floor_cannot_be_observe_only(string category)
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[category] = ToleranceLevel.Observe;

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(levels: levels));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.HardFloorBelowStrict);
    }

    [Fact]
    public void A_hard_floor_may_be_more_protective_than_strict()
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[PolicyCategories.ChildSafetyGrooming] = ToleranceLevel.ZeroTolerance;

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(levels: levels));

        Assert.True(validation.IsValid, Describe(validation));
    }

    [Fact]
    public void A_pack_that_omits_a_hard_floor_is_refused()
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels.Remove(PolicyCategories.IllegalContent);

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(levels: levels));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.HardFloorUndeclared);
    }

    [Fact]
    public void An_unknown_category_is_refused()
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels["adult_content_but_actually_upselling"] = ToleranceLevel.Strict;

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(levels: levels));

        Assert.False(validation.IsValid);
        var violation = Assert.Single(validation.Violations, violation => violation.Code == PolicyPackValidator.CategoryUnknown);
        Assert.Contains("adult_content_but_actually_upselling", violation.Message);
    }

    [Fact]
    public void A_mixed_audience_runs_grooming_at_strict_and_does_not_guess()
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[PolicyCategories.ChildSafetyGrooming] = ToleranceLevel.Standard;

        var validation = PolicyPackValidator.Validate(
            PolicyFixtures.Pack(levels: levels, audience: PolicyAudience.Mixed));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.MixedAudienceGroomingNotStrict);
    }

    [Theory]
    [InlineData(PolicyAudience.Mixed)]
    [InlineData(PolicyAudience.Unknown)]
    public void An_unknown_audience_is_treated_as_conservative(PolicyAudience audience)
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[PolicyCategories.ChildSafetyGrooming] = ToleranceLevel.Standard;

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(levels: levels, audience: audience));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.MixedAudienceGroomingNotStrict);
    }

    [Fact]
    public void Lowering_a_category_without_a_second_approver_is_refused()
    {
        var previous = PolicyFixtures.Pack();
        var lowered = PolicyFixtures.DefaultLevels();
        lowered[PolicyCategories.PaymentRedirection] = ToleranceLevel.Observe;

        var validation = PolicyPackValidator.Validate(
            PolicyFixtures.Pack(levels: lowered, version: "1.1.0"),
            previous);

        Assert.False(validation.IsValid);
        var violation = Assert.Single(
            validation.Violations,
            violation => violation.Code == PolicyPackValidator.LevelLoweredWithoutSecondApprover);
        Assert.Contains(PolicyCategories.PaymentRedirection, violation.Message);
    }

    [Fact]
    public void Lowering_a_category_with_a_second_approver_is_accepted()
    {
        var previous = PolicyFixtures.Pack();
        var lowered = PolicyFixtures.DefaultLevels();
        lowered[PolicyCategories.PaymentRedirection] = ToleranceLevel.Observe;

        var validation = PolicyPackValidator.Validate(
            PolicyFixtures.Pack(levels: lowered, version: "1.1.0", secondApprover: "reviewer-b"),
            previous);

        Assert.True(validation.IsValid, Describe(validation));
    }

    [Fact]
    public void Widening_capture_without_a_second_approver_is_refused()
    {
        var previous = PolicyFixtures.Pack();

        var validation = PolicyPackValidator.Validate(
            PolicyFixtures.Pack(capture: PolicyFixtures.ExcerptCapture(), version: "1.1.0"),
            previous);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.CaptureModeRaisedWithoutSecondApprover);
    }

    [Fact]
    public void Full_body_capture_requires_a_second_approver()
    {
        var capture = PolicyFixtures.ExcerptCapture() with { Mode = TextCaptureMode.Full };

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(capture: capture));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.CaptureFullRequiresSecondApprover);
    }

    [Fact]
    public void The_second_approver_must_be_a_different_human()
    {
        var capture = PolicyFixtures.ExcerptCapture();

        var validation = PolicyPackValidator.Validate(
            PolicyFixtures.Pack(capture: capture, secondApprover: "approver-a", approver: "approver-a"));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.SecondApproverMustDiffer);
    }

    [Fact]
    public void A_same_person_second_approver_is_refused_even_when_capture_is_disabled()
    {
        // The check lives outside the capture rule. It used to sit after the capture mode's early
        // return, so a lowering with capture disabled accepted the same principal twice.
        var validation = PolicyPackValidator.Validate(
            PolicyFixtures.Pack(
                capture: TextCaptureRules.Disabled,
                secondApprover: "approver-a",
                approver: "approver-a"));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.SecondApproverMustDiffer);
    }

    [Fact]
    public void Capturing_content_requires_a_retention_clock()
    {
        var capture = PolicyFixtures.ExcerptCapture() with { RetentionDays = 0 };

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(capture: capture));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.CaptureRetentionMissing);
    }

    [Fact]
    public void Capturing_content_requires_a_statement_and_a_bound()
    {
        var capture = PolicyFixtures.ExcerptCapture() with { Statement = " ", MaxCharacters = 0 };

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(capture: capture));

        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.CaptureStatementMissing);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.CaptureBoundMissing);
    }

    [Fact]
    public void A_ceiling_that_cannot_escalate_is_refused()
    {
        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(ceiling: PolicyVerdict.Permit));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.ActionCeilingInvalid);
    }

    [Fact]
    public void A_scoring_pack_cannot_cap_itself_below_review()
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[PolicyCategories.PaymentRedirection] = ToleranceLevel.Observe;

        var validation = PolicyPackValidator.Validate(
            PolicyFixtures.Pack(levels: levels, ceiling: PolicyVerdict.Observe));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.ActionCeilingBelowReview);
    }

    [Fact]
    public void Validation_reports_every_failure_rather_than_stopping_at_the_first()
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[PolicyCategories.ChildSafetyGrooming] = ToleranceLevel.Off;
        levels["not_a_category"] = ToleranceLevel.Strict;

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(levels: levels));

        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.HardFloorBelowStrict);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.CategoryUnknown);
    }

    [Fact]
    public void A_pack_version_is_never_edited_in_place()
    {
        var previous = PolicyFixtures.Pack();

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(), previous);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.VersionNotAdvanced);
    }

    [Fact]
    public void A_pack_is_only_compared_with_the_same_pack_in_the_same_tenant()
    {
        var previous = PolicyFixtures.Pack(tenant: "tenant-b");

        var validation = PolicyPackValidator.Validate(PolicyFixtures.Pack(version: "1.1.0"), previous);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.PreviousPackMismatch);
    }

    [Fact]
    public void A_draft_may_be_validated_before_an_approver_exists()
    {
        var draft = PolicyFixtures.Pack() with { Approver = string.Empty };

        var full = PolicyPackValidator.Validate(draft);
        var asDraft = PolicyPackValidator.Validate(draft, previous: null, validateApprover: false);

        // The approval step must still refuse it: the draft exemption is not a weaker pack.
        Assert.False(full.IsValid);
        Assert.Contains(full.Violations, violation => violation.Code == PolicyPackValidator.ApproverMissing);
        Assert.True(asDraft.IsValid, Describe(asDraft));
    }

    [Fact]
    public void A_pack_without_an_effective_date_is_refused()
    {
        var validation = PolicyPackValidator.Validate(
            PolicyFixtures.Pack(effectiveFrom: DateTimeOffset.MinValue));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyPackValidator.EffectiveDateMissing);
    }

    private static string Describe(PolicyPackValidation validation) =>
        string.Join("; ", validation.Violations.Select(violation => $"{violation.Code}: {violation.Message}"));
}
