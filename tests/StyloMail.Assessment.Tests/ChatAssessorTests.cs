using StyloMail.Adaptive.Profiles;
using StyloMail.Chat;
using StyloMail.Core;
using StyloMail.Policy;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// The chat assessment path: what it produces, and the three things it must never say.
/// </summary>
public sealed class ChatAssessorTests
{
    private static ChatAssessor Build() =>
        new(new FakeProfileStore(new StepRecorder()), Builders.Options());

    /// <summary>
    /// The input as the connector would actually produce it.
    /// </summary>
    /// <remarks>
    /// Built through the real factory rather than hand-constructed. Hand-building it with an empty
    /// <see cref="ChatAnalysisInput.Links"/> would quietly test an input nothing produces, and the
    /// link signal would then be missing for a reason that has nothing to do with the assessor.
    /// </remarks>
    private static ChatAnalysisInput Input(string text = "hello", bool isExternal = false) =>
        ChatInputFactory.From(new ChatMessage
        {
            ChannelKind = ChannelKind.Slack,
            EventId = "Ev01",
            WorkspaceId = "T01",
            ChannelId = "C01",
            AuthorId = "U01",
            IsExternal = isExternal,
            Text = text,
            OccurredAt = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000),
        });

    private static AssessmentContext Context() => new()
    {
        TenantId = "acme",
        ShadowMode = false,
        AssessmentOnly = true,
        CorrelationId = "corr-chat-1",
        TimeProvider = new FixedClock(),
    };

    private static ValueTask<MailAssessment> AssessAsync(string text = "hello", bool isExternal = false) =>
        Build().AssessAsync(Input(text, isExternal), Context(), CancellationToken.None);

    [Fact]
    public async Task Every_chat_assessment_says_it_could_not_have_stopped_the_message()
    {
        // The claim this extension must never make by accident. Email decides before delivery; a
        // normal Slack app is told after the platform has already delivered. An assessment claiming
        // PreAcceptance would tell an operator the system could have stopped something it only
        // reacted to.
        var assessment = await AssessAsync();

        Assert.Equal(DeliveryTiming.PostDelivery, assessment.DeliveryTiming);
    }

    [Fact]
    public async Task A_chat_assessment_records_the_channel_it_is_about()
    {
        // Which channel a decision came from, without a reader going to find the input. This is the
        // member plan 1 added to MailAssessment and 2b Task 1 made required.
        var assessment = await AssessAsync();

        Assert.Equal(ChannelKind.Slack, assessment.Channel.Kind);
        Assert.Equal("T01", assessment.Channel.WorkspaceId);
        Assert.Equal("C01", assessment.Channel.ChannelId);
    }

    [Fact]
    public async Task The_semantic_gap_is_an_explicit_unavailable_for_every_dimension()
    {
        // "Unknown is a distinct state" applied with full force: a verdict reached without the
        // classifier must be legible as an uninformed judgement. An absent entry would let a reader
        // infer a clean result, and a zero would state one outright.
        var assessment = await AssessAsync();

        foreach (var dimension in SemanticDimensions.All)
        {
            var entry = Assert.Single(assessment.Evidence, e => e.SignalId == dimension.Id);
            Assert.Equal(EvidenceAvailability.Unavailable, entry.Availability);
            Assert.Null(entry.Value);
        }
    }

    [Fact]
    public async Task A_chat_assessment_says_why_it_has_no_semantic_evidence()
    {
        var assessment = await AssessAsync();

        Assert.Contains(
            assessment.Reasons,
            r => r.Code == AssessmentReasonCodes.SemanticUnavailable
                && r.EvidenceSignalIds.Count > 0);
    }

    [Fact]
    public async Task A_chat_assessment_takes_no_action()
    {
        // Observe only. The policy decision is still recorded, so the audit trail exists to decide
        // on later, but the action taken is none.
        var assessment = await AssessAsync("urgent <http://paypal.com.evil.example|paypal.com>");

        Assert.Equal(MailAction.Allow, assessment.Action);
    }

    [Fact]
    public async Task The_signal_a_chat_message_supports_is_still_recorded()
    {
        // Taking no action is not the same as looking at nothing. The deterministic producer ran,
        // and its finding is in the ledger next to the decision.
        var assessment = await AssessAsync("urgent <http://paypal.com.evil.example|paypal.com>");

        var mismatch = Assert.Single(
            assessment.Evidence,
            e => e.SignalId == "deterministic.link_display_mismatch");

        Assert.Equal(EvidenceAvailability.Available, mismatch.Availability);
        Assert.NotNull(assessment.RiskDimensions);
    }

    [Fact]
    public async Task A_member_is_assessed_against_behavioural_evidence()
    {
        // Job two: an authenticated principal fanning out to people it never talks to. This is the
        // evidence that job rests on, and it exists only because the direction put the member in the
        // outbound pool rather than among strangers.
        var assessment = await AssessAsync("hello", isExternal: false);

        Assert.Contains(assessment.Evidence, e => e.SignalId == "behavioural.trend.velocity");
        Assert.Contains(assessment.Evidence, e => e.SignalId == "behavioural.drift.distance");
    }

    [Fact]
    public async Task An_external_authors_missing_behavioural_evidence_is_stated_rather_than_absent()
    {
        // The inbound sender scope is qualified by email's authentication provenance, which a chat
        // message does not have, so this path cannot read a profile yet. Saying so is the difference
        // between an uninformed judgement and one that merely looks quiet.
        var assessment = await AssessAsync("hello", isExternal: true);

        var reason = Assert.Single(
            assessment.Reasons,
            r => r.Code == AssessmentReasonCodes.ChatBehaviouralUnavailable);

        Assert.NotEmpty(reason.EvidenceSignalIds);
    }

    [Fact]
    public async Task The_chat_path_decides_with_the_standard_policy_composition()
    {
        // The drift pin, in the form that could actually be built.
        //
        // The obvious version, pushing equivalent evidence down both paths and comparing the
        // actions, does not exist: the two evidence sets differ structurally at three points that
        // have nothing to do with the composition. Chat always records twelve semantic dimensions
        // unavailable and three deterministic signals, the mail path's relationship profiles and
        // acceptance step change what it carries, and its semantic-outage override turns an outage
        // into a declined responsibility that chat has no counterpart for. A comparison across all
        // that measures the differences, not the drift.
        //
        // So the pin is on the composition itself, which is the thing at risk: chat builds the
        // scorer and the engine from the options rather than sharing extracted steps, so this fails
        // if it ever drifts to different weights, a different engine, or a direction other than the
        // one the membership derives.
        var assessment = await AssessAsync("urgent <http://paypal.com.evil.example|paypal.com>");

        var options = Builders.Options();
        var risk = CompositeRiskScorer.Compute(assessment.Evidence, options.Policy.DimensionWeights);
        var decision = new MailPolicyEngine(options.Policy, new FixedClock()).Decide(new PolicyInput
        {
            Evidence = assessment.Evidence,
            Risk = risk,
            Context = new PolicyContext
            {
                EmergencyKillSwitchEngaged = false,
                OutboundQuotaExhausted = false,
                VerifiedSecurityRuleViolations = [],
                AllowlistEntryValid = false,
                BaselineFrozenForSuspectedCompromise = false,
            },
            Direction = MailDirection.Outbound,
        });

        Assert.Equal(risk.Index, assessment.RiskIndex);
        Assert.Equal(decision.Action, assessment.ProposedActionInShadow);
    }

    [Fact]
    public async Task A_member_is_read_through_their_own_profile_and_a_stranger_is_not()
    {
        // The direction selects the pool the observations are counted in, and the two are never
        // merged. An always-inbound rule would have filed a member's traffic with strangers'.
        //
        // The stranger still carries the behavioural signal ids, because this path records an
        // explicit gap rather than an absent entry. What separates the two is that the member was
        // actually read and the stranger was not.
        var member = await AssessAsync("hello", isExternal: false);
        var stranger = await AssessAsync("hello", isExternal: true);

        Assert.DoesNotContain(
            member.Reasons,
            r => r.Code == AssessmentReasonCodes.ChatBehaviouralUnavailable);

        Assert.Contains(
            stranger.Reasons,
            r => r.Code == AssessmentReasonCodes.ChatBehaviouralUnavailable);
    }
}
