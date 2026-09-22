using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// How a decision is turned into something an operator can read.
/// </summary>
/// <remarks>
/// This is the pane the console exists for. Spec 10.3 is explicit that it
/// answers "why was this held" with evidence and ordered reason codes rather
/// than a single score, and each test below pins one of the ways that promise
/// is easy to break while every other test stays green.
/// </remarks>
public sealed class DecisionViewTests
{
    private static DecisionResponse Decision(string json = Wire.Decision) => Json.Read<DecisionResponse>(json);

    /// <summary>
    /// The order is the answer. Policy ranked the reasons and the pane renders
    /// them as ranked; a view that sorted them by anything else, code
    /// alphabetically perhaps, would quietly rewrite the explanation.
    /// </summary>
    [Fact]
    public void Reasons_keep_the_order_policy_ranked_them_in()
    {
        var view = DecisionView.From(Decision());

        Assert.Equal(["credential_request_high", "link_display_mismatch"], view.Reasons.Select(r => r.Code));
    }

    /// <summary>
    /// Every reason resolves to the evidence behind it, because a reason with
    /// no visible evidence is an assertion. This is what makes the pane
    /// navigable rather than merely informative.
    /// </summary>
    [Fact]
    public void A_reason_carries_the_evidence_that_produced_it()
    {
        var view = DecisionView.From(Decision());

        var first = view.Reasons[0];

        Assert.Equal(["sig_cred", "sig_hist"], first.Evidence.Select(e => e.SignalId));
        Assert.Empty(first.MissingSignalIds);
    }

    /// <summary>
    /// A signal id the response did not include is named rather than dropped.
    /// </summary>
    /// <remarks>
    /// The tempting alternative is to render the signals that happen to be
    /// present and say nothing about the rest, which turns a truncated or
    /// mismatched response into a reason that looks fully evidenced. Naming
    /// what is missing keeps the operator's "why" answer honest.
    /// </remarks>
    [Fact]
    public void A_reason_naming_evidence_that_is_absent_says_so()
    {
        var decision = Decision() with
        {
            Reasons =
            [
                new ReasonResponse
                {
                    Code = "credential_request_high",
                    Message = "Requests credentials.",
                    EvidenceSignalIds = ["sig_cred", "sig_that_was_not_returned"],
                },
            ],
        };

        var view = DecisionView.From(decision);

        Assert.Equal(["sig_that_was_not_returned"], view.Reasons[0].MissingSignalIds);
    }

    // ===================== unavailable is not zero =====================

    /// <summary>
    /// The rule the whole project keeps restating, in the one place an operator
    /// would be misled by it.
    /// </summary>
    /// <remarks>
    /// A dimension nobody could measure is not a dimension that scored nothing.
    /// Rendering it as a zero-length bar would tell an operator the semantic
    /// layer looked and found nothing, when in fact it never looked: the
    /// opposite conclusion, from the same pixels.
    /// </remarks>
    [Theory]
    [InlineData(EvidenceAvailability.Unavailable)]
    [InlineData(EvidenceAvailability.NotApplicable)]
    public void A_dimension_that_was_not_measured_offers_no_score(EvidenceAvailability availability)
    {
        var decision = Decision() with
        {
            RiskDimensions =
            [
                new RiskDimensionResponse
                {
                    Name = "semantic",
                    Score = 0.0,
                    Availability = availability,
                    EvidenceSignalIds = [],
                },
            ],
        };

        var dimension = DecisionView.From(decision).Dimensions.Single();

        Assert.False(dimension.HasScore);
        Assert.Contains("not measured", dimension.ScoreLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An available dimension does carry its score.</summary>
    [Fact]
    public void An_available_dimension_carries_its_score()
    {
        var view = DecisionView.From(Decision()).Dimensions.Single(d => d.Name == "semantic");

        Assert.True(view.HasScore);
        Assert.Equal(0.91, view.Score);
    }

    /// <summary>
    /// Reduced coverage is a real value over weaker input, so it is shown, and
    /// shown as qualified.
    /// </summary>
    [Fact]
    public void A_dimension_over_reduced_coverage_shows_its_score_and_says_why()
    {
        var behavioural = DecisionView.From(Decision()).Dimensions.Single(d => d.Name == "behavioural");

        Assert.True(behavioural.HasScore);
        Assert.Equal(EvidenceAvailability.ReducedCoverage, behavioural.Availability);
        Assert.Contains("reduced", behavioural.Qualifier, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A semantic answer carries a probability and no confidence at all, which
    /// was measured against the live API rather than assumed. The pane must say
    /// that rather than showing a zero or an empty control waiting for data
    /// that is never coming.
    /// </summary>
    [Fact]
    public void A_signal_with_no_confidence_field_says_it_is_not_reported()
    {
        var semantic = DecisionView.From(Decision()).Evidence.Single(e => e.SignalId == "sig_cred");

        Assert.Null(semantic.Confidence);
        Assert.Contains("not reported", semantic.ConfidenceLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_signal_that_did_report_confidence_shows_it()
    {
        var behavioural = DecisionView.From(Decision()).Evidence.Single(e => e.SignalId == "sig_hist");

        Assert.Equal(0.6, behavioural.Confidence);
        Assert.Contains("0.6", behavioural.ConfidenceLabel, StringComparison.Ordinal);
    }

    // ===================== the aggregate =====================

    /// <summary>
    /// The risk index is a documented index and not a calibrated probability.
    /// Labelling it as one is the single easiest way for this pane to be
    /// confidently wrong.
    /// </summary>
    [Fact]
    public void The_risk_index_is_labelled_as_an_index()
    {
        var view = DecisionView.From(Decision());

        // The negation is the point rather than an accident, which is why the
        // wording is pinned: this string is the difference between an operator
        // reading "how likely is this to be malicious" and "how far along a
        // documented index is this".
        Assert.Contains("index", view.RiskIndexLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a probability", view.RiskIndexLabel, StringComparison.OrdinalIgnoreCase);

        // And never rendered as one.
        Assert.DoesNotContain("%", view.RiskIndexLabel, StringComparison.Ordinal);
        Assert.Equal("risk index 0.82 (an index, not a probability)", view.RiskIndexLabel);
    }

    /// <summary>
    /// Shadow mode forwards while recording what policy would have done. Both
    /// values have to be visible, or the pane cannot tell an operator that a
    /// message they are looking at was allowed but should have been held.
    /// </summary>
    [Fact]
    public void A_shadow_decision_shows_both_actions()
    {
        var view = DecisionView.From(Decision(Wire.DecisionInShadowMode));

        Assert.Equal("Allow", view.ActionLabel);
        Assert.NotNull(view.ShadowLabel);
        Assert.Contains("Quarantine", view.ShadowLabel, StringComparison.Ordinal);
    }

    /// <summary>A decision taken outright has nothing to say about shadow.</summary>
    [Fact]
    public void A_decision_taken_outright_has_no_shadow_label()
    {
        Assert.Null(DecisionView.From(Decision()).ShadowLabel);
    }

    /// <summary>
    /// Coverage is the qualifier on every number above it, so the flags that
    /// are true are the ones shown, each named.
    /// </summary>
    [Fact]
    public void Coverage_names_only_what_was_true()
    {
        var view = DecisionView.From(Decision());

        var names = view.Coverage.Select(c => c.Name).ToList();

        Assert.Contains("html_text_disagreement", names);
        Assert.Contains("conversation_context_missing", names);

        // BodyParsed is true and is the ordinary case, so it is not a flag.
        Assert.DoesNotContain("body_parsed", names);
    }

    [Fact]
    public void Versions_are_carried_and_the_absent_ones_are_named_as_absent()
    {
        var view = DecisionView.From(Decision());

        Assert.Contains(view.Versions, v => v.Label == "Policy" && v.Value == "policy-7");
        Assert.Contains(view.Versions, v => v.Label == "Classifier model" && v.Value == "jev-1.13.0");

        // Null is meaningful rather than missing: no regime was in force, and
        // the pane says so instead of leaving a blank an operator would read as
        // a rendering fault.
        var regime = view.Versions.Single(v => v.Label == "Regime");
        Assert.False(regime.IsPresent);
        Assert.Contains("not used", regime.Display, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cache provenance is what distinguishes a fresh decision from a reused one.</summary>
    [Fact]
    public void Cache_provenance_is_shown_when_there_is_one()
    {
        var view = DecisionView.From(Decision());

        Assert.NotNull(view.Cache);
        Assert.True(view.Cache.Hit);
        Assert.False(view.Cache.Stale);
    }

    [Fact]
    public void A_decision_with_no_cache_shows_none()
    {
        Assert.Null(DecisionView.From(Decision(Wire.DecisionWithoutOptionalMembers)).Cache);
    }

    /// <summary>
    /// The pane is built from the decision alone. A view that needed a live
    /// client to render could not be tested or photographed without a Host.
    /// </summary>
    [Fact]
    public void A_decision_renders_with_nothing_but_the_response()
    {
        var view = DecisionView.From(Decision());

        Assert.Equal("asm_0f4d2a", view.AssessmentId);
        Assert.NotEmpty(view.Reasons);
        Assert.NotEmpty(view.Evidence);
    }
}
