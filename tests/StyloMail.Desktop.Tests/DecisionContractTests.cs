using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// That the wire body binds to the type the decision pane renders.
/// </summary>
/// <remarks>
/// The fixtures in <see cref="Wire"/> are transcribed by hand from the Host's
/// own contracts, and these tests are what makes that transcription checkable.
/// A field renamed on the Host, or a member added to an enum, fails here rather
/// than in front of an operator looking at a pane that quietly shows the wrong
/// thing.
///
/// <para>
/// Read together with <c>Wire.Decision</c>: every assertion below names the
/// literal value the Host sends, so a test that passes is evidence about the
/// contract rather than about this client's opinion of it.
/// </para>
/// </remarks>
public sealed class DecisionContractTests
{
    private static Task<DecisionResponse> ReadDecision(string json)
        => new StyloMailApiClient(
                StubHttpMessageHandler.ReturningJson(json).CreateClient(),
                new TestApiKeyProvider())
            .GetDecisionAsync("asm_0f4d2a");

    /// <summary>
    /// The order is the answer to "why was this held". Reasons arrive ordered
    /// and must stay ordered: the pane renders them in the order policy ranked
    /// them, not in an order this client finds convenient.
    /// </summary>
    [Fact]
    public async Task Reasons_bind_in_the_order_the_host_sent_them()
    {
        var decision = await ReadDecision(Wire.Decision);

        Assert.Equal(
            ["credential_request_high", "link_display_mismatch"],
            decision.Reasons.Select(reason => reason.Code));
    }

    [Fact]
    public async Task A_reason_keeps_the_signal_ids_that_produced_it()
    {
        var decision = await ReadDecision(Wire.Decision);

        var first = decision.Reasons[0];

        Assert.Equal(
            "Message requests credentials and the sender has no trusted history.",
            first.Message);
        Assert.Equal(["sig_cred", "sig_hist"], first.EvidenceSignalIds);
    }

    /// <summary>
    /// Origin and availability are what decide how much weight evidence may
    /// carry, so both have to survive the trip as names rather than as the
    /// ordinals they happen to map to.
    /// </summary>
    [Fact]
    public async Task Evidence_binds_its_origin_and_availability()
    {
        var decision = await ReadDecision(Wire.Decision);

        var history = Assert.Single(decision.Evidence, e => e.SignalId == "sig_hist");

        Assert.Equal(EvidenceOrigin.Behavioural, history.Origin);
        Assert.Equal(EvidenceAvailability.ReducedCoverage, history.Availability);
        Assert.Equal(0.42, history.Value);
        Assert.Equal(3, history.SampleSupport);
        Assert.Equal("sender", history.ObservedScope);
    }

    /// <summary>
    /// The measured fact from the live Jev run on 2026-09-22: a Noul answer
    /// carries a probability and <b>no confidence field at all</b>. A null here
    /// is the documented shape, not an absence a retry might fill, and the pane
    /// must not render it as one.
    /// </summary>
    [Fact]
    public async Task Semantic_evidence_binds_a_null_confidence_as_null()
    {
        var decision = await ReadDecision(Wire.Decision);

        var semantic = Assert.Single(decision.Evidence, e => e.SignalId == "sig_cred");

        Assert.Equal(EvidenceOrigin.Semantic, semantic.Origin);
        Assert.Equal(0.99, semantic.Value);
        Assert.Null(semantic.Confidence);
        Assert.Equal("jev-1.13.0", semantic.SourceVersion);
    }

    /// <summary>
    /// The versions are what make a ledger entry reproducible years later, and
    /// the coverage flags are the qualifier on every number above them.
    /// </summary>
    [Fact]
    public async Task Versions_and_coverage_bind()
    {
        var decision = await ReadDecision(Wire.Decision);

        Assert.Equal("policy-7", decision.Versions.PolicyVersion);
        Assert.Equal("jev-1.13.0", decision.Versions.ClassifierModelVersion);
        Assert.Null(decision.Versions.RegimeId);

        Assert.True(decision.Coverage.BodyParsed);
        Assert.True(decision.Coverage.HtmlTextDisagreement);
        Assert.True(decision.Coverage.ConversationContextMissing);
        Assert.False(decision.Coverage.ContentEncrypted);
    }

    /// <summary>
    /// Cache provenance is the difference between a fresh assessment and one
    /// reused from hours ago, which is the first question an operator asks when
    /// a score looks stale.
    /// </summary>
    [Fact]
    public async Task Cache_provenance_binds()
    {
        var decision = await ReadDecision(Wire.Decision);

        Assert.NotNull(decision.Cache);
        Assert.True(decision.Cache.Hit);
        Assert.False(decision.Cache.Stale);
        Assert.Equal("sha256:6b1f0c", decision.Cache.KeyDigest);
        Assert.Equal("jev-1.13.0", decision.Cache.ModelVersion);
    }

    /// <summary>A cache miss is absent rather than an empty object.</summary>
    [Fact]
    public async Task An_absent_cache_binds_as_null()
    {
        var decision = await ReadDecision(Wire.DecisionWithoutOptionalMembers);

        Assert.Null(decision.Cache);
        Assert.Empty(decision.Reasons);
        Assert.Empty(decision.Recipients);
        Assert.Equal(MailAction.Allow, decision.Action);
    }

    [Fact]
    public async Task Recipient_dispositions_bind_per_recipient()
    {
        var decision = await ReadDecision(Wire.Decision);

        var recipient = Assert.Single(decision.Recipients);

        Assert.Equal("alice@example.test", recipient.Recipient);
        Assert.Equal(MailAction.Quarantine, recipient.Action);
        Assert.Equal(DeliveryState.Quarantined, recipient.DeliveryState);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-23T10:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
            recipient.ReEvaluateBy);
    }

    /// <summary>
    /// Shadow mode forwards while recording what policy would have done. Both
    /// values have to be visible, or the pane cannot tell an operator that a
    /// message they are looking at was allowed but should have been held.
    /// </summary>
    [Fact]
    public async Task A_shadow_decision_binds_both_the_taken_and_the_proposed_action()
    {
        var decision = await ReadDecision(Wire.DecisionInShadowMode);

        Assert.Equal(MailAction.Allow, decision.Action);
        Assert.Equal(MailAction.Quarantine, decision.ProposedActionInShadow);
    }

    /// <summary>
    /// The failure this whole file exists to prevent.
    /// </summary>
    /// <remarks>
    /// If an unknown action bound to the zero member it would arrive as
    /// <c>Allow</c>, and the console would tell an operator that a message was
    /// allowed when the Host had in fact held it. Failing loudly is the only
    /// safe direction, and it is also what makes a console / Host version skew
    /// visible as skew rather than as wrong data.
    /// </remarks>
    [Fact]
    public async Task An_unknown_action_fails_loudly_rather_than_binding_to_a_default()
    {
        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => ReadDecision(Wire.DecisionWithAction("ShadowHold")));

        Assert.Equal(StyloMailApiFailure.UnreadableResponse, exception.Failure);
    }

    /// <summary>Every member of the Host's action vocabulary has to be understood.</summary>
    [Theory]
    [InlineData("Allow", MailAction.Allow)]
    [InlineData("Hold", MailAction.Hold)]
    [InlineData("Quarantine", MailAction.Quarantine)]
    [InlineData("Defer", MailAction.Defer)]
    [InlineData("Reject", MailAction.Reject)]
    public async Task Every_action_the_host_can_send_is_understood(string wireName, MailAction expected)
    {
        var decision = await ReadDecision(Wire.DecisionWithAction(wireName));

        Assert.Equal(expected, decision.Action);
    }
}
