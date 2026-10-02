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
    /// The fraction has three readings and the pane says which one it is: zero
    /// and one are measurements at the two ends of the range, and null is a
    /// decision made before the member was recorded. Collapsing the third into
    /// the first reports an unrecorded arithmetic as a measured empty one, which
    /// is the failure the neighbour's remarks exist to prevent, so all three
    /// arms are asserted rather than the value alone.
    /// </summary>
    [Fact]
    public void The_covered_weight_fraction_says_which_of_its_three_readings_it_is()
    {
        var full = DecisionView.From(Decision());
        Assert.Contains("1 was counted", full.CoveredWeightArithmetic);
        Assert.DoesNotContain("not recorded", full.CoveredWeightArithmetic);

        // The measured-empty reading, derived by substitution from the first so
        // that the three arms are covered on ONE fixture rather than on files
        // that can drift apart.
        var empty = DecisionView.From(Decision(
            Wire.Decision.Replace("\"coveredWeightFraction\": 1.0", "\"coveredWeightFraction\": 0.0")));
        Assert.Contains("NOT a measurement", empty.CoveredWeightArithmetic);
        Assert.DoesNotContain("not recorded", empty.CoveredWeightArithmetic);

        var absent = DecisionView.From(Decision(Wire.DecisionWithoutOptionalMembers));
        Assert.Contains("not recorded", absent.CoveredWeightArithmetic);
        Assert.DoesNotContain("the share the refusal text quotes", absent.CoveredWeightArithmetic);
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

                    // An unmeasured row says nothing about the arithmetic
                    // either. These are not "false" and not "0": the row was
                    // never scored, so whether it counted is a question that was
                    // not reached, and null is the only honest answer.
                    Weight = null,
                    Counted = null,
                    ExclusionReason = null,
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

    /// <summary>
    /// A reason the wire carries reaches the row, joined when there is more than one.
    /// </summary>
    /// <remarks>
    /// The value alone cannot carry this. The fit's kept length never reaches this wire and the
    /// coverage flag answers a different question, so a row the model answered over a shortened body
    /// renders exactly like a row it answered whole unless the reason is on the row. Joined rather
    /// than shown one per line because the reasons answer one question, why this row reads the way
    /// it does.
    /// </remarks>
    [Fact]
    public void A_reason_the_wire_carries_reaches_the_row()
    {
        var semantic = DecisionView.From(Decision()).Evidence.Single(e => e.SignalId == "sig_cred");

        Assert.Equal(
            "the client shortened the message body to fit the context window"
                + "; no behavioural profile was available to the classifier",
            semantic.ReasonLabel);
    }

    /// <summary>
    /// A row the producer explained nothing about renders nothing, not the word "unknown".
    /// </summary>
    /// <remarks>
    /// This asserts an absence, so the test above it is its firing control: without that one a null
    /// here would read the same whether the mechanism works or the member was never bound. A
    /// qualifier claiming a gap where there is none is the same mistake as rendering an unavailable
    /// dimension as zero, which is the rule the scope label already follows.
    /// </remarks>
    [Fact]
    public void A_row_with_no_reason_renders_nothing()
    {
        var behavioural = DecisionView.From(Decision()).Evidence.Single(e => e.SignalId == "sig_hist");

        Assert.Null(behavioural.ReasonLabel);
    }

    /// <summary>
    /// A REFUSAL reaches the row: a reason on an Unavailable row is rendered, not filtered away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the case the member exists for. Its own remark calls <c>AvailabilityReasons</c> "the only
    /// channel that carries a cut or a refusal", and the producer emits it on an <c>Unavailable</c> row
    /// when a body is cut to nothing. <c>ReasonLabel</c> is deliberately UNFILTERED on availability, and
    /// that is the property a consistency edit would remove, because <c>AvailabilityFacts.Produces</c>
    /// gates the sibling <c>ValueLabel</c> on the same row.
    /// </para>
    /// <para>
    /// The row is constructed rather than read from the wire fixture: the fixture carries no
    /// <c>Unavailable</c> evidence row, and adding one would move every other test that reads it. The
    /// reason text is the producer's <c>EmptyBodyShortenedReason</c>, copied by hand because this project
    /// references no Nimble assembly by design.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_refusal_reason_reaches_the_row_even_though_the_row_is_unavailable()
    {
        var decision = Decision() with
        {
            Evidence =
            [
                new EvidenceResponse
                {
                    SignalId = "sig_refused",
                    Origin = EvidenceOrigin.Semantic,
                    Availability = EvidenceAvailability.Unavailable,
                    SourceVersion = "jev-1.13.0",
                    ObservedAt = DateTimeOffset.UnixEpoch,
                    AvailabilityReasons =
                    [
                        "the client shortened the message body to nothing, so the answer was produced over an empty body",
                    ],
                },
            ],
        };

        var refused = Assert.Single(DecisionView.From(decision).Evidence);

        // The contrast is the point: the row shows no value, and still says why.
        Assert.Equal("not produced", refused.ValueLabel);
        Assert.Equal(
            "the client shortened the message body to nothing, so the answer was produced over an empty body",
            refused.ReasonLabel);
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

    /// <summary>
    /// A signal id the Host sent twice renders both rows rather than crashing.
    /// </summary>
    /// <remarks>
    /// <b>Found only by driving the console against a real Host.</b> A real
    /// decision came back with <c>behavioural.trend.velocity</c> in the evidence
    /// list twice, and the pane's lookup was a <c>ToDictionary</c>, which throws
    /// on a duplicate key. The whole pane failed to load: the operator saw
    /// nothing at all, and every unit test passed because every fixture had
    /// unique ids.
    ///
    /// <para>
    /// Resolving to a lookup rather than keeping the first is deliberate. Two
    /// rows under one id is real evidence, and silently dropping one would be
    /// the console editing the Host's answer, which is the same class of mistake
    /// as rendering an unavailable dimension as zero.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_signal_id_sent_twice_renders_both_rows()
    {
        var decision = Decision() with
        {
            Evidence =
            [
                new EvidenceResponse
                {
                    SignalId = "behavioural.trend.velocity",
                    Origin = EvidenceOrigin.Behavioural,
                    Availability = EvidenceAvailability.Available,
                    Value = 0.4,
                    SourceVersion = "adaptive-1",
                    ObservedAt = DateTimeOffset.UnixEpoch,
                    ObservedScope = "sender",
                },
                new EvidenceResponse
                {
                    SignalId = "behavioural.trend.velocity",
                    Origin = EvidenceOrigin.Behavioural,
                    Availability = EvidenceAvailability.Available,
                    Value = 0.9,
                    SourceVersion = "adaptive-1",
                    ObservedAt = DateTimeOffset.UnixEpoch,
                    ObservedScope = "recipient",
                },
            ],
            Reasons =
            [
                new ReasonResponse
                {
                    Code = "behaviour.velocity",
                    Message = "Velocity moved.",
                    EvidenceSignalIds = ["behavioural.trend.velocity"],
                },
            ],
        };

        var view = DecisionView.From(decision);

        // Both rows survive, and the reason that names the id shows both.
        Assert.Equal(2, view.Evidence.Count);
        Assert.Equal(2, view.Reasons[0].Evidence.Count);
        Assert.Empty(view.Reasons[0].MissingSignalIds);
        Assert.Equal(["sender", "recipient"], view.Reasons[0].Evidence.Select(e => e.ObservedScope));
    }

    /// <summary>
    /// Two rows that agree on the signal id AND the scope are still told apart,
    /// by the trend window the producer ran them for.
    /// </summary>
    /// <remarks>
    /// <b>The case that reached the console as two identical lines.</b> The
    /// behavioural evaluator emits one velocity row per trend window, so a real
    /// response carries <c>behavioural.trend.velocity</c> twice at the same
    /// scope with <c>window</c> "burst" and "slow". The pane kept both rows
    /// (dropping one would be the console editing the Host's answer) and drew
    /// them identically, which is a different way of losing the distinction the
    /// operator needs: a burst and a slow trend are different findings.
    ///
    /// <para>
    /// The label is asserted rather than the property, because the label is what
    /// is rendered. A qualifier that exists on the model and is bound to nothing
    /// is how this gap survived the first fix.
    /// </para>
    /// </remarks>
    [Fact]
    public void Two_trend_rows_are_told_apart_by_their_window()
    {
        var decision = Decision() with
        {
            Evidence =
            [
                Trend(window: "burst", value: 0.4),
                Trend(window: "slow", value: 0.9),
            ],
            Reasons =
            [
                new ReasonResponse
                {
                    Code = "behaviour.velocity",
                    Message = "Velocity moved.",
                    EvidenceSignalIds = ["behavioural.trend.velocity"],
                },
            ],
        };

        var rows = DecisionView.From(decision).Reasons[0].Evidence;

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            ["OutboundSender · burst", "OutboundSender · slow"],
            rows.Select(e => e.ScopeLabel));
    }

    /// <summary>
    /// A row with no window says nothing about a window, rather than "unknown".
    /// </summary>
    /// <remarks>
    /// Most rows in a real response are not windowed: every semantic row and
    /// every drift row. Reporting them as an unknown window would tell an
    /// operator that something was lost on 20 rows out of 24, while the truth is
    /// that the producer does not partition those signals at all. On the same
    /// terms as an unavailable dimension rendering "not measured" rather than
    /// 0.0, absence is a fact and is rendered as one.
    /// </remarks>
    [Fact]
    public void An_unwindowed_row_carries_no_qualifier()
    {
        var decision = Decision() with
        {
            Evidence =
            [
                new EvidenceResponse
                {
                    SignalId = "semantic.conversational_continuity",
                    Origin = EvidenceOrigin.Semantic,
                    Availability = EvidenceAvailability.NotApplicable,
                    SourceVersion = "jev/1",
                    ObservedAt = DateTimeOffset.UnixEpoch,
                },
            ],
        };

        var row = Assert.Single(DecisionView.From(decision).Evidence);

        Assert.Null(row.ScopeLabel);
        Assert.Null(row.Window);
    }

    /// <summary>
    /// A window with no scope still qualifies the row, which is what makes the
    /// two parts one label rather than two independent ones.
    /// </summary>
    [Fact]
    public void A_window_without_a_scope_still_qualifies_the_row()
    {
        var decision = Decision() with
        {
            Evidence = [Trend(window: "burst", value: 0.4) with { ObservedScope = null }],
        };

        var row = Assert.Single(DecisionView.From(decision).Evidence);

        Assert.Equal("burst", row.ScopeLabel);
    }

    /// <summary>One behavioural trend row, at the scope a real response uses.</summary>
    private static EvidenceResponse Trend(string window, double value) => new()
    {
        SignalId = "behavioural.trend.velocity",
        Origin = EvidenceOrigin.Behavioural,
        Availability = EvidenceAvailability.Available,
        Value = value,
        SourceVersion = "adaptive/1",
        ObservedAt = DateTimeOffset.UnixEpoch,
        ObservedScope = "OutboundSender",
        Window = window,
    };

    // ===================== delivery timing =====================

    /// <summary>
    /// A decision taken after the platform had already delivered says so.
    /// </summary>
    /// <remarks>
    /// This is the one field on a decision that changes what the rest of the
    /// pane means. Every action a post-delivery decision names is post-hoc, so
    /// rendering it like any other would tell an operator the system could have
    /// intervened when it only reacted. The field is required on the assessment
    /// precisely so it cannot be defaulted into silence.
    /// </remarks>
    [Fact]
    public void A_post_delivery_decision_says_so()
    {
        var decision = Decision() with { DeliveryTiming = DeliveryTiming.PostDelivery };

        var view = DecisionView.From(decision);

        Assert.True(view.IsPostDelivery);
        Assert.NotNull(view.PostDeliveryCaveat);
        Assert.Contains("already delivered", view.PostDeliveryCaveat, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("post-hoc", view.PostDeliveryCaveat, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ordinary case carries no banner.
    /// </summary>
    /// <remarks>
    /// A caveat shown on every decision is one an operator stops reading, and
    /// the whole value of this one is that it is exceptional.
    /// </remarks>
    [Fact]
    public void A_pre_acceptance_decision_carries_no_caveat()
    {
        var view = DecisionView.From(Decision());

        Assert.False(view.IsPostDelivery);
        Assert.Null(view.PostDeliveryCaveat);
    }

    /// <summary>The channel is shown, because one of them cannot be acted on before delivery.</summary>
    [Theory]
    [InlineData(ChannelKind.Email, "Email")]
    [InlineData(ChannelKind.Slack, "Slack")]
    [InlineData(ChannelKind.Discord, "Discord")]
    public void The_channel_is_named(ChannelKind kind, string expected)
    {
        var decision = Decision() with { Channel = new ChannelContext { Kind = kind } };

        Assert.Equal(expected, DecisionView.From(decision).ChannelLabel);
    }

    /// <summary>
    /// A channel this build does not know fails loudly rather than defaulting.
    /// </summary>
    /// <remarks>
    /// Same rule as the action: a value the console does not recognise is an
    /// error, never a default. The channel decides whether a decision could
    /// have been preventative at all, so guessing it would guess the most
    /// consequential thing on the pane.
    /// </remarks>
    [Fact]
    public async Task An_unknown_channel_fails_loudly()
    {
        var json = Wire.Decision.Replace("\"kind\": \"Email\"", "\"kind\": \"Teams\"", StringComparison.Ordinal);

        var exception = await Assert.ThrowsAsync<Api.StyloMailApiException>(
            () => new Api.StyloMailApiClient(
                    StubHttpMessageHandler.ReturningJson(json).CreateClient(),
                    new TestApiKeyProvider())
                .GetDecisionAsync("asm_0f4d2a"));

        Assert.Equal(Api.StyloMailApiFailure.UnreadableResponse, exception.Failure);
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

    // ===================== the arithmetic behind the index =====================

    /// <summary>
    /// The index says what it was divided by, so an operator can check it.
    /// </summary>
    /// <remarks>
    /// An index nobody can check from what is on screen is one they have to take
    /// on trust, and the whole reason the denominator is served is that the check
    /// is possible.
    /// </remarks>
    [Fact]
    public void The_index_says_what_it_was_divided_by()
    {
        var view = DecisionView.From(Decision());

        Assert.Contains("1.33", view.RiskIndexArithmetic, StringComparison.Ordinal);
        Assert.Contains("divided by", view.RiskIndexArithmetic, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>An unrecorded divisor is not a divisor of zero.</b>
    /// </summary>
    /// <remarks>
    /// A decision taken before the arithmetic was served has no denominator at
    /// all, and rendering that as <c>0</c> would report an index that cannot be
    /// checked as an index that was divided by nothing. The two sentences have to
    /// stay different, and the absent case has to be a sentence rather than a
    /// blank, because a blank reads as a rendering fault.
    /// </remarks>
    [Fact]
    public void An_unrecorded_divisor_is_not_a_divisor_of_zero()
    {
        var absent = DecisionView.From(Decision(Wire.DecisionWithoutOptionalMembers));
        var zero = DecisionView.From(Decision() with { RiskIndexDenominator = 0.0 });

        Assert.Contains("not recorded", absent.RiskIndexArithmetic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not recorded", zero.RiskIndexArithmetic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0", zero.RiskIndexArithmetic, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row that entered the index says nothing extra about it.
    /// </summary>
    /// <remarks>
    /// A marker on every row is one nobody reads, and the counted case is the
    /// ordinary one.
    /// </remarks>
    [Fact]
    public void A_counted_row_says_nothing_about_the_arithmetic()
    {
        var counted = DecisionView.From(Decision()).Dimensions
            .Where(d => d.Counted == true)
            .ToList();

        Assert.NotEmpty(counted);
        Assert.All(counted, d =>
        {
            Assert.False(d.HasContributionNote);
            Assert.Empty(d.ContributionLabel);
        });
    }

    /// <summary>
    /// <b>A masked row names itself as masked, and says its weight stayed out.</b>
    /// </summary>
    /// <remarks>
    /// This is the defect decision 37 rules on. A row that was measured and came
    /// back 0.0 was counted and dilutes the index; a row decision 31 masked
    /// contributed nothing. Both arrive as <c>score: 0, availability:
    /// Available</c>, so without the flag an operator reconstructing the
    /// arithmetic from the pane would add the masked row's weight back into the
    /// divisor and get a number that agrees with a shape the system no longer has.
    /// </remarks>
    [Fact]
    public void A_masked_row_names_its_reason_and_the_weight_that_stayed_out()
    {
        var masked = DecisionView.From(Decision()).Dimensions.Single(d => d.Name == "reputation");

        Assert.False(masked.Counted);

        // And it shows a number, which is why it has to say otherwise: this row
        // is the one that would otherwise be added into the divisor.
        Assert.True(masked.HasScore);
        Assert.True(masked.HasContributionNote);

        // The whole sentence, not a substring of it. The Host's reason is itself
        // a sentence ending in a full stop, and the composed line renders that
        // stop and then a second one if the composition does not look: this is
        // exactly how "rule.. Its weight" reached the shipped fixture and the
        // smoke's selector, which matches whole controls. Asserting pieces would
        // pass over it.
        Assert.Equal(
            "Not counted towards the index: Masked by the trusted-history rule. "
                + "Its weight of 0.25 is not in the divisor.",
            masked.ContributionLabel);
    }

    /// <summary>
    /// A row that was never measured needs no further note: it already says so.
    /// </summary>
    /// <remarks>
    /// This is the main smoke's whole decision, where the semantic layer is down
    /// and every dimension is unmasked-but-unmeasured. A sentence on each of
    /// them would be the same line eleven times, and the marker would stop
    /// meaning anything on the rows that need it.
    /// </remarks>
    [Fact]
    public void An_unmeasured_row_that_did_not_count_needs_no_further_note()
    {
        var decision = Decision() with
        {
            RiskDimensions =
            [
                new RiskDimensionResponse
                {
                    Name = "semantic",
                    Score = 0.0,
                    Availability = EvidenceAvailability.Unavailable,
                    Weight = 0.5,
                    Counted = false,
                    ExclusionReason = null,
                    EvidenceSignalIds = [],
                },
            ],
        };

        var masked = DecisionView.From(decision).Dimensions.Single();

        Assert.False(masked.Counted);
        Assert.False(masked.HasScore);
        Assert.False(masked.HasContributionNote);
        Assert.Contains("not measured", masked.ScoreLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A masked row with a reason says the reason even if it shows no score.
    /// </summary>
    /// <remarks>
    /// The Host only sets a reason where a row carried an answer and was
    /// excluded anyway, so this pairs with the score path rather than replacing
    /// it. It is asserted separately because a reason is the one thing a reader
    /// cannot get from the availability, and losing it silently would leave the
    /// exclusion unexplained.
    /// </remarks>
    [Theory]
    [InlineData("Excluded by policy.")]
    [InlineData("Excluded by policy")]
    public void A_masked_row_with_a_reason_says_the_reason(string reason)
    {
        var decision = Decision() with
        {
            RiskDimensions =
            [
                new RiskDimensionResponse
                {
                    Name = "reputation",
                    Score = 0.0,
                    Availability = EvidenceAvailability.NotApplicable,
                    Weight = 0.5,
                    Counted = false,
                    ExclusionReason = reason,
                    EvidenceSignalIds = [],
                },
            ],
        };

        var masked = DecisionView.From(decision).Dimensions.Single();

        // One stop, whether or not the Host wrote one. A reason handed over
        // without its full stop is a clause in this sentence and gets one; a
        // reason that has one keeps exactly one.
        Assert.Equal(
            "Not counted towards the index: Excluded by policy. "
                + "Its weight of 0.5 is not in the divisor.",
            masked.ContributionLabel);
    }

    /// <summary>
    /// <b>A row that predates the arithmetic never reads as a row that did not
    /// count.</b>
    /// </summary>
    /// <remarks>
    /// Null is a third state, and it is said even on a row that shows no score.
    /// The Host could have derived false from an older row's availability and
    /// deliberately does not: with no weight behind the flag the arithmetic
    /// still cannot be reproduced, and showing half of the pair invites a reader
    /// to guess the other half. So this is the one case the note is never
    /// suppressed, because two rows that both read "not measured" would
    /// otherwise be indistinguishable.
    /// </remarks>
    [Theory]
    [InlineData(EvidenceAvailability.Available, 0.5)]
    [InlineData(EvidenceAvailability.Unavailable, 0.0)]
    public void A_row_that_predates_the_arithmetic_says_so_rather_than_reading_as_false(
        EvidenceAvailability availability, double score)
    {
        var decision = Decision() with
        {
            RiskDimensions =
            [
                new RiskDimensionResponse
                {
                    Name = "semantic",
                    Score = score,
                    Availability = availability,
                    Weight = null,
                    Counted = null,
                    ExclusionReason = null,
                    EvidenceSignalIds = [],
                },
            ],
        };

        var row = DecisionView.From(decision).Dimensions.Single();

        Assert.Null(row.Counted);
        Assert.True(row.HasContributionNote);
        Assert.Contains("not recorded", row.ContributionLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not counted", row.ContributionLabel, StringComparison.OrdinalIgnoreCase);
    }
}
