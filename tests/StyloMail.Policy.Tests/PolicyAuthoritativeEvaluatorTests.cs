using StyloMail.Core;

namespace StyloMail.Policy.Tests;

/// <summary>
/// The authoritative resolution: typed enablement, cache, fallback and required property keys.
/// </summary>
public sealed class PolicyAuthoritativeEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_disabled_tenant_does_no_work()
    {
        var packs = new PackReader(PolicyFixtures.Pack(), throws: false);
        var evaluator = Build(packs, options: new PolicyAuthoritativeOptions());

        var result = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now);

        Assert.Equal(PolicyAuthoritativeKind.Disabled, result.Kind);
        Assert.Equal(0, packs.Calls);
    }

    [Fact]
    public async Task A_healthy_no_pack_is_absent_not_fallback()
    {
        var packs = new PackReader(null, throws: false);
        var evaluator = Build(packs);

        var result = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now);

        Assert.Equal(PolicyAuthoritativeKind.Absent, result.Kind);
        Assert.Null(result.Record);
    }

    [Fact]
    public async Task A_pack_read_failure_without_cache_is_a_bounded_review_fallback()
    {
        var packs = new PackReader(null, throws: true);
        var evaluator = Build(packs);

        var result = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now);

        Assert.Equal(PolicyAuthoritativeKind.Unavailable, result.Kind);
        Assert.NotNull(result.Record);
        Assert.Equal(PolicySource.Fallback, result.Record!.Source);
        Assert.True(result.Record.FallbackAttempted);
        Assert.True(result.Record.PackUnavailable);
        Assert.Null(result.Record.PackId);
        Assert.Null(result.Record.PackVersion);
        Assert.Null(result.Record.EvidenceSnapshotDigest);
        Assert.Equal(Now.AddHours(24), result.FallbackReviewDeadline);
    }

    [Fact]
    public async Task A_pack_and_rule_set_are_cached_coherently_and_go_stale_after_five_minutes()
    {
        var packs = new PackReader(PolicyFixtures.Pack(), throws: false);
        var evaluator = Build(packs);

        var first = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.9)],
            new PolicyPropertyScope("entity-1"), Now);
        Assert.Equal(PolicyAuthoritativeKind.Pack, first.Kind);

        packs.Throw = true;
        var within = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.9)],
            new PolicyPropertyScope("entity-1"), Now.AddMinutes(2));
        Assert.Equal(PolicyAuthoritativeKind.Pack, within.Kind);
        Assert.True(within.Record!.PackFromCache);
        Assert.Equal(TimeSpan.FromMinutes(2), within.Record.CacheAge);

        var stale = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.9)],
            new PolicyPropertyScope("entity-1"), Now.AddMinutes(6));
        Assert.Equal(PolicyAuthoritativeKind.Unavailable, stale.Kind);
    }

    [Fact]
    public async Task A_rule_requiring_a_missing_property_escalates_before_its_band()
    {
        var rule = PolicyFixtures.PaymentRule() with
        {
            RequiredPropertyKeys = [PolicyPropertyKeys.AgeBand],
            Bands = [new RuleBand(0.80, PolicyVerdict.Quarantine, "policy.high")],
        };

        var packs = new PackReader(PolicyFixtures.Pack(), throws: false);
        var evaluator = Build(
            packs,
            ruleSet: PolicyFixtures.RuleSet(rule),
            properties: new PropertyReader(
                facts: [],
                missing: [PolicyPropertyKeys.AgeBand]));

        var result = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant,
            [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.10)],
            new PolicyPropertyScope("entity-1"),
            Now);

        Assert.Equal(PolicyVerdict.Review, result.Evaluation!.Verdict);
        Assert.True(result.Record!.PropertyFactsUnavailable);
        Assert.Contains(PolicyPropertyKeys.AgeBand, result.Record.MissingPropertyKeys);
    }

    [Fact]
    public async Task The_effective_input_is_recorded_with_its_own_digest()
    {
        var packs = new PackReader(PolicyFixtures.Pack(), throws: false);
        var evaluator = Build(packs);

        var result = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant,
            [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.9)],
            new PolicyPropertyScope("entity-1"),
            Now);

        Assert.NotNull(result.Record!.EffectiveInput);
        Assert.Equal(64, result.Record.EffectiveInputDigest!.Length);
        Assert.NotEqual(result.Record.EvidenceSnapshotDigest, result.Record.EffectiveInputDigest);
        Assert.Equal("effective-policy/1", result.Record.EffectiveInput.ResolverVersion);
    }

    private static PolicyAuthoritativeEvaluator Build(
        PackReader packs,
        PolicyRuleSet? ruleSet = null,
        PropertyReader? properties = null,
        PolicyAuthoritativeOptions? options = null) => new(
        packs,
        new RuleReader(ruleSet ?? PolicyFixtures.RuleSet()),
        properties ?? new PropertyReader([], []),
        new BindingReader(),
        options ?? new PolicyAuthoritativeOptions
        {
            Tenants = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PolicyFixtures.Tenant] = "GB",
            },
        });

    private sealed class PackReader : IPolicyPackReader
    {
        private readonly PolicyPack? _pack;

        public PackReader(PolicyPack? pack, bool throws)
        {
            _pack = pack;
            Throw = throws;
        }

        public bool Throw { get; set; }

        public int Calls { get; private set; }

        public Task<PolicyPack?> GetEffectiveAsync(
            string tenantId,
            string jurisdiction,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Throw
                ? Task.FromException<PolicyPack?>(new InvalidOperationException("store unavailable"))
                : Task.FromResult(_pack);
        }

        public Task<PolicyPack?> GetVersionAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult(_pack);

        public Task<PolicyPackSummary?> GetSummaryAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPackSummary?>(null);

        public Task<IReadOnlyList<PolicyPackSummary>> ListAsync(
            string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyPackSummary>>([]);
    }

    private sealed class RuleReader : IPolicyRuleSetReader
    {
        private readonly PolicyRuleSet _ruleSet;

        public RuleReader(PolicyRuleSet ruleSet) => _ruleSet = ruleSet;

        public Task<PolicyRuleSet?> GetAsync(
            string tenantId, string ruleSetId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyRuleSet?>(_ruleSet);

        public Task<PolicyRuleSet?> FindLiveByVersionAsync(
            string tenantId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyRuleSet?>(_ruleSet);
    }

    private sealed class PropertyReader : IPolicyPropertyReader
    {
        private readonly IReadOnlyList<PolicyPropertyFact> _facts;
        private readonly IReadOnlyList<string> _missing;

        public PropertyReader(IReadOnlyList<PolicyPropertyFact> facts, IReadOnlyList<string> missing)
        {
            _facts = facts;
            _missing = missing;
        }

        public Task<PolicyPropertyReadResult> ReadAsync(
            string tenantId,
            PolicyPropertyScope scope,
            IReadOnlyList<string> keys,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PolicyPropertyReadResult
            {
                Facts = _facts,
                MissingKeys = _missing,
                Unavailable = _missing.Count > 0,
            });
    }

    private sealed class BindingReader : IPolicyBindingReader
    {
        public Task<IReadOnlyList<PolicyBinding>> ListForResolutionAsync(
            string tenantId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyBinding>>([]);
    }
}
