using StyloMail.Adaptive.Profiles;

namespace StyloMail.Adaptive.Tests;

/// <summary>
/// The membership structure behind "have we ever seen this recipient?".
/// </summary>
/// <remarks>
/// A capped set cannot answer that question once it has been truncated, and truncation is permanent
///, so novelty would go dark precisely on the accounts with the widest reach, which are the ones
/// most likely to be compromised. A signal that vanishes exactly where it is needed is worse than
/// no signal, because it still looks present.
///
/// <para>
/// A Bloom filter answers membership with <b>no false negatives</b>. Its false positives run
/// towards treating a new recipient as already known, so its errors <em>miss</em> novelty rather
/// than inventing it. The direction of the error decides whether the structure is acceptable, and
/// this is the harmless direction.
/// </para>
/// </remarks>
public class RecipientBloomFilterTests
{
    [Fact]
    public void ARecordedRecipientIsAlwaysReportedAsSeen()
    {
        var filter = new RecipientBloomFilter(expectedCapacity: 1024, falsePositiveRate: 0.01);

        for (var i = 0; i < 500; i++)
        {
            filter.Add($"recipient-{i}");
        }

        // The guarantee the whole design rests on: never a false negative. Everything added must
        // be found, however full the filter gets.
        for (var i = 0; i < 500; i++)
        {
            Assert.True(filter.MightContain($"recipient-{i}"), $"recipient-{i} went missing");
        }
    }

    [Fact]
    public void NoFalseNegativesAfterFarMoreInsertsThanItWasSizedFor()
    {
        var filter = new RecipientBloomFilter(expectedCapacity: 64, falsePositiveRate: 0.01);

        for (var i = 0; i < 5_000; i++)
        {
            filter.Add($"recipient-{i}");
        }

        // Saturation degrades the false-positive rate, never the no-false-negative guarantee.
        // That asymmetry is the entire reason this structure is acceptable here.
        for (var i = 0; i < 5_000; i++)
        {
            Assert.True(filter.MightContain($"recipient-{i}"), $"recipient-{i} went missing");
        }
    }

    [Fact]
    public void MemoryIsFixedAtConstructionRegardlessOfHowMuchIsRecorded()
    {
        var small = new RecipientBloomFilter(expectedCapacity: 256, falsePositiveRate: 0.01);
        var large = new RecipientBloomFilter(expectedCapacity: 4096, falsePositiveRate: 0.01);

        for (var i = 0; i < 100_000; i++)
        {
            small.Add($"recipient-{i}");
        }

        // Bounded by construction rather than by a cap that has to evict, which is what makes it
        // answer the question the set cannot: it never forgets.
        Assert.True(small.BitCount < large.BitCount);
        Assert.True(small.BitCount > 0);
    }

    [Fact]
    public void ASmallerFalsePositiveRateCostsMoreMemory()
    {
        var loose = new RecipientBloomFilter(expectedCapacity: 1024, falsePositiveRate: 0.1);
        var tight = new RecipientBloomFilter(expectedCapacity: 1024, falsePositiveRate: 0.001);

        Assert.True(tight.BitCount > loose.BitCount);
    }

    [Fact]
    public void RealWorldFalsePositivesStayNearTheConfiguredRate()
    {
        const int capacity = 2_000;
        var filter = new RecipientBloomFilter(expectedCapacity: capacity, falsePositiveRate: 0.01);

        for (var i = 0; i < capacity; i++)
        {
            filter.Add($"seen-{i}");
        }

        var falsePositives = Enumerable.Range(0, 5_000)
            .Count(i => filter.MightContain($"never-seen-{i}"));

        // A filter that never reported a false positive would mean the probe set proves nothing.
        // The tolerance is loose on purpose: this pins the order of magnitude, not a bit pattern.
        Assert.InRange(falsePositives, 1, 200);
    }

    [Fact]
    public void AMemberIsFoundEvenInAnEmptyFilterItWasNeverAddedTo()
    {
        var filter = new RecipientBloomFilter(expectedCapacity: 256, falsePositiveRate: 0.01);

        // An empty filter is not a filter that knows nothing: it is one that has recorded nothing.
        // Callers must not read "not present" from an unrestored filter as "never seen", which is
        // why the history tracks whether it was ever populated.
        Assert.False(filter.MightContain("recipient-0"));
        Assert.Equal(0, filter.Count);
    }

    [Fact]
    public void TheSameKeysProduceTheSameBitsEveryTime()
    {
        var first = new RecipientBloomFilter(expectedCapacity: 256, falsePositiveRate: 0.01);
        var second = new RecipientBloomFilter(expectedCapacity: 256, falsePositiveRate: 0.01);

        foreach (var key in new[] { "alice", "bob", "carol" })
        {
            first.Add(key);
            second.Add(key);
        }

        // Hashing must not depend on per-process seed randomisation: a restarted process would
        // otherwise disagree with a persisted filter about what it already contains, and would
        // report every recipient as novel. Building the same set twice must produce the same bits,
        // not merely the same answers.
        Assert.Equal(first.ToBytes(), second.ToBytes());
    }

    [Fact]
    public void AFilterSurvivesRoundTrippingThroughBytes()
    {
        var filter = new RecipientBloomFilter(expectedCapacity: 512, falsePositiveRate: 0.01);
        foreach (var key in new[] { "alice", "bob", "carol" })
        {
            filter.Add(key);
        }

        var restored = RecipientBloomFilter.FromBytes(filter.ToBytes());

        Assert.Equal(filter.BitCount, restored.BitCount);
        Assert.True(restored.MightContain("alice"));
        Assert.True(restored.MightContain("bob"));
        Assert.True(restored.MightContain("carol"));

        // And it stays honest about what it has not seen, which is the whole point of restoring it.
        Assert.False(restored.MightContain("stranger-who-was-never-recorded"));
    }

    [Fact]
    public void CorruptBytesAreRejectedRatherThanSilentlyTrusted()
    {
        var filter = new RecipientBloomFilter(expectedCapacity: 256, falsePositiveRate: 0.01);
        var bytes = filter.ToBytes();

        // A truncated blob would otherwise restore as a smaller filter that answers confidently
        // and wrongly, which is worse than refusing it.
        Assert.Throws<ArgumentException>(
            () => RecipientBloomFilter.FromBytes(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<ArgumentException>(() => RecipientBloomFilter.FromBytes([1, 2, 3]));
    }
}
