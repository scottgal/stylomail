using StyloMail.Adaptive.Profiles;
using StyloMail.Chat;
using StyloMail.Chat.Slack;
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
    private static ChatAnalysisInput Input(
        string text = "hello",
        bool isExternal = false,
        ChatConversationKind conversation = ChatConversationKind.Audience) =>
        ChatInputFactory.From(new ChatMessage
        {
            ChannelKind = ChannelKind.Slack,
            EventId = "Ev01",
            WorkspaceId = "T01",
            ChannelId = "C01",
            AuthorId = "U01",
            IsExternal = isExternal,

            // Mapped back through the platform's own vocabulary so the factory does the mapping these
            // tests are exercising, rather than the test asserting against its own translation.
            Conversation = conversation switch
            {
                ChatConversationKind.People => SlackConversationType.DirectMessage,
                ChatConversationKind.Audience => SlackConversationType.Channel,
                _ => SlackConversationType.Unknown,
            },
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
    public async Task An_external_author_is_read_through_the_chat_author_scope()
    {
        // Email's inbound scope is qualified by DKIM and SPF provenance because an email sender's
        // identity is a claim. A chat author's is asserted by the platform and verified by the
        // connector before an assessment existed, so it gets its own scope kind with no provenance
        // component rather than borrowing a pool whose meaning is "claimed, and here is the proof".
        var assessment = await AssessAsync("hello", isExternal: true);

        // Matched on the scope as well as the signal, because a behavioural signal is emitted once
        // per profile read and this assessment reads two: the author, and the conversation.
        Assert.Contains(
            assessment.Evidence,
            e => e.SignalId == "behavioural.drift.distance"
                && e.ObservedScope == nameof(ProfileScopeKind.ChatAuthor));
    }

    [Fact]
    public async Task The_two_directions_read_different_scopes_for_the_same_author()
    {
        // The whole point of deriving the direction: a member and a stranger are different traffic,
        // and the scope is where that shows up. Reading both through one scope would merge the two
        // pools the adaptive engine keeps apart on purpose.
        var member = await AssessAsync("hello", isExternal: false);
        var stranger = await AssessAsync("hello", isExternal: true);

        Assert.Contains(
            member.Evidence,
            e => e.SignalId == "behavioural.drift.distance"
                && e.ObservedScope == nameof(ProfileScopeKind.OutboundSender));

        Assert.Contains(
            stranger.Evidence,
            e => e.SignalId == "behavioural.drift.distance"
                && e.ObservedScope == nameof(ProfileScopeKind.ChatAuthor));

        // And neither is read through the other's scope, which is the claim that matters.
        Assert.DoesNotContain(
            member.Evidence,
            e => e.SignalId == "behavioural.drift.distance"
                && e.ObservedScope == nameof(ProfileScopeKind.ChatAuthor));

        Assert.DoesNotContain(
            stranger.Evidence,
            e => e.SignalId == "behavioural.drift.distance"
                && e.ObservedScope == nameof(ProfileScopeKind.OutboundSender));
    }

    [Fact]
    public async Task The_attempt_is_written_to_the_author_pool_and_the_conversation_pool()
    {
        // Observed state is what velocity and drift are later read from, so it records every message
        // that was assessed rather than the ones that turned out interesting. The mail path counts
        // attempts rather than deliveries for the same reason.
        var store = new FakeProfileStore(new StepRecorder());
        var assessor = new ChatAssessor(store, Builders.Options());

        await assessor.AssessAsync(Input("hello"), Context(), CancellationToken.None);

        var scopes = store.Writes.Select(w => w.Key.Scope).ToList();

        Assert.Contains(ProfileScopeKind.OutboundSender, scopes);
        Assert.Contains(ProfileScopeKind.Relationship, scopes);
    }

    [Fact]
    public async Task The_attempt_is_written_even_when_policy_would_have_held_it()
    {
        // Unconditional on the outcome. A write that only happened for traffic we flagged would make
        // the baseline a record of what we found suspicious, which is the opposite of a baseline.
        var store = new FakeProfileStore(new StepRecorder());
        var assessor = new ChatAssessor(store, Builders.Options());

        var assessment = await assessor.AssessAsync(
            Input("urgent <http://paypal.com.evil.example|paypal.com>"),
            Context(),
            CancellationToken.None);

        Assert.NotEqual(MailAction.Allow, assessment.ProposedActionInShadow);
        Assert.NotEmpty(store.Writes);
    }

    [Fact]
    public async Task A_conversation_whose_kind_is_unknown_writes_no_relationship()
    {
        // The platform did not say whether this went to people or an audience, and filing it as
        // either would answer a question the event did not. The author pool is still written, so the
        // gap is in the relationship rather than in the record of the attempt.
        var store = new FakeProfileStore(new StepRecorder());
        var assessor = new ChatAssessor(store, Builders.Options());

        await assessor.AssessAsync(Input("hello", conversation: ChatConversationKind.Unknown), Context(), CancellationToken.None);

        var scopes = store.Writes.Select(w => w.Key.Scope).ToList();

        Assert.Contains(ProfileScopeKind.OutboundSender, scopes);
        Assert.DoesNotContain(ProfileScopeKind.Relationship, scopes);

        // Absent rather than empty, which leaves novelty unanswerable for this message rather than
        // making it zero.
        Assert.All(store.Writes, w => Assert.Null(w.Observation.RecipientKeys));
    }

    [Fact]
    public async Task A_direct_message_and_a_channel_post_do_not_accumulate_together()
    {
        // "Talking to people it never talks to" and "posting in channels it never posts in" are
        // different claims. Merged into one pool, the fan-out evidence reports a member as suddenly
        // talking to new people when they have merely posted somewhere new.
        var dm = new FakeProfileStore(new StepRecorder());
        await new ChatAssessor(dm, Builders.Options()).AssessAsync(
            Input("hello", conversation: ChatConversationKind.People), Context(), CancellationToken.None);

        var channel = new FakeProfileStore(new StepRecorder());
        await new ChatAssessor(channel, Builders.Options()).AssessAsync(
            Input("hello", conversation: ChatConversationKind.Audience), Context(), CancellationToken.None);

        // Same author, same channel id, different conversation kind. The target keys must differ or
        // the kind is decoration rather than part of the key.
        var dmTarget = dm.Writes.Single(w => w.Key.Scope == ProfileScopeKind.Relationship).Key;
        var channelTarget = channel.Writes.Single(w => w.Key.Scope == ProfileScopeKind.Relationship).Key;

        Assert.NotEqual(dmTarget.Key, channelTarget.Key);
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

}
