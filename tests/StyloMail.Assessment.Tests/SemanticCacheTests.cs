using StyloMail.Assessment.Semantic;
using StyloMail.Core;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// The cache's job is to remove provider calls and nothing else: not an action, not a check, not a
/// verdict about a sender. These tests are mostly about what must <em>not</em> be reused.
/// </summary>
public sealed class SemanticCacheTests
{
    private static SemanticMailInput Input(MailAnalysisInput message) => new()
    {
        Message = message,
        Dimensions = SemanticDimensions.All,
    };

    private static SemanticCacheOptions CacheOptions(
        double sampleRate = 0,
        TimeSpan? lifetime = null,
        int maxEntries = 128,
        bool serveStale = false) => new()
        {
            ClassifierModelVersion = "jev-1.13.0",
            EntryLifetime = lifetime ?? TimeSpan.FromHours(6),
            MaxEntries = maxEntries,
            ReclassificationSampleRate = sampleRate,
            ServeStaleOnProviderUnavailable = serveStale,
        };

    private static (SemanticCacheClassifier Decorator, RecordingSemanticClassifier Inner, FixedClock Clock, InMemorySemanticCacheStore Store)
        Build(SemanticCacheOptions? options = null)
    {
        var clock = new FixedClock();
        var inner = new RecordingSemanticClassifier(clock);
        var store = new InMemorySemanticCacheStore();
        var decorator = new SemanticCacheClassifier(inner, store, options ?? CacheOptions(), clock);
        return (decorator, inner, clock, store);
    }

    [Fact]
    public async Task CacheHit_SkipsTheProvider()
    {
        var (decorator, inner, clock, _) = Build();
        var input = Input(Builders.Message());

        var first = await decorator.ClassifyAsync(input, clock, CancellationToken.None);
        var second = await decorator.ClassifyAsync(input, clock, CancellationToken.None);

        Assert.Equal(1, inner.CallCount);
        Assert.False(first.Cache.Hit);
        Assert.True(second.Cache.Hit);
        Assert.Equal(first.Cache.KeyDigest, second.Cache.KeyDigest);
        Assert.Equal("jev-1.13.0", second.Cache.ModelVersion);
    }

    [Fact]
    public async Task CacheHit_RetainsTheProviderTimestampRatherThanReDatingIt()
    {
        var (decorator, _, clock, store) = Build();
        var input = Input(Builders.Message());

        var first = await decorator.ClassifyAsync(input, clock, CancellationToken.None);
        var classifiedAt = first.Evidence[0].ObservedAt;

        clock.Advance(TimeSpan.FromMinutes(30));
        var second = await decorator.ClassifyAsync(input, clock, CancellationToken.None);

        Assert.True(second.Cache.Hit);
        Assert.Equal(classifiedAt, second.Evidence[0].ObservedAt);
        Assert.Equal(classifiedAt, second.Cache.CachedAt);

        // Coverage and provenance survive the round trip; a hit that lost them would launder an
        // answer with masked dimensions into a clean-looking one.
        var entry = store.Lookup(second.Cache.KeyDigest, CacheOptions(), clock.GetUtcNow()).Entry;
        Assert.NotNull(entry);
        Assert.Equal(SemanticDimensions.All.Count, entry!.Coverage.Total);
        Assert.Equal("jev-1.13.0", entry.ResolvedModelVersion);
    }

    [Fact]
    public async Task AChangedModelVersionInvalidatesTheEntry()
    {
        var clock = new FixedClock();
        var inner = new RecordingSemanticClassifier(clock);
        var store = new InMemorySemanticCacheStore();
        var input = Input(Builders.Message());

        var before = new SemanticCacheClassifier(inner, store, CacheOptions(), clock);
        await before.ClassifyAsync(input, clock, CancellationToken.None);

        // The same traffic, assessed after the operator moves the pinned model. The key covers the
        // configured model, so nothing already stored can answer for this question.
        var after = new SemanticCacheClassifier(
            inner,
            store,
            new SemanticCacheOptions { ClassifierModelVersion = "jev-1.14.0" },
            clock);

        var result = await after.ClassifyAsync(input, clock, CancellationToken.None);

        Assert.Equal(2, inner.CallCount);
        Assert.False(result.Cache.Hit);
    }

    [Fact]
    public async Task AnEntryResolvedByADifferentModelIsRefusedAndDropped()
    {
        var (decorator, inner, clock, store) = Build();
        var input = Input(Builders.Message());
        var options = CacheOptions();
        var digest = SemanticCacheKey.Digest(input, options);

        store.Store(
            new CachedSemanticAssessment
            {
                KeyDigest = digest,
                Evidence = [],
                Coverage = SemanticCoverage.From([]),
                CachedAt = clock.GetUtcNow(),
                ExpiresAt = clock.GetUtcNow() + TimeSpan.FromHours(1),
                ResolvedModelVersion = "jev-1.12.0",
                QuestionSchemaVersion = options.QuestionSchemaVersion,
                PreprocessingVersion = options.PreprocessingVersion,
                Fingerprint = SecurityBearingFingerprint.Compute(Builders.Message()),
            },
            options,
            clock.GetUtcNow());

        var result = await decorator.ClassifyAsync(input, clock, CancellationToken.None);

        // An alias that moved is the case this guards: the provider answered, but with a model whose
        // thresholds were never tuned here. That is a different answer, not an old one.
        Assert.False(result.Cache.Hit);
        Assert.Equal(1, inner.CallCount);
        Assert.Equal(1, decorator.Statistics.VersionInvalidations);

        // The mismatched entry is gone rather than merely skipped, and what replaced it is the
        // answer this configuration can actually vouch for.
        var stored = store.Lookup(digest, options, clock.GetUtcNow()).Entry;
        Assert.NotNull(stored);
        Assert.Equal("jev-1.13.0", stored!.ResolvedModelVersion);
    }

    [Fact]
    public async Task ConcurrentIdenticalCallsCollapseToOneProviderCall()
    {
        var (decorator, inner, clock, _) = Build();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.Gate = gate;
        var input = Input(Builders.Message());

        var calls = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => decorator.ClassifyAsync(input, clock, CancellationToken.None).AsTask()))
            .ToArray();

        // Wait until exactly one caller is inside the provider, then let it finish. Without
        // single-flight the other seven would be inside it too, which is what the assertion on
        // CallCount below would catch, the count is the contract, not the timing.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (inner.ConcurrentCalls < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        gate.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, inner.CallCount);

        // One provider call, one outcome shared by every caller.
        Assert.Single(results.Select(r => r.Cache.KeyDigest).Distinct(StringComparer.Ordinal));
        Assert.All(results, r => Assert.Equal("jev-1.13.0", r.ResolvedModelVersion));
    }

    [Fact]
    public async Task ANearDuplicateWithAChangedAccountNumberDoesNotReuseTheAssessment()
    {
        var (decorator, inner, clock, _) = Build();

        var original = Input(Builders.Message(
            "Please update the payment details for invoice 4471 to account 12345678."));
        var changed = Input(Builders.Message(
            "Please update the payment details for invoice 4471 to account 87654321."));

        await decorator.ClassifyAsync(original, clock, CancellationToken.None);
        var result = await decorator.ClassifyAsync(changed, clock, CancellationToken.None);

        // The wording is one token different and the effect is different. An exact-key cache gets
        // this right by construction, and the fingerprints are asserted separately because they are
        // what the campaign window uses to draw the same distinction.
        Assert.Equal(2, inner.CallCount);
        Assert.False(result.Cache.Hit);
        Assert.False(SameFingerprint(original.Message, changed.Message));
    }

    [Fact]
    public async Task ANearDuplicateWithAChangedLinkTargetDoesNotReuseTheAssessment()
    {
        var (decorator, inner, clock, _) = Build();

        var original = Input(Builders.MessageWithLink("https://secure-bank.example/login"));
        var changed = Input(Builders.MessageWithLink("https://secure-bank.example.attacker.test/login"));

        await decorator.ClassifyAsync(original, clock, CancellationToken.None);
        var result = await decorator.ClassifyAsync(changed, clock, CancellationToken.None);

        Assert.Equal(2, inner.CallCount);
        Assert.False(result.Cache.Hit);
    }

    [Fact]
    public async Task AnEntryWhoseSecurityBearingFingerprintDisagreesIsRefusedAndDropped()
    {
        var (decorator, inner, clock, store) = Build();
        var input = Input(Builders.Message());
        var options = CacheOptions();
        var digest = SemanticCacheKey.Digest(input, options);

        // A hand-built entry that collides on the key but describes a different message. The key
        // digest covers every field the fingerprint does, so this cannot happen through the front
        // door, the check exists so that if it ever does, the answer is "no" rather than a changed
        // destination inheriting a previous verdict.
        store.Store(
            new CachedSemanticAssessment
            {
                KeyDigest = digest,
                Evidence =
                [
                    new Evidence
                    {
                        SignalId = SemanticDimensions.All[0].Id,
                        Origin = EvidenceOrigin.Semantic,
                        Availability = EvidenceAvailability.Available,
                        Value = 0.9,
                        SourceVersion = "jev-1.13.0",
                        ObservedAt = clock.GetUtcNow(),
                    },
                ],
                Coverage = SemanticCoverage.From([]),
                CachedAt = clock.GetUtcNow(),
                ExpiresAt = clock.GetUtcNow() + TimeSpan.FromHours(1),
                ResolvedModelVersion = "jev-1.13.0",
                QuestionSchemaVersion = options.QuestionSchemaVersion,
                PreprocessingVersion = options.PreprocessingVersion,
                Fingerprint = SecurityBearingFingerprint.Compute(
                    Builders.Message("a completely different message naming account 99999999")),
            },
            options,
            clock.GetUtcNow());

        var result = await decorator.ClassifyAsync(input, clock, CancellationToken.None);

        Assert.Equal(1, inner.CallCount);
        Assert.False(result.Cache.Hit);
        Assert.Equal(1, decorator.Statistics.SecurityBearingRejections);

        // The colliding entry is dropped, and what is stored afterwards describes this message
        // rather than the one it collided with.
        var replacement = store.Lookup(digest, options, clock.GetUtcNow()).Entry;
        Assert.NotNull(replacement);
        Assert.Equal(
            SecurityBearingFingerprint.Compute(Builders.Message()).Digest,
            replacement!.Fingerprint.Digest);
    }

    [Fact]
    public async Task AnUnavailableAnswerIsNeverMemoised()
    {
        var (decorator, inner, clock, store) = Build();
        inner.Unavailable = true;
        var input = Input(Builders.Message());

        var first = await decorator.ClassifyAsync(input, clock, CancellationToken.None);
        var second = await decorator.ClassifyAsync(input, clock, CancellationToken.None);

        // Writing an outage down would turn a provider incident into one that lasts as long as the
        // entry's lifetime, and every message in between would read as a confident hit.
        Assert.Equal(2, inner.CallCount);
        Assert.Equal(0, store.Count);
        Assert.False(first.Cache.Hit);
        Assert.False(second.Cache.Hit);
        Assert.All(second.Evidence, e => Assert.Equal(EvidenceAvailability.Unavailable, e.Availability));
    }

    [Fact]
    public async Task AnOutageDoesNotServeAStaleEntryByDefault()
    {
        var (decorator, inner, clock, _) = Build(CacheOptions(lifetime: TimeSpan.FromMinutes(5)));
        var input = Input(Builders.Message());

        await decorator.ClassifyAsync(input, clock, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(10));
        inner.Unavailable = true;

        var result = await decorator.ClassifyAsync(input, clock, CancellationToken.None);

        // The invariant the wiring exists to uphold: missing evidence stays unavailable, and never
        // becomes an allow. Serving the five-minute-old answer would convert an outage into a
        // confident result with no way for policy to see that anything was wrong.
        Assert.False(result.Cache.Hit);
        Assert.All(result.Evidence, e => Assert.Equal(EvidenceAvailability.Unavailable, e.Availability));
    }

    [Fact]
    public async Task StaleReuseHappensOnlyWhenAnOperatorHasExplicitlyOptedIn()
    {
        var (decorator, inner, clock, _) = Build(
            CacheOptions(lifetime: TimeSpan.FromMinutes(5), serveStale: true));
        var input = Input(Builders.Message());

        var classifiedAt = clock.GetUtcNow();
        await decorator.ClassifyAsync(input, clock, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(10));
        inner.Unavailable = true;

        var result = await decorator.ClassifyAsync(input, clock, CancellationToken.None);

        Assert.True(result.Cache.Hit);
        Assert.True(result.Cache.Stale);

        // The reuse is dated to when the provider actually answered, not to when we fell back to
        // it. That timestamp is the only thing distinguishing an operator's deliberate trade from
        // a silent one.
        Assert.Equal(classifiedAt, result.Cache.CachedAt);
        Assert.Equal(1, decorator.Statistics.StaleServed);
    }

    [Fact]
    public async Task AnExpiredEntryIsRefreshedFromTheProvider()
    {
        var (decorator, inner, clock, _) = Build(CacheOptions(lifetime: TimeSpan.FromMinutes(5)));
        var input = Input(Builders.Message());

        await decorator.ClassifyAsync(input, clock, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(6));
        var result = await decorator.ClassifyAsync(input, clock, CancellationToken.None);

        Assert.Equal(2, inner.CallCount);
        Assert.False(result.Cache.Hit);
        Assert.Equal(1, decorator.Statistics.Expiries);
    }

    [Fact]
    public async Task FullSamplingRefreshesEveryHit()
    {
        var (decorator, inner, clock, _) = Build(CacheOptions(sampleRate: 1.0));
        var input = Input(Builders.Message());

        await decorator.ClassifyAsync(input, clock, CancellationToken.None);
        await decorator.ClassifyAsync(input, clock, CancellationToken.None);
        await decorator.ClassifyAsync(input, clock, CancellationToken.None);

        Assert.Equal(3, inner.CallCount);
        Assert.Equal(2, decorator.Statistics.SampledRefreshes);
    }

    [Fact]
    public async Task TheStoreIsBounded()
    {
        var (decorator, inner, clock, store) = Build(CacheOptions(maxEntries: 2));

        for (var i = 0; i < 5; i++)
        {
            await decorator.ClassifyAsync(
                Input(Builders.Message($"message number {i}")),
                clock,
                CancellationToken.None);
        }

        // A cache with no ceiling is a memory leak fed by whatever traffic arrives, and the traffic
        // that arrives is chosen by the sender.
        Assert.Equal(2, store.Count);
        Assert.Equal(5, inner.CallCount);
    }

    [Fact]
    public async Task TheLeastFrequentlyUsedPolicyEvictsTheColdestEntry()
    {
        // The policy is configurable and this branch was never exercised. It matters because the
        // first version of it was wrong, it aged nothing and could pin a victim forever.
        //
        // The scenario has to make the two policies DISAGREE, or the test proves nothing. My first
        // attempt used a cold key that was also the least recently used, so degrading LFU to LRU
        // left it green. Here "sticky" is the most-read entry and also the oldest, so LFU keeps it
        // and LRU evicts it: the assertions below separate the two.
        var clock = new FixedClock();
        var store = new InMemorySemanticCacheStore();
        var decorator = new SemanticCacheClassifier(
            new RecordingSemanticClassifier(clock),
            store,
            CacheOptions(maxEntries: 2) with { EvictionPolicy = SemanticCacheEvictionPolicy.LeastFrequentlyUsed },
            clock);

        var sticky = Input(Builders.Message("sticky message"));

        await decorator.ClassifyAsync(sticky, clock, CancellationToken.None);                          // store sticky
        await decorator.ClassifyAsync(Input(Builders.Message("other message")), clock, CancellationToken.None); // store other
        await decorator.ClassifyAsync(sticky, clock, CancellationToken.None);                          // sticky: 1 hit
        await decorator.ClassifyAsync(sticky, clock, CancellationToken.None);                          // sticky: 2 hits, now oldest
        await decorator.ClassifyAsync(Input(Builders.Message("other message")), clock, CancellationToken.None); // other: 1 hit, newest

        // Fill the cache: sticky has 2 hits and the oldest timestamp, other has 1 hit and the newest.
        await decorator.ClassifyAsync(Input(Builders.Message("third message")), clock, CancellationToken.None);

        Assert.Equal(2, store.Count);

        // LFU keeps the entry that keeps being asked for; LRU would have evicted it for being old.
        var survived = await decorator.ClassifyAsync(sticky, clock, CancellationToken.None);
        Assert.True(survived.Cache.Hit);
    }

    [Fact]
    public async Task ATenantIsPartOfTheKey()
    {
        var (decorator, inner, clock, _) = Build();

        var first = Input(Builders.Message(envelope: Builders.Envelope(tenantId: "tenant-1")));
        var second = Input(Builders.Message(envelope: Builders.Envelope(tenantId: "tenant-2")));

        var a = await decorator.ClassifyAsync(first, clock, CancellationToken.None);
        var b = await decorator.ClassifyAsync(second, clock, CancellationToken.None);

        Assert.Equal(2, inner.CallCount);
        Assert.NotEqual(a.Cache.KeyDigest, b.Cache.KeyDigest);
    }

    private static bool SameFingerprint(MailAnalysisInput left, MailAnalysisInput right) =>
        string.Equals(
            SecurityBearingFingerprint.Compute(left).Digest,
            SecurityBearingFingerprint.Compute(right).Digest,
            StringComparison.Ordinal);
}
