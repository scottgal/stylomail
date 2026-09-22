using System.Reflection;
using StyloMail.Assessment.Campaign;
using StyloMail.Assessment.Learning;
using StyloMail.Assessment.Semantic;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// Tripwires for the objects a host shares across threads.
/// </summary>
/// <remarks>
/// A host registers one assessor and calls it from whatever thread a message arrives on. That is
/// safe today and nothing about the type says so, which is the problem: a field added next month —
/// a reusable buffer, a cached list, a "last decision" for logging — works perfectly under a single
/// sender and corrupts results under load, and the corruption surfaces as a message-handling bug
/// somewhere else entirely.
///
/// <para>
/// So the design decision is asserted rather than assumed. These fail with an explanation that says
/// what to do about it, because the useful thing a tripwire produces is not a red build but a
/// sentence telling the next person which choice they are making.
/// </para>
/// </remarks>
public sealed class SharedStateTests
{
    [Theory]
    [InlineData(typeof(MailAssessor))]
    [InlineData(typeof(SemanticCacheClassifier))]
    [InlineData(typeof(RecentCampaignWindow))]
    [InlineData(typeof(CampaignNearDuplicateDetector))]
    [InlineData(typeof(ProfileCoordinator))]
    [InlineData(typeof(TrustedLearningGate))]
    [InlineData(typeof(InMemoryRawMessageSource))]
    // Deliberately not listed: InMemorySemanticCacheStore and SendingQuotaLedger. Both exist to be
    // mutated, so "no reassignable state" is the wrong question for them — the right one is whether
    // their mutations are serialised, and that is answered behaviourally by
    // TheCacheStoreIsSafeUnderConcurrentWriters rather than by reflection. A tripwire applied to a
    // type it cannot describe would only teach the next person to delete the tripwire.
    public void SharedTypesCarryNoReassignableInstanceState(Type type)
    {
        var mutable = type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => !field.IsInitOnly && !field.IsLiteral)
            // Compiler-generated backing fields for get-only auto-properties are readonly and are
            // exactly what "no reassignable state" is supposed to permit.
            .ToList();

        Assert.True(
            mutable.Count == 0,
            $"{type.Name} holds reassignable instance state "
            + $"({string.Join(", ", mutable.Select(f => f.Name))}). It is registered as a singleton "
            + "and called from every thread a message arrives on. Either hold the state somewhere "
            + "that is designed for concurrent access — a store, a lock, an Interlocked counter — or "
            + "stop sharing the instance and register it per scope.");
    }

    [Fact]
    public void TheCampaignWindowIsBoundedRatherThanGrowingWithTraffic()
    {
        var window = new RecentCampaignWindow(capacityPerTenant: 8);
        var now = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        var vector = StyloMail.Adaptive.Profiles.DimensionVector.Create();
        var fingerprint = SecurityBearingFingerprint.Compute(Builders.Message());

        for (var i = 0; i < 200; i++)
        {
            window.Record(
                new CampaignObservation
                {
                    TenantId = "tenant-1",
                    AssessmentId = $"asm-{i}",
                    InternalMessageId = $"msg-{i}",
                    ObservedAt = now,
                    Vector = vector,
                    Fingerprint = fingerprint,
                },
                now);
        }

        // The more mail an attacker sends, the more an unbounded window would retain — they would
        // control our memory by controlling our traffic.
        Assert.Equal(8, window.CountFor("tenant-1"));
    }

    [Fact]
    public void RetentionExpiresObservationsThatHaveAgedOut()
    {
        var window = new RecentCampaignWindow(capacityPerTenant: 64, retention: TimeSpan.FromMinutes(10));
        var start = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        var vector = StyloMail.Adaptive.Profiles.DimensionVector.Create();
        var fingerprint = SecurityBearingFingerprint.Compute(Builders.Message());

        void Record(int index, DateTimeOffset at) => window.Record(
            new CampaignObservation
            {
                TenantId = "tenant-1",
                AssessmentId = $"asm-{index}",
                InternalMessageId = $"msg-{index}",
                ObservedAt = at,
                Vector = vector,
                Fingerprint = fingerprint,
            },
            at);

        Record(0, start);
        Record(1, start + TimeSpan.FromMinutes(5));
        Record(2, start + TimeSpan.FromMinutes(30));

        // The two early observations are outside the retention window when the third arrives, so
        // only the message that just landed is still comparable.
        Assert.Equal(1, window.CountFor("tenant-1"));
    }

    [Fact]
    public async Task TheCacheStoreIsSafeUnderConcurrentWriters()
    {
        var store = new InMemorySemanticCacheStore();
        var options = new SemanticCacheOptions
        {
            ClassifierModelVersion = "jev-1.13.0",
            MaxEntries = 64,
        };

        var now = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

        // Task.Run so the work occupies real threads. A bare WhenAll over synchronous bodies would
        // serialise and prove nothing — the mistake this fleet made once already.
        var writers = Enumerable.Range(0, 32)
            .Select(worker => Task.Run(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    var key = $"key-{worker}-{i}";
                    store.Store(Entry(key, now), options, now);
                    store.Lookup(key, options, now);
                }
            }))
            .ToArray();

        await Task.WhenAll(writers);

        // The invariant is the ceiling, not the contents: whatever order the threads interleaved in,
        // the store must not have grown past its bound or thrown.
        Assert.True(store.Count <= options.MaxEntries);
        Assert.True(store.Count > 0);
    }

    private static CachedSemanticAssessment Entry(string key, DateTimeOffset now) => new()
    {
        KeyDigest = key,
        Evidence = [],
        Coverage = SemanticCoverage.From([]),
        CachedAt = now,
        ExpiresAt = now + TimeSpan.FromHours(1),
        ResolvedModelVersion = "jev-1.13.0",
        QuestionSchemaVersion = "semantic-dimensions/1",
        PreprocessingVersion = "assessment-preprocessing/1",
        Fingerprint = SecurityBearingFingerprint.Compute(Builders.Message()),
    };
}
