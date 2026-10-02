using StyloMail.Core;

namespace StyloMail.Cascade.Tests;

/// <summary>
/// The escalation rule, condition by condition, with a control for each thing it must NOT do.
/// </summary>
/// <remarks>
/// <b>Every test here names the condition it pins, because a rule that fires for two reasons passes
/// a test written for one.</b> The controls are as load-bearing as the firings: a band that escalates
/// everything and a band that escalates nothing both leave "the band fired" green, so each firing
/// test is paired with the answer just outside it.
/// </remarks>
public sealed class CascadeEscalationRuleTests
{
    private static readonly string[] FirstTwoIds =
        [SemanticDimensions.All[0].Id, SemanticDimensions.All[1].Id];

    [Fact]
    public void Escalates_a_dimension_the_local_model_did_not_answer()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence>
        {
            CascadeRows.Unavailable(FirstTwoIds[0]),
            CascadeRows.Available(FirstTwoIds[1], 0.97),
        };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions());

        // Only the unanswered one. UNKNOWN is a reason to escalate; it is not a reason to doubt the
        // answer that did arrive.
        Assert.Equal(FirstTwoIds[0], Assert.Single(decision.Escalated).Id);
        Assert.Equal(EscalationReason.LocalUnavailable, Assert.Single(decision.Reasons[FirstTwoIds[0]]));
        Assert.False(decision.Reasons.ContainsKey(FirstTwoIds[1]));
    }

    [Fact]
    public void Escalates_a_dimension_answered_over_reduced_coverage()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence>
        {
            CascadeRows.ReducedCoverage(FirstTwoIds[0], 0.9),
            CascadeRows.Available(FirstTwoIds[1], 0.97),
        };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions());

        Assert.Equal(EscalationReason.LocalReducedCoverage, Assert.Single(decision.Reasons[FirstTwoIds[0]]));
        Assert.Single(decision.Escalated);
    }

    [Fact]
    public void Escalates_a_dimension_whose_read_was_shortened()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence>
        {
            CascadeRows.Shortened(FirstTwoIds[0], 0.97),
            CascadeRows.Available(FirstTwoIds[1], 0.97),
        };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions());

        // The row is Available and its value is decisive, so this escalation is driven by the reason
        // attribute alone. Availability cannot see it: that is the whole point of this condition.
        Assert.Equal(EscalationReason.PartialRead, Assert.Single(decision.Reasons[FirstTwoIds[0]]));
        Assert.Single(decision.Escalated);
    }

    [Fact]
    public void Does_not_escalate_on_a_reason_attribute_that_says_nothing()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence>
        {
            CascadeRows.Available(
                FirstTwoIds[0],
                0.97,
                attributes: [new EvidenceAttribute { Name = "reason", Value = string.Empty }]),
            CascadeRows.Available(FirstTwoIds[1], 0.97),
        };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions());

        // A producer that wrote the attribute with no value has said nothing, which is a different
        // fact from "the read was whole". The control exists because presence alone would be a
        // one-line predicate that reads an empty string as a partial read.
        Assert.False(decision.Escalates);
    }

    [Fact]
    public void Escalates_a_dimension_whose_value_sits_inside_the_band()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence>
        {
            CascadeRows.Available(FirstTwoIds[0], 0.50),
            CascadeRows.Available(FirstTwoIds[1], 0.97),
        };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions());

        Assert.Equal(EscalationReason.IndecisiveValue, Assert.Single(decision.Reasons[FirstTwoIds[0]]));
        Assert.Single(decision.Escalated);
    }

    [Theory]
    [InlineData(0.35, true)]
    [InlineData(0.65, true)]
    [InlineData(0.3499, false)]
    [InlineData(0.6501, false)]
    public void Treats_the_band_bounds_as_inside_the_band(double value, bool escalates)
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence> { CascadeRows.Available(FirstTwoIds[0], value) };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions());

        // The bounds are inclusive, and the two values either side of them are the control. Without
        // them, a rule that escalated every value in [0, 1] would pass the two arms above.
        Assert.Equal(escalates, decision.Escalated.Any(d => d.Id == FirstTwoIds[0]));
    }

    [Fact]
    public void Escalates_a_dimension_the_local_model_answered_differently_last_run()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence> { CascadeRows.Available(FirstTwoIds[0], 0.80) };
        var prior = new Dictionary<string, double>(StringComparer.Ordinal) { [FirstTwoIds[0]] = 0.78 };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions(), prior);

        var reasons = decision.Reasons[FirstTwoIds[0]];
        Assert.Contains(EscalationReason.RunDisagreement, reasons);
    }

    [Fact]
    public void Keeps_the_local_answer_when_the_prior_run_agreed_with_it()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence>
        {
            CascadeRows.Available(FirstTwoIds[0], 0.80),
            CascadeRows.Available(FirstTwoIds[1], 0.96),
        };

        // The measured control: one host answered the same body twice and came back bit-identical.
        // A prior that agrees is the ordinary case, and a condition that fired on it would escalate
        // every message on every deployment that holds a prior at all.
        var prior = new Dictionary<string, double>(StringComparer.Ordinal) { [FirstTwoIds[0]] = 0.80 };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions(), prior);

        Assert.False(decision.Escalates);
    }

    [Theory]
    [InlineData(0.805, false)]
    [InlineData(0.82, true)]
    public void Fires_only_above_the_tolerance(double priorValue, bool escalates)
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence>
        {
            CascadeRows.Available(FirstTwoIds[0], 0.80),
            CascadeRows.Available(FirstTwoIds[1], 0.96),
        };

        var prior = new Dictionary<string, double>(StringComparer.Ordinal) { [FirstTwoIds[0]] = priorValue };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions(), prior);

        // The comparison is an exclusive bound, and the boundary is decided in BINARY arithmetic.
        // This test used to assert that a difference of exactly 0.01 is not a disagreement, and it was
        // red: 0.81 - 0.80 evaluates to 0.010000000000000009, a hair above the tolerance, so it fires.
        // The claim was wrong rather than the rule, and the honest form of the claim is the one here:
        // a difference well inside the tolerance does not fire and one well outside it does. A reader
        // comparing this threshold against another lane's figure should not expect an exact decimal
        // tie to be treated as agreement.
        Assert.Equal(escalates, decision.Reasons.ContainsKey(FirstTwoIds[0]));
    }

    [Fact]
    public void Does_not_ask_the_strong_model_over_a_dimension_policy_does_not_weigh()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence>
        {
            CascadeRows.Available(FirstTwoIds[0], 0.50),
            CascadeRows.Available(FirstTwoIds[1], 0.50),
        };

        var options = new CascadeOptions
        {
            DimensionWeights = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [FirstTwoIds[0]] = 0.0,
                [FirstTwoIds[1]] = 1.0,
            },
        };

        var decision = CascadeEscalationRule.Decide(input, local, options);

        // Both rows are equally undecided and only one of them could move a decision. This is the
        // minimal-intervention commitment as an assertion rather than as a sentence.
        Assert.False(decision.Reasons.ContainsKey(FirstTwoIds[0]));
        Assert.Equal(EscalationReason.IndecisiveValue, Assert.Single(decision.Reasons[FirstTwoIds[1]]));
    }

    [Fact]
    public void Treats_a_dimension_with_no_weight_entry_as_relevant()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence> { CascadeRows.Available(FirstTwoIds[0], 0.50) };

        // An empty table is the fail-closed direction: a wiring mistake, or a dimension added to the
        // question set and not yet weighed, escalates more than it should rather than less.
        var options = new CascadeOptions
        {
            DimensionWeights = new Dictionary<string, double>(StringComparer.Ordinal),
        };

        var decision = CascadeEscalationRule.Decide(input, local, options);

        Assert.True(decision.Escalates);
    }

    [Fact]
    public void Never_escalates_a_question_the_message_cannot_support()
    {
        var input = CascadeInputs.Input();
        var local = SemanticDimensions.All
            .Select(d => d.Id == SemanticDimensions.ConversationalContinuityId
                ? CascadeRows.NotApplicable(d.Id)
                : CascadeRows.Available(d.Id, 0.50))
            .ToList();

        var trust = new CascadeTrust
        {
            AlwaysEscalateIds = [SemanticDimensions.ConversationalContinuityId],
        };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions { Trust = trust });

        // Not even the trust table overrides this. The strong model cannot answer a question the
        // message has no referent for, so asking would spend a call to be told the same thing, and a
        // NotApplicable row that came back Unavailable would arm policy's asked-and-unanswered gate
        // on a message where nothing was ever asked.
        Assert.DoesNotContain(
            SemanticDimensions.ConversationalContinuityId,
            decision.Escalated.Select(d => d.Id));
    }

    [Fact]
    public void Escalates_a_dimension_the_trust_table_distrusts()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence>
        {
            CascadeRows.Available(FirstTwoIds[0], 0.99),
            CascadeRows.Available(FirstTwoIds[1], 0.99),
        };

        var trust = new CascadeTrust { AlwaysEscalateIds = [FirstTwoIds[0]] };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions { Trust = trust });

        // A decisive answer on a dimension the table distrusts still escalates, and the reason says
        // the trust table rather than a threshold, so a reader can tell why.
        Assert.Equal(EscalationReason.UntrustedDimension, Assert.Single(decision.Reasons[FirstTwoIds[0]]));
        Assert.Single(decision.Escalated);
    }

    [Fact]
    public void Escalates_a_dimension_the_local_arm_returned_no_row_for()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = new List<Evidence> { CascadeRows.Available(FirstTwoIds[1], 0.97) };

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions());

        // The row set the local arm returns is one row per dimension it was asked, so a missing row
        // is a dimension that was asked and answered with nothing. Reading it as absent-but-fine
        // would be the "absence of a field is not absence of the quantity" mistake in this tier.
        Assert.Equal(EscalationReason.LocalUnavailable, Assert.Single(decision.Reasons[FirstTwoIds[0]]));
    }

    [Fact]
    public void Keeps_a_confident_local_answer_and_asks_nobody()
    {
        var input = CascadeInputs.Input();
        var local = SemanticDimensions.All
            .Select(d => CascadeRows.Available(d.Id, 0.97))
            .ToList();

        var decision = CascadeEscalationRule.Decide(input, local, new CascadeOptions());

        // THE CONTROL the design note promises: a confident local answer is the normal path, and the
        // strong model is not spent on it. The values are decisive, nothing carries a reason, no prior
        // is held, and the trust table distrusts nothing.
        Assert.False(decision.Escalates);
        Assert.Empty(decision.Escalated);
        Assert.Empty(decision.Reasons);
    }
}
