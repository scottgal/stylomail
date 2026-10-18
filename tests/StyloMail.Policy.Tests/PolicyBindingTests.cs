namespace StyloMail.Policy.Tests;

/// <summary>
/// The binding model: a scoped, time-bound manual exception with a named author, a cap, an expiry
/// and an audit trail.
/// </summary>
public sealed class PolicyBindingTests
{
    [Fact]
    public void A_scoped_permit_binding_is_valid_and_resolves_to_its_action()
    {
        var binding = PolicyFixtures.Binding();

        var validation = PolicyBindingValidator.Validate(binding);

        Assert.True(validation.IsValid, Describe(validation));
        Assert.Equal(BindingAction.Permit, binding.Resolve().Action);
        Assert.Equal("entity-1", binding.ScopeValue);
    }

    [Fact]
    public void A_binding_cannot_lower_a_hard_floor()
    {
        var binding = PolicyFixtures.Binding(
            action: BindingAction.LowerTolerance,
            categoryId: PolicyCategories.ChildSafetyGrooming,
            targetLevel: ToleranceLevel.Observe,
            approvedBy: "reviewer-b");

        var validation = PolicyBindingValidator.Validate(binding);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyBindingValidator.HardFloorBelowStrict);
    }

    [Fact]
    public void Lowering_a_tolerance_requires_a_second_approver()
    {
        var binding = PolicyFixtures.Binding(
            action: BindingAction.LowerTolerance,
            categoryId: PolicyCategories.PaymentRedirection,
            targetLevel: ToleranceLevel.Strict);

        var validation = PolicyBindingValidator.Validate(binding);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyBindingValidator.SecondApproverMissing);
    }

    [Fact]
    public void The_second_approver_must_be_a_different_human()
    {
        var binding = PolicyFixtures.Binding(
            action: BindingAction.LowerTolerance,
            categoryId: PolicyCategories.PaymentRedirection,
            targetLevel: ToleranceLevel.Strict,
            approvedBy: "operator-a");

        var validation = PolicyBindingValidator.Validate(binding);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, violation => violation.Code == PolicyBindingValidator.SecondApproverMustDiffer);
    }

    [Fact]
    public void Raising_a_tolerance_needs_no_second_approver()
    {
        var binding = PolicyFixtures.Binding(
            action: BindingAction.RaiseTolerance,
            categoryId: PolicyCategories.ChildSafetyGrooming,
            targetLevel: ToleranceLevel.ZeroTolerance);

        var validation = PolicyBindingValidator.Validate(binding);

        Assert.True(validation.IsValid, Describe(validation));
    }

    [Fact]
    public void A_capture_binding_needs_a_second_approver_and_a_pack_that_allows_it()
    {
        var binding = PolicyFixtures.Binding(action: BindingAction.CaptureText);

        var withoutApprover = PolicyBindingValidator.Validate(binding, PolicyFixtures.Pack());
        Assert.Contains(withoutApprover.Violations, violation => violation.Code == PolicyBindingValidator.SecondApproverMissing);

        var withApprover = binding with { ApprovedBy = "reviewer-b" };
        var packDisallows = PolicyBindingValidator.Validate(withApprover, PolicyFixtures.Pack());
        Assert.Contains(packDisallows.Violations, violation => violation.Code == PolicyBindingValidator.CaptureExceedsPack);

        var allowed = PolicyBindingValidator.Validate(
            withApprover,
            PolicyFixtures.Pack(capture: PolicyFixtures.ExcerptCapture()));
        Assert.True(allowed.IsValid, Describe(allowed));
    }

    [Fact]
    public void A_tolerance_binding_must_name_a_known_category_and_a_level()
    {
        var binding = PolicyFixtures.Binding(
            action: BindingAction.LowerTolerance,
            categoryId: "a_category_that_does_not_exist",
            targetLevel: ToleranceLevel.Standard,
            approvedBy: "reviewer-b");

        var validation = PolicyBindingValidator.Validate(binding);

        Assert.Contains(validation.Violations, violation => violation.Code == PolicyBindingValidator.CategoryUnknown);
    }

    [Fact]
    public void A_binding_must_expire_after_it_starts()
    {
        var binding = PolicyFixtures.Binding(
            startsAt: PolicyFixtures.Now,
            expiresAt: PolicyFixtures.Now.AddDays(-1),
            reviewBy: PolicyFixtures.Now.AddDays(-2));

        var validation = PolicyBindingValidator.Validate(binding);

        Assert.Contains(validation.Violations, violation => violation.Code == PolicyBindingValidator.ExpiryNotAfterStart);
    }

    [Fact]
    public void The_review_date_must_fall_inside_the_binding_window()
    {
        var binding = PolicyFixtures.Binding(
            startsAt: PolicyFixtures.Now,
            expiresAt: PolicyFixtures.Now.AddDays(30),
            reviewBy: PolicyFixtures.Now.AddDays(31));

        var validation = PolicyBindingValidator.Validate(binding);

        Assert.Contains(validation.Violations, violation => violation.Code == PolicyBindingValidator.ReviewOutsideWindow);
    }

    [Fact]
    public void A_binding_without_a_reason_is_refused()
    {
        var binding = PolicyFixtures.Binding() with { Reason = " " };

        var validation = PolicyBindingValidator.Validate(binding);

        Assert.Contains(validation.Violations, violation => violation.Code == PolicyBindingValidator.ReasonMissing);
    }

    [Fact]
    public void A_binding_is_active_from_its_start_and_not_after_its_expiry()
    {
        var binding = PolicyFixtures.Binding(
            startsAt: PolicyFixtures.Now,
            expiresAt: PolicyFixtures.Now.AddDays(10),
            reviewBy: PolicyFixtures.Now.AddDays(5));

        Assert.False(binding.IsActiveAt(PolicyFixtures.Now.AddTicks(-1)));
        Assert.True(binding.IsActiveAt(PolicyFixtures.Now));
        Assert.True(binding.IsActiveAt(PolicyFixtures.Now.AddDays(10).AddTicks(-1)));
        Assert.False(binding.IsActiveAt(PolicyFixtures.Now.AddDays(10)));
    }

    [Fact]
    public void A_binding_change_is_an_audit_event_rather_than_a_quiet_edit()
    {
        var binding = PolicyFixtures.Binding();

        var audit = binding.Audit(BindingAuditEvent.Revoked, "operator-b", PolicyFixtures.Now, "Supplier contract ended.");

        Assert.Equal(binding.Id, audit.BindingId);
        Assert.Equal(binding.TenantId, audit.TenantId);
        Assert.Equal(BindingAuditEvent.Revoked, audit.Event);
        Assert.Equal("operator-b", audit.Actor);
        Assert.Equal("Supplier contract ended.", audit.Reason);
    }

    private static string Describe(BindingValidation validation) =>
        string.Join("; ", validation.Violations.Select(violation => $"{violation.Code}: {violation.Message}"));
}
