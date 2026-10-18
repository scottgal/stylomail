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

    [Fact]
    public async Task An_older_slow_read_cannot_overwrite_a_newer_cached_pack()
    {
        var reader = new RacingPackReader();
        var evaluator = new PolicyAuthoritativeEvaluator(
            reader,
            new RuleReader(PolicyFixtures.RuleSet()),
            new PropertyReader([], []),
            new BindingReader(),
            new PolicyAuthoritativeOptions
            {
                Tenants = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [PolicyFixtures.Tenant] = "GB",
                },
            });

        var older = evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now);
        var newer = evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(1));

        reader.Second.SetResult(PolicyFixtures.Pack() with { Version = "2.0.0" });
        var newerResult = await newer;
        Assert.Equal("2.0.0", newerResult.Record!.PackVersion);

        reader.First.SetResult(PolicyFixtures.Pack() with { Version = "1.0.0" });
        var olderResult = await older;
        Assert.Equal("1.0.0", olderResult.Record!.PackVersion);

        reader.ThrowAfterFirstTwo = true;
        var fromCache = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(2));

        Assert.Equal("2.0.0", fromCache.Record!.PackVersion);
        Assert.True(fromCache.Record.PackFromCache);
    }

    [Fact]
    public async Task A_newer_read_still_installs_when_an_older_read_completed_first()
    {
        var reader = new BothControlledPacks();
        var evaluator = NewControlledEvaluator(reader);

        var older = evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now);
        await reader.OlderStarted.Task;
        var newer = evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(1));
        await reader.NewerStarted.Task;

        reader.Older.SetResult(PolicyFixtures.Pack() with { Version = "1.0.0" });
        Assert.Equal("1.0.0", (await older).Record!.PackVersion);

        reader.Newer.SetResult(PolicyFixtures.Pack() with { Version = "2.0.0" });
        Assert.Equal("2.0.0", (await newer).Record!.PackVersion);

        reader.FailAll = true;
        var cached = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(2));
        Assert.True(cached.Record!.PackFromCache);
        Assert.Equal("2.0.0", cached.Record.PackVersion);
    }

    [Fact]
    public async Task A_delayed_older_absent_does_not_clear_a_newer_installed_pack()
    {
        var reader = new BothControlledPacks();
        var evaluator = NewControlledEvaluator(reader);

        var older = evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now);
        await reader.OlderStarted.Task;
        var newer = evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(1));
        await reader.NewerStarted.Task;

        reader.Newer.SetResult(PolicyFixtures.Pack() with { Version = "2.0.0" });
        Assert.Equal("2.0.0", (await newer).Record!.PackVersion);

        reader.Older.SetResult(null);
        Assert.Equal(PolicyAuthoritativeKind.Absent, (await older).Kind);

        reader.FailAll = true;
        var cached = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(2));
        Assert.True(cached.Record!.PackFromCache);
        Assert.Equal("2.0.0", cached.Record.PackVersion);
    }

    private static PolicyAuthoritativeEvaluator NewControlledEvaluator(IPolicyPackReader packs) => new(
        packs,
        new RuleReader(PolicyFixtures.RuleSet()),
        new PropertyReader([], []),
        new BindingReader(),
        new PolicyAuthoritativeOptions
        {
            Tenants = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PolicyFixtures.Tenant] = "GB",
            },
        });

    [Fact]
    public async Task Two_hundred_fifty_seven_healthy_Absent_tenants_keep_one_fence_each_and_no_live_pack()
    {
        var tenants = Enumerable.Range(0, 257)
            .Select(index => $"tenant-{index}")
            .ToDictionary(tenant => tenant, _ => "GB", StringComparer.Ordinal);
        var evaluator = new PolicyAuthoritativeEvaluator(
            new AbsentPackReader(),
            new RuleReader(PolicyFixtures.RuleSet()),
            new PropertyReader([], []),
            new BindingReader(),
            new PolicyAuthoritativeOptions { Tenants = tenants });

        foreach (var tenant in tenants.Keys)
        {
            var result = await evaluator.EvaluateAsync(
                tenant, [], new PolicyPropertyScope("entity-1"), Now);
            Assert.Equal(PolicyAuthoritativeKind.Absent, result.Kind);
        }

        Assert.Equal(257, CountPrivateDictionary(evaluator, "_fences"));
        Assert.Equal(0, CountPrivateDictionary(evaluator, "_cache"));
    }

    [Fact]
    public async Task Live_pack_values_stay_at_the_256_bound_while_every_fence_survives()
    {
        var tenants = Enumerable.Range(0, 257)
            .Select(index => $"live-tenant-{index}")
            .ToDictionary(tenant => tenant, _ => "GB", StringComparer.Ordinal);
        var evaluator = new PolicyAuthoritativeEvaluator(
            new PerTenantPackReader(),
            new RuleReader(PolicyFixtures.RuleSet()),
            new PropertyReader([], []),
            new BindingReader(),
            new PolicyAuthoritativeOptions { Tenants = tenants });

        foreach (var tenant in tenants.Keys)
        {
            var result = await evaluator.EvaluateAsync(
                tenant, [], new PolicyPropertyScope("entity-1"), Now);
            Assert.Equal(PolicyAuthoritativeKind.Pack, result.Kind);
        }

        Assert.Equal(257, CountPrivateDictionary(evaluator, "_fences"));
        Assert.Equal(256, CountPrivateDictionary(evaluator, "_cache"));
    }

    [Fact]
    public async Task Repeated_assessment_of_one_tenant_does_not_grow_the_live_cache_or_fences()
    {
        var evaluator = new PolicyAuthoritativeEvaluator(
            new PerTenantPackReader(),
            new RuleReader(PolicyFixtures.RuleSet()),
            new PropertyReader([], []),
            new BindingReader(),
            new PolicyAuthoritativeOptions
            {
                Tenants = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [PolicyFixtures.Tenant] = "GB",
                },
            });

        for (var index = 0; index < 10; index++)
        {
            var result = await evaluator.EvaluateAsync(
                PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(index));
            Assert.Equal(PolicyAuthoritativeKind.Pack, result.Kind);
        }

        Assert.Equal(1, CountPrivateDictionary(evaluator, "_fences"));
        Assert.Equal(1, CountPrivateDictionary(evaluator, "_cache"));
    }

    [Fact]
    public void A_restart_with_a_removed_tenant_drops_its_fence()
    {
        var first = new PolicyAuthoritativeEvaluator(
            new AbsentPackReader(),
            new RuleReader(PolicyFixtures.RuleSet()),
            new PropertyReader([], []),
            new BindingReader(),
            new PolicyAuthoritativeOptions
            {
                Tenants = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["tenant-old"] = "GB",
                },
            });
        Assert.Equal(1, CountPrivateDictionary(first, "_fences"));

        var restarted = new PolicyAuthoritativeEvaluator(
            new AbsentPackReader(),
            new RuleReader(PolicyFixtures.RuleSet()),
            new PropertyReader([], []),
            new BindingReader(),
            new PolicyAuthoritativeOptions
            {
                Tenants = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["tenant-new"] = "GB",
                },
            });

        Assert.Equal(1, CountPrivateDictionary(restarted, "_fences"));
        Assert.DoesNotContain("tenant-old|GB", FenceKeys(restarted));
    }

    [Fact]
    public void Exceeding_the_configured_tenant_cap_refuses_enablement_with_a_named_reason()
    {
        var tenants = Enumerable.Range(0, PolicyAuthoritativeOptions.MaxConfiguredTenants + 1)
            .ToDictionary(index => $"cap-tenant-{index}", _ => "GB", StringComparer.Ordinal);

        var error = Assert.Throws<ArgumentException>(() => new PolicyAuthoritativeEvaluator(
            new AbsentPackReader(),
            new RuleReader(PolicyFixtures.RuleSet()),
            new PropertyReader([], []),
            new BindingReader(),
            new PolicyAuthoritativeOptions { Tenants = tenants }));

        Assert.Contains(PolicyAuthoritativeOptions.TenantCapExceeded, error.Message);
    }

    private static int CountPrivateDictionary(object evaluator, string fieldName)
    {
        var field = typeof(PolicyAuthoritativeEvaluator).GetField(
            fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dictionary = field.GetValue(evaluator)!;
        return (int)dictionary.GetType().GetProperty("Count")!.GetValue(dictionary)!;
    }

    private static IEnumerable<string> FenceKeys(object evaluator)
    {
        var field = typeof(PolicyAuthoritativeEvaluator).GetField(
            "_fences",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dictionary = field.GetValue(evaluator)!;
        return (IEnumerable<string>)dictionary.GetType().GetProperty("Keys")!.GetValue(dictionary)!;
    }

    private sealed class AbsentPackReader : IPolicyPackReader
    {
        public Task<PolicyPack?> GetEffectiveAsync(
            string tenantId,
            string jurisdiction,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult<PolicyPack?>(null);

        public Task<PolicyPack?> GetVersionAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPack?>(null);

        public Task<PolicyPackSummary?> GetSummaryAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPackSummary?>(null);

        public Task<IReadOnlyList<PolicyPackSummary>> ListAsync(
            string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyPackSummary>>([]);
    }

    private sealed class PerTenantPackReader : IPolicyPackReader
    {
        public Task<PolicyPack?> GetEffectiveAsync(
            string tenantId,
            string jurisdiction,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPack?>(
                PolicyFixtures.Pack(tenant: tenantId, levels: PolicyFixtures.DefaultLevels()));

        public Task<PolicyPack?> GetVersionAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPack?>(null);

        public Task<PolicyPackSummary?> GetSummaryAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPackSummary?>(null);

        public Task<IReadOnlyList<PolicyPackSummary>> ListAsync(
            string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyPackSummary>>([]);
    }

    [Fact]
    public async Task A_pack_becoming_effective_after_a_healthy_Absent_installs_and_clears_absence()
    {
        var reader = new SwitchablePackReader { Absent = true, Version = "1.0.0" };
        var evaluator = NewSwitchableEvaluator(reader);

        var absent = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now);
        Assert.Equal(PolicyAuthoritativeKind.Absent, absent.Kind);

        reader.Absent = false;
        var pack = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(1));

        Assert.Equal(PolicyAuthoritativeKind.Pack, pack.Kind);
        Assert.Equal("1.0.0", pack.Record!.PackVersion);
        Assert.Equal(1, CountPrivateDictionary(evaluator, "_cache"));
    }

    [Fact]
    public async Task A_current_read_after_Invalidate_installs_the_new_pack()
    {
        var reader = new SwitchablePackReader { Version = "1.0.0" };
        var evaluator = NewSwitchableEvaluator(reader);

        var first = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now);
        Assert.Equal("1.0.0", first.Record!.PackVersion);

        evaluator.Invalidate(PolicyFixtures.Tenant);
        reader.Version = "2.0.0";
        var second = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(1));

        Assert.Equal(PolicyAuthoritativeKind.Pack, second.Kind);
        Assert.Equal("2.0.0", second.Record!.PackVersion);
        Assert.Equal(1, CountPrivateDictionary(evaluator, "_cache"));
    }

    private static PolicyAuthoritativeEvaluator NewSwitchableEvaluator(IPolicyPackReader packs) => new(
        packs,
        new RuleReader(PolicyFixtures.RuleSet()),
        new PropertyReader([], []),
        new BindingReader(),
        new PolicyAuthoritativeOptions
        {
            Tenants = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PolicyFixtures.Tenant] = "GB",
            },
        });

    private sealed class SwitchablePackReader : IPolicyPackReader
    {
        public bool Absent { get; set; }

        public string Version { get; set; } = "1.0.0";

        public Task<PolicyPack?> GetEffectiveAsync(
            string tenantId,
            string jurisdiction,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPack?>(
                Absent ? null : PolicyFixtures.Pack(version: Version, tenant: tenantId));

        public Task<PolicyPack?> GetVersionAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPack?>(null);

        public Task<PolicyPackSummary?> GetSummaryAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPackSummary?>(null);

        public Task<IReadOnlyList<PolicyPackSummary>> ListAsync(
            string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyPackSummary>>([]);
    }

    private sealed class BothControlledPacks : IPolicyPackReader
    {
        private int _calls;

        public bool FailAll { get; set; }

        public TaskCompletionSource OlderStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource NewerStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<PolicyPack?> Older { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<PolicyPack?> Newer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PolicyPack?> GetEffectiveAsync(
            string tenantId,
            string jurisdiction,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            if (FailAll)
            {
                return Task.FromException<PolicyPack?>(new InvalidOperationException("controlled outage"));
            }

            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                OlderStarted.SetResult();
                return Older.Task;
            }

            if (call == 2)
            {
                NewerStarted.SetResult();
                return Newer.Task;
            }

            return Task.FromException<PolicyPack?>(new InvalidOperationException("controlled outage"));
        }

        public Task<PolicyPack?> GetVersionAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPack?>(null);

        public Task<PolicyPackSummary?> GetSummaryAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPackSummary?>(null);

        public Task<IReadOnlyList<PolicyPackSummary>> ListAsync(
            string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyPackSummary>>([]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cache_publication_is_atomic_with_the_generation_fence(bool explicitInvalidation)
    {
        var reader = new ImmediateVersions();
        var evaluator = NewControlledEvaluator(reader);
        using var comparer = new PauseFirstHash();
        var field = typeof(PolicyAuthoritativeEvaluator).GetField(
            "_cache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.NotNull(field);
        field.SetValue(evaluator, Activator.CreateInstance(field.FieldType, [comparer]));

        var old = Task.Run(() => evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now));
        Task<PolicyAuthoritativeResult>? newer = null;
        Task? invalidate = null;
        try
        {
            await comparer.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (explicitInvalidation)
            {
                invalidate = Task.Run(() => evaluator.Invalidate(PolicyFixtures.Tenant));
            }
            else
            {
                newer = Task.Run(() => evaluator.EvaluateAsync(
                    PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(1)));
            }

        }
        finally
        {
            // The contender is allowed to block on the publication gate until this read finishes,
            // so release before awaiting it.
            comparer.Release.Set();
        }

        await old.WaitAsync(TimeSpan.FromSeconds(15));
        if (invalidate is not null)
        {
            await invalidate.WaitAsync(TimeSpan.FromSeconds(15));
        }
        else
        {
            Assert.Equal("2.0.0", (await newer!.WaitAsync(TimeSpan.FromSeconds(15))).Record!.PackVersion);
        }

        reader.Fail = true;
        var cached = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(2));

        Assert.True(
            explicitInvalidation
                ? cached.Kind == PolicyAuthoritativeKind.Unavailable
                : cached.Kind == PolicyAuthoritativeKind.Pack && cached.Record?.PackVersion == "2.0.0",
            $"explicitInvalidation={explicitInvalidation}; kind={cached.Kind}; "
                + $"version={cached.Record?.PackVersion ?? "NULL"}");
    }

    [Fact]
    public async Task Cache_publication_after_a_healthy_Absent_clear_does_not_resurrect_the_old_pack()
    {
        var reader = new ImmediateVersions();
        var evaluator = NewControlledEvaluator(reader);
        using var comparer = new PauseFirstHash();
        var field = typeof(PolicyAuthoritativeEvaluator).GetField(
            "_cache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(evaluator, Activator.CreateInstance(field.FieldType, [comparer]));

        var old = Task.Run(() => evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now));
        Task<PolicyAuthoritativeResult>? absentTask = null;
        try
        {
            await comparer.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            reader.ReturnAbsent = true;
            absentTask = Task.Run(() => evaluator.EvaluateAsync(
                PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(1)));
        }
        finally
        {
            comparer.Release.Set();
        }

        await old.WaitAsync(TimeSpan.FromSeconds(15));
        var absent = await absentTask!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(PolicyAuthoritativeKind.Absent, absent.Kind);
        reader.ReturnAbsent = false;
        reader.Fail = true;

        var cached = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(2));

        Assert.Equal(PolicyAuthoritativeKind.Unavailable, cached.Kind);
    }

    [Fact]
    public async Task Older_absence_must_not_advance_the_fence_after_a_newer_successful_read()
    {
        var evaluator = NewControlledEvaluator(new FirstAbsentThenPack());
        using var comparer = new PauseThirdFenceHash();
        var field = typeof(PolicyAuthoritativeEvaluator).GetField(
            "_fences",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var original = (System.Collections.IDictionary)field.GetValue(evaluator)!;
        var instrumented = (System.Collections.IDictionary)Activator.CreateInstance(
            field.FieldType,
            [comparer])!;
        var iterator = original.GetEnumerator();
        while (iterator.MoveNext())
        {
            instrumented.Add(iterator.Key, iterator.Value);
        }

        comparer.Reset();
        field.SetValue(evaluator, instrumented);

        var old = Task.Run(() => evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now));
        try
        {
            await comparer.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var newer = await evaluator.EvaluateAsync(
                PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(1));
            Assert.Equal(PolicyAuthoritativeKind.Pack, newer.Kind);
            Assert.Equal("2.0.0", newer.Record!.PackVersion);
        }
        finally
        {
            comparer.Release.Set();
        }

        Assert.Equal(PolicyAuthoritativeKind.Absent, (await old.WaitAsync(TimeSpan.FromSeconds(15))).Kind);
        var cached = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now.AddSeconds(2));
        Assert.True(
            cached.Kind == PolicyAuthoritativeKind.Pack && cached.Record?.PackVersion == "2.0.0",
            $"afterDelayedAbsent kind={cached.Kind}; version={cached.Record?.PackVersion ?? "NULL"}");
    }

    [Fact]
    public async Task An_empty_rule_set_is_an_unavailable_fallback_not_a_pack_decision()
    {
        var evaluator = new PolicyAuthoritativeEvaluator(
            new PackReader(PolicyFixtures.Pack(), throws: false),
            new RuleReader(PolicyFixtures.RuleSet() with { Rules = [] }),
            new PropertyReader([], []),
            new BindingReader(),
            new PolicyAuthoritativeOptions
            {
                Tenants = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [PolicyFixtures.Tenant] = "GB",
                },
            });

        var result = await evaluator.EvaluateAsync(
            PolicyFixtures.Tenant, [], new PolicyPropertyScope("entity-1"), Now);

        Assert.Equal(PolicyAuthoritativeKind.Unavailable, result.Kind);
        Assert.NotNull(result.Record);
        Assert.Equal(PolicySource.Fallback, result.Record!.Source);
        Assert.Null(result.Record.DecidingRuleId);
    }

    private sealed class ImmediateVersions : IPolicyPackReader
    {
        private int _calls;

        public bool Fail { get; set; }

        public bool ReturnAbsent { get; set; }

        public Task<PolicyPack?> GetEffectiveAsync(
            string tenantId,
            string jurisdiction,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                return Task.FromException<PolicyPack?>(new InvalidOperationException("controlled outage"));
            }

            if (ReturnAbsent)
            {
                return Task.FromResult<PolicyPack?>(null);
            }

            return Task.FromResult<PolicyPack?>(
                PolicyFixtures.Pack(version: Interlocked.Increment(ref _calls) == 1 ? "1.0.0" : "2.0.0"));
        }

        public Task<PolicyPack?> GetVersionAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPack?>(null);

        public Task<PolicyPackSummary?> GetSummaryAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPackSummary?>(null);

        public Task<IReadOnlyList<PolicyPackSummary>> ListAsync(
            string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyPackSummary>>([]);
    }

    private sealed class PauseThirdFenceHash : IEqualityComparer<string>, IDisposable
    {
        private int _calls;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new(false);

        public void Reset() => Interlocked.Exchange(ref _calls, 0);

        public bool Equals(string? x, string? y) => StringComparer.Ordinal.Equals(x, y);

        public int GetHashCode(string key)
        {
            if (Interlocked.Increment(ref _calls) == 3)
            {
                Started.SetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException("fence gate timed out");
                }
            }

            return StringComparer.Ordinal.GetHashCode(key);
        }

        public void Dispose() => Release.Dispose();
    }

    private sealed class FirstAbsentThenPack : IPolicyPackReader
    {
        private int _calls;

        public Task<PolicyPack?> GetEffectiveAsync(
            string tenantId,
            string jurisdiction,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _calls) switch
            {
                1 => Task.FromResult<PolicyPack?>(null),
                2 => Task.FromResult<PolicyPack?>(PolicyFixtures.Pack(version: "2.0.0")),
                _ => Task.FromException<PolicyPack?>(new InvalidOperationException("controlled outage")),
            };

        public Task<PolicyPack?> GetVersionAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPack?>(null);

        public Task<PolicyPackSummary?> GetSummaryAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPackSummary?>(null);

        public Task<IReadOnlyList<PolicyPackSummary>> ListAsync(
            string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyPackSummary>>([]);
    }

    private sealed class PauseFirstHash : IEqualityComparer<string>, IDisposable
    {
        private int _calls;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new(false);

        public bool Equals(string? x, string? y) => StringComparer.Ordinal.Equals(x, y);

        public int GetHashCode(string key)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.SetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException("instrument gate timed out");
                }
            }

            return StringComparer.Ordinal.GetHashCode(key);
        }

        public void Dispose() => Release.Dispose();
    }

    [Fact]
    public async Task Effective_binding_reports_direct_inherited_and_scope_matched_bindings()
    {
        var company = PolicyFixtures.Binding(
            action: BindingAction.LowerTolerance,
            categoryId: PolicyCategories.PaymentRedirection,
            targetLevel: ToleranceLevel.Observe,
            approvedBy: "reviewer-b") with
        {
            Id = "binding-company",
            ScopeKind = BindingScope.Company,
            ScopeValue = "company-1",
        };
        var entity = PolicyFixtures.Binding(
            action: BindingAction.RaiseTolerance,
            categoryId: PolicyCategories.CredentialRequest,
            targetLevel: ToleranceLevel.Strict) with
        {
            Id = "binding-entity",
        };
        var other = entity with { Id = "binding-other", ScopeValue = "other-entity" };

        var evaluator = NewBindingEvaluator(
            new PackReader(PolicyFixtures.Pack(), throws: false),
            new SuppliedBindings([company, entity, other]));

        var descriptor = await evaluator.ResolveEffectiveBindingAsync(
            PolicyFixtures.Tenant,
            new PolicyPropertyScope("entity-1", "example.com", "company-1"),
            Now);

        Assert.Equal(EffectiveBindingState.Bound, descriptor.State);
        Assert.Equal(PolicyFixtures.PackId, descriptor.PackId);
        Assert.Equal("1.0.0", descriptor.PackVersion);
        Assert.Contains("entity:entity-1", descriptor.AddressedChain);
        Assert.Contains("company:company-1", descriptor.AddressedChain);
        Assert.Equal(2, descriptor.Bindings.Count);

        var inherited = Assert.Single(
            descriptor.Bindings,
            binding => binding.BindingId == "binding-company");
        Assert.Equal("inherited", inherited.Source);
        Assert.Equal(BindingScope.Company, inherited.ScopeKind);
        Assert.Equal(ToleranceLevel.Observe, inherited.TargetLevel);
        Assert.True(inherited.Selected);
        Assert.Equal("operator-a", inherited.Author);
        Assert.Equal(Now.AddDays(20), inherited.ReviewBy);

        var direct = Assert.Single(
            descriptor.Bindings,
            binding => binding.BindingId == "binding-entity");
        Assert.Equal("direct", direct.Source);
        Assert.Equal(BindingScope.Entity, direct.ScopeKind);
        Assert.Equal(ToleranceLevel.Strict, direct.TargetLevel);
        Assert.True(direct.Selected);
        Assert.DoesNotContain(descriptor.Bindings, binding => binding.BindingId == "binding-other");
    }

    [Fact]
    public async Task Effective_binding_reports_an_expired_HoldForReview_as_withheld()
    {
        var expired = PolicyFixtures.Binding(
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

        var evaluator = NewBindingEvaluator(
            new PackReader(PolicyFixtures.Pack(), throws: false),
            new SuppliedBindings([expired]));

        var descriptor = await evaluator.ResolveEffectiveBindingAsync(
            PolicyFixtures.Tenant,
            new PolicyPropertyScope("entity-1"),
            Now);

        var binding = Assert.Single(descriptor.Bindings);
        Assert.Equal(EffectiveBindingState.Bound, descriptor.State);
        Assert.True(binding.Selected);
        Assert.True(binding.Expired);
        Assert.True(binding.Withheld);
        Assert.Equal(BindingOnExpiry.HoldForReview, binding.OnExpiry);
    }

    [Fact]
    public async Task Effective_binding_reports_a_rule_set_outage_as_unavailable()
    {
        var evaluator = NewBindingEvaluator(
            new PackReader(PolicyFixtures.Pack(), throws: false),
            new SuppliedBindings([]),
            new ThrowingRuleReader());

        var descriptor = await evaluator.ResolveEffectiveBindingAsync(
            PolicyFixtures.Tenant, new PolicyPropertyScope("entity-1"), Now);

        Assert.Equal(EffectiveBindingState.Unavailable, descriptor.State);
        Assert.Equal("policy.rule_set_unavailable", descriptor.Reason);
        Assert.Equal(PolicyFixtures.PackId, descriptor.PackId);
        Assert.Equal("1.0.0", descriptor.PackVersion);
        Assert.Empty(descriptor.Bindings);
    }

    [Fact]
    public async Task Effective_binding_reports_a_pack_store_outage_as_unavailable()
    {
        var evaluator = NewBindingEvaluator(
            new PackReader(null, throws: true),
            new SuppliedBindings([]));

        var descriptor = await evaluator.ResolveEffectiveBindingAsync(
            PolicyFixtures.Tenant, new PolicyPropertyScope("entity-1"), Now);

        Assert.Equal(EffectiveBindingState.Unavailable, descriptor.State);
        Assert.Equal("policy.pack_store_unavailable", descriptor.Reason);
        Assert.Empty(descriptor.Bindings);
    }

    [Fact]
    public async Task Effective_binding_reports_no_pack_and_no_binding_states_clearly()
    {
        var noPack = await NewBindingEvaluator(new PackReader(null, throws: false), new SuppliedBindings([]))
            .ResolveEffectiveBindingAsync(
                PolicyFixtures.Tenant, new PolicyPropertyScope("entity-1"), Now);

        Assert.Equal(EffectiveBindingState.NoPack, noPack.State);
        Assert.Equal("no_effective_pack", noPack.Reason);
        Assert.Empty(noPack.Bindings);

        var noBinding = await NewBindingEvaluator(
                new PackReader(PolicyFixtures.Pack(), throws: false),
                new SuppliedBindings([]))
            .ResolveEffectiveBindingAsync(
                PolicyFixtures.Tenant, new PolicyPropertyScope("entity-1"), Now);

        Assert.Equal(EffectiveBindingState.NoBinding, noBinding.State);
        Assert.Equal("no_binding_for_scope", noBinding.Reason);
        Assert.Equal(PolicyFixtures.PackId, noBinding.PackId);
        Assert.Empty(noBinding.Bindings);
    }

    private static PolicyAuthoritativeEvaluator NewBindingEvaluator(
        IPolicyPackReader packs,
        IPolicyBindingReader bindings,
        IPolicyRuleSetReader? rules = null) => new(
        packs,
        rules ?? new RuleReader(PolicyFixtures.RuleSet()),
        new PropertyReader([], []),
        bindings,
        new PolicyAuthoritativeOptions
        {
            Tenants = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PolicyFixtures.Tenant] = "GB",
            },
        });

    private sealed class ThrowingRuleReader : IPolicyRuleSetReader
    {
        public Task<PolicyRuleSet?> GetAsync(
            string tenantId, string ruleSetId, string version, CancellationToken cancellationToken = default) =>
            Task.FromException<PolicyRuleSet?>(new InvalidOperationException("rule store unavailable"));

        public Task<PolicyRuleSet?> FindLiveByVersionAsync(
            string tenantId, string version, CancellationToken cancellationToken = default) =>
            Task.FromException<PolicyRuleSet?>(new InvalidOperationException("rule store unavailable"));
    }

    private sealed class SuppliedBindings : IPolicyBindingReader
    {
        private readonly IReadOnlyList<PolicyBinding> _bindings;

        public SuppliedBindings(IReadOnlyList<PolicyBinding> bindings) => _bindings = bindings;

        public Task<IReadOnlyList<PolicyBinding>> ListForResolutionAsync(
            string tenantId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_bindings);
    }

    private sealed class RacingPackReader : IPolicyPackReader
    {
        public TaskCompletionSource<PolicyPack?> First { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<PolicyPack?> Second { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ThrowAfterFirstTwo { get; set; }

        private int _calls;

        public Task<PolicyPack?> GetEffectiveAsync(
            string tenantId,
            string jurisdiction,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1) return First.Task;
            if (call == 2) return Second.Task;
            if (ThrowAfterFirstTwo)
            {
                return Task.FromException<PolicyPack?>(new InvalidOperationException("store unavailable"));
            }

            return Task.FromResult<PolicyPack?>(null);
        }

        public Task<PolicyPack?> GetVersionAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPack?>(null);

        public Task<PolicyPackSummary?> GetSummaryAsync(
            string tenantId, string packId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PolicyPackSummary?>(null);

        public Task<IReadOnlyList<PolicyPackSummary>> ListAsync(
            string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyPackSummary>>([]);
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
