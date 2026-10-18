using StyloMail.Core;

namespace StyloMail.Policy.Tests;

/// <summary>
/// Effective policy resolution: property floors, binding precedence, missing property keys and the
/// replay snapshot.
/// </summary>
public sealed class EffectivePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_minor_raises_the_protective_floors()
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[PolicyCategories.AdultContent] = ToleranceLevel.Off;
        levels[PolicyCategories.CoercionAndExtortion] = ToleranceLevel.Standard;
        levels[PolicyCategories.Harassment] = ToleranceLevel.Standard;

        var effective = Resolve(
            PolicyFixtures.Pack(levels: levels),
            facts: [Fact(PolicyPropertyKeys.AgeBand, "13_15", "Declared")]);

        Assert.Equal(ToleranceLevel.Strict, effective.Levels[PolicyCategories.AdultContent]);
        Assert.Equal(ToleranceLevel.Strict, effective.Levels[PolicyCategories.CoercionAndExtortion]);
        Assert.Equal(ToleranceLevel.Standard, effective.Levels[PolicyCategories.Harassment]);
    }

    [Fact]
    public void A_suggested_adult_never_lowers_a_declared_minor()
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[PolicyCategories.AdultContent] = ToleranceLevel.Strict;

        var effective = Resolve(
            PolicyFixtures.Pack(levels: levels),
            facts: [Fact(PolicyPropertyKeys.AgeBand, "18_24", "Suggested")]);

        Assert.Equal(ToleranceLevel.Strict, effective.Levels[PolicyCategories.AdultContent]);
    }

    [Fact]
    public void A_less_protective_binding_cannot_erase_a_minor_floor()
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[PolicyCategories.AdultContent] = ToleranceLevel.Strict;

        var binding = PolicyFixtures.Binding(
            action: BindingAction.LowerTolerance,
            categoryId: PolicyCategories.AdultContent,
            targetLevel: ToleranceLevel.Observe,
            approvedBy: "reviewer-b");

        var effective = Resolve(
            PolicyFixtures.Pack(levels: levels),
            bindings: [binding],
            facts: [Fact(PolicyPropertyKeys.AgeBand, "13_15", "Declared")]);

        Assert.Equal(ToleranceLevel.Strict, effective.Levels[PolicyCategories.AdultContent]);
        Assert.Contains(effective.Bindings, snapshot => snapshot.BindingId == binding.Id);
    }

    [Fact]
    public void A_single_lower_binding_applies_when_no_floor_blocks_it()
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[PolicyCategories.PaymentRedirection] = ToleranceLevel.Strict;

        var binding = PolicyFixtures.Binding(
            action: BindingAction.LowerTolerance,
            categoryId: PolicyCategories.PaymentRedirection,
            targetLevel: ToleranceLevel.Observe,
            approvedBy: "reviewer-b");

        var effective = Resolve(PolicyFixtures.Pack(levels: levels), bindings: [binding]);

        Assert.Equal(ToleranceLevel.Observe, effective.Levels[PolicyCategories.PaymentRedirection]);
        Assert.True(effective.Bindings.Single(snapshot => snapshot.BindingId == binding.Id).Selected);
    }

    [Fact]
    public void An_expired_HoldForReview_binding_still_withholds()
    {
        var binding = PolicyFixtures.Binding(
            action: BindingAction.LowerTolerance,
            categoryId: PolicyCategories.PaymentRedirection,
            targetLevel: ToleranceLevel.Observe,
            approvedBy: "reviewer-b",
            startsAt: Now.AddDays(-10),
            expiresAt: Now.AddDays(-1),
            reviewBy: Now.AddDays(-2)) with
        {
            OnExpiry = BindingOnExpiry.HoldForReview,
        };

        var effective = Resolve(PolicyFixtures.Pack(), bindings: [binding]);

        Assert.Contains(PolicyCategories.PaymentRedirection, effective.ForcedReviewCategories);
    }

    [Fact]
    public void A_missing_required_property_escalates_only_that_rule()
    {
        var ruleWithProperty = PolicyFixtures.PaymentRule(version: 2) with
        {
            Id = "rule-needs-age",
            RequiredPropertyKeys = [PolicyPropertyKeys.AgeBand],
            Bands = [new RuleBand(0.80, PolicyVerdict.Quarantine, "policy.high")],
        };

        var paymentRule = PolicyFixtures.PaymentRule() with
        {
            Id = "rule-payment",
            Bands = [new RuleBand(0.80, PolicyVerdict.Quarantine, "policy.high")],
        };

        var pack = PolicyFixtures.Pack();
        var rules = PolicyFixtures.RuleSet(ruleWithProperty, paymentRule);
        var effective = EffectivePolicyResolver.Resolve(
            pack,
            rules,
            bindings: [],
            facts: [],
            missingPropertyKeys: [PolicyPropertyKeys.AgeBand],
            addressedChain: ["entity:e1"],
            now: Now);

        var evaluation = new DeterministicPolicyEvaluator().Evaluate(new PolicyEvaluationInput
        {
            TenantId = PolicyFixtures.Tenant,
            Pack = pack,
            RuleSet = rules,
            Evidence = [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.10)],
            Effective = effective,
        });

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        var dependent = evaluation.Rules.Single(rule => rule.RuleId == "rule-needs-age");
        Assert.Equal(RuleEvaluationState.MissingProperty, dependent.State);
        Assert.Equal("policy.property_facts_unavailable", dependent.ReasonCode);

        var independent = evaluation.Rules.Single(rule => rule.RuleId == "rule-payment");
        Assert.Equal(RuleEvaluationState.NotFired, independent.State);
    }

    [Fact]
    public void The_effective_input_digest_is_order_independent_and_fact_sensitive()
    {
        var factsA =
            new[]
            {
                Fact(PolicyPropertyKeys.AgeBand, "18_24", "Declared"),
                Fact(PolicyPropertyKeys.Country, "GB", "Declared"),
            };
        var factsB = factsA.Reverse().ToArray();

        var first = Resolve(PolicyFixtures.Pack(), facts: factsA).ToSnapshot();
        var reordered = Resolve(PolicyFixtures.Pack(), facts: factsB).ToSnapshot();
        var changed = Resolve(
            PolicyFixtures.Pack(),
            facts: [Fact(PolicyPropertyKeys.AgeBand, "13_15", "Declared")]).ToSnapshot();

        Assert.Equal(first.Digest, reordered.Digest);
        Assert.NotEqual(first.Digest, changed.Digest);
    }

    [Fact]
    public void An_unknown_property_key_on_a_rule_is_refused()
    {
        var rule = PolicyFixtures.PaymentRule() with
        {
            RequiredPropertyKeys = ["not_a_v1_key"],
        };

        var violations = RuleSetValidator.Validate(PolicyFixtures.RuleSet(rule));

        Assert.Contains(violations, violation => violation.Code == RuleSetValidator.PropertyKeyUnknown);
    }

    private static EffectivePolicy Resolve(
        PolicyPack pack,
        IReadOnlyList<PolicyBinding>? bindings = null,
        IReadOnlyList<PolicyPropertyFact>? facts = null,
        IReadOnlyList<string>? missing = null) =>
        EffectivePolicyResolver.Resolve(
            pack,
            PolicyFixtures.RuleSet(),
            bindings ?? [],
            facts ?? [],
            missing ?? [],
            ["entity:e1"],
            Now);

    private static PolicyPropertyFact Fact(string key, string value, string state) => new()
    {
        Key = key,
        Value = value,
        State = state,
        Source = state,
        Scope = "entity:e1",
        Availability = "Available",
    };
}
