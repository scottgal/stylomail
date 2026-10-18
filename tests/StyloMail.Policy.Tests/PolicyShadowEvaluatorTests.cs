using StyloMail.Core;

namespace StyloMail.Policy.Tests;

/// <summary>
/// The Phase 2 shadow: a pack evaluation recorded beside the legacy decision, never an action.
/// </summary>
public sealed class PolicyShadowEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_shadow_record_carries_the_packs_rule_version_threshold_and_digest()
    {
        var shadow = Shadow(
            pack: PolicyFixtures.Pack(),
            ruleSet: PolicyFixtures.RuleSet());

        var record = await shadow.EvaluateAsync(
            PolicyFixtures.Tenant,
            [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90)],
            Now);

        Assert.NotNull(record);
        Assert.True(record!.Shadow);
        Assert.Equal(PolicyVerdict.Quarantine, record.Verdict);
        Assert.Equal(PolicyFixtures.PackId, record.PackId);
        Assert.Equal("1.0.0", record.PackVersion);
        Assert.Equal(PolicyFixtures.RuleSetVersion, record.RuleSetVersion);
        Assert.Equal("rule-payment-redirection", record.DecidingRuleId);
        Assert.Equal(1, record.DecidingRuleVersion);
        Assert.Equal(0.80, record.DecidingThreshold);
        Assert.Equal("policy.payment_redirection_quarantine", record.DecidingReasonCode);
        Assert.Equal(64, record.EvidenceSnapshotDigest!.Length);
        Assert.Empty(record.BindingIds);
        Assert.Empty(record.PropertyFacts);
        Assert.Equal(Now, record.EvaluatedAt);
    }

    [Fact]
    public async Task No_active_pack_produces_no_record()
    {
        var shadow = Shadow(pack: null, ruleSet: null);

        var record = await shadow.EvaluateAsync(
            PolicyFixtures.Tenant,
            [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90)],
            Now);

        Assert.Null(record);
    }

    [Fact]
    public async Task A_missing_rule_set_is_a_review_record_not_a_throw()
    {
        var shadow = Shadow(pack: PolicyFixtures.Pack(), ruleSet: null);

        var record = await shadow.EvaluateAsync(
            PolicyFixtures.Tenant,
            [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90)],
            Now);

        Assert.NotNull(record);
        Assert.True(record!.Shadow);
        Assert.Equal(PolicyVerdict.Review, record.Verdict);
        Assert.True(record.RequiresHumanReview);
        Assert.Equal("policy.rule_set_not_live", record.DecidingReasonCode);
        Assert.Null(record.EvidenceSnapshotDigest);
    }

    [Fact]
    public async Task A_store_failure_is_a_review_record_not_a_throw()
    {
        var shadow = Shadow(
            pack: PolicyFixtures.Pack(),
            ruleSet: PolicyFixtures.RuleSet(),
            packReaderThrows: true);

        var record = await shadow.EvaluateAsync(
            PolicyFixtures.Tenant,
            [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90)],
            Now);

        Assert.NotNull(record);
        Assert.Equal(PolicyVerdict.Review, record!.Verdict);
        Assert.Equal("policy.pack_store_unavailable", record.DecidingReasonCode);
        Assert.True(record.Shadow);
    }

    [Fact]
    public async Task A_disabled_shadow_reads_nothing()
    {
        var packs = new FakePackReader(PolicyFixtures.Pack(), throws: false);
        var shadow = new PolicyShadowEvaluator(
            packs,
            new FakeRuleSetReader(PolicyFixtures.RuleSet(), throws: false),
            new PolicyShadowOptions { Enabled = false, Jurisdiction = "GB" });

        var record = await shadow.EvaluateAsync(PolicyFixtures.Tenant, [], Now);

        Assert.Null(record);
        Assert.Equal(0, packs.Reads);
    }

    [Fact]
    public async Task A_blank_jurisdiction_reads_nothing()
    {
        var packs = new FakePackReader(PolicyFixtures.Pack(), throws: false);
        var shadow = new PolicyShadowEvaluator(
            packs,
            new FakeRuleSetReader(PolicyFixtures.RuleSet(), throws: false),
            new PolicyShadowOptions { Enabled = true, Jurisdiction = " " });

        var record = await shadow.EvaluateAsync(PolicyFixtures.Tenant, [], Now);

        Assert.Null(record);
        Assert.Equal(0, packs.Reads);
    }

    private static PolicyShadowEvaluator Shadow(
        PolicyPack? pack,
        PolicyRuleSet? ruleSet,
        bool packReaderThrows = false) => new(
        new FakePackReader(pack, packReaderThrows),
        new FakeRuleSetReader(ruleSet, throws: false),
        new PolicyShadowOptions { Enabled = true, Jurisdiction = "GB" },
        new DeterministicPolicyEvaluator(new PolicyFixtures.FixedTimeProvider()));

    private sealed class FakePackReader : IPolicyPackReader
    {
        private readonly PolicyPack? _pack;
        private readonly bool _throws;

        public FakePackReader(PolicyPack? pack, bool throws)
        {
            _pack = pack;
            _throws = throws;
        }

        public int Reads { get; private set; }

        public Task<PolicyPack?> GetEffectiveAsync(
            string tenantId,
            string jurisdiction,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Reads++;

            return _throws
                ? Task.FromException<PolicyPack?>(new InvalidOperationException("store unavailable"))
                : Task.FromResult(_pack);
        }

        public Task<PolicyPack?> GetVersionAsync(
            string tenantId,
            string packId,
            string version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_pack);

        public Task<PolicyPackSummary?> GetSummaryAsync(
            string tenantId,
            string packId,
            string version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPackSummary?>(null);

        public Task<IReadOnlyList<PolicyPackSummary>> ListAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyPackSummary>>([]);
    }

    private sealed class FakeRuleSetReader : IPolicyRuleSetReader
    {
        private readonly PolicyRuleSet? _ruleSet;
        private readonly bool _throws;

        public FakeRuleSetReader(PolicyRuleSet? ruleSet, bool throws)
        {
            _ruleSet = ruleSet;
            _throws = throws;
        }

        public Task<PolicyRuleSet?> GetAsync(
            string tenantId,
            string ruleSetId,
            string version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_ruleSet);

        public Task<PolicyRuleSet?> FindLiveByVersionAsync(
            string tenantId,
            string version,
            CancellationToken cancellationToken = default) =>
            _throws
                ? Task.FromException<PolicyRuleSet?>(new InvalidOperationException("store unavailable"))
                : Task.FromResult(_ruleSet);
    }
}
