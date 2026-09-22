using StyloMail.Adaptive.Profiles;
using StyloMail.Core;

namespace StyloMail.Adaptive.Temporal;

/// <summary>Well-known non-semantic dimensions carried alongside the semantic ones.</summary>
public static class FeatureIds
{
    /// <summary>Messages per second in the bucket. Normalised by elapsed time, never by count.</summary>
    public const string MessagesPerSecond = "rate.messages_per_second";

    /// <summary>Recipients per second — the fan-out feature.</summary>
    public const string RecipientsPerSecond = "rate.recipients_per_second";
}

/// <summary>
/// One fixed clock bucket of behaviour.
/// </summary>
public sealed class BehaviourBucket
{
    private readonly Dictionary<string, (double Sum, int Count)> _dimensionSums = new(StringComparer.Ordinal);

    internal BehaviourBucket(long index, DateTimeOffset start, TimeSpan width)
    {
        Index = index;
        Start = start;
        End = start + width;
        Width = width;
    }

    public long Index { get; }

    public DateTimeOffset Start { get; }

    /// <summary>Exclusive end of the bucket.</summary>
    public DateTimeOffset End { get; }

    public TimeSpan Width { get; }

    public int SampleCount { get; private set; }

    public int RecipientCount { get; private set; }

    public int RejectedCount { get; private set; }

    /// <summary>Complete-bucket recipient rate. For the in-progress bucket use <see cref="RecipientsPerSecondAt"/>.</summary>
    public double RecipientsPerSecond => RecipientsPerSecondAt(End);

    public double MessagesPerSecond => MessagesPerSecondAt(End);

    /// <summary>
    /// Recipient rate as at <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// A bucket that is still filling is divided by the time actually elapsed, not by the
    /// bucket width. Dividing by the full width would halve the observed rate for the first
    /// thirty seconds of every bucket — precisely when a burst is beginning and the rate
    /// matters most.
    /// </remarks>
    public double RecipientsPerSecondAt(DateTimeOffset now) => Rate(RecipientCount, now);

    public double MessagesPerSecondAt(DateTimeOffset now) => Rate(SampleCount, now);

    /// <summary>
    /// The bucket's mean over each dimension, or a mask where too few samples support one.
    /// </summary>
    /// <remarks>
    /// A single sample is an anecdote, not a bucket mean. Below the support floor the
    /// dimension stays unknown rather than becoming a confident-looking point reading.
    /// </remarks>
    public DimensionVector MeanDimensions(int minimumSamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumSamples, 1);

        var samples = new List<DimensionSample>(_dimensionSums.Count);
        foreach (var (dimensionId, accumulator) in _dimensionSums)
        {
            samples.Add(accumulator.Count >= minimumSamples
                ? DimensionSample.Available(dimensionId, accumulator.Sum / accumulator.Count)
                : DimensionSample.Missing(dimensionId, EvidenceAvailability.Unavailable));
        }

        return DimensionVector.Create([.. samples]);
    }

    /// <summary>
    /// The bucket as a comparable vector: time-normalised rate features plus the semantic means.
    /// </summary>
    public DimensionVector FeatureVector(DateTimeOffset now, int minimumSamples)
    {
        var samples = new List<DimensionSample>(MeanDimensions(minimumSamples).Dimensions);

        var elapsed = ElapsedAt(now);
        if (elapsed > TimeSpan.Zero)
        {
            samples.Add(DimensionSample.Available(FeatureIds.MessagesPerSecond, MessagesPerSecondAt(now)));
            samples.Add(DimensionSample.Available(FeatureIds.RecipientsPerSecond, RecipientsPerSecondAt(now)));
        }

        return DimensionVector.Create([.. samples]);
    }

    /// <summary>The bucket's accumulated values, for persistence.</summary>
    public IReadOnlyList<BucketDimensionAccumulator> Accumulators =>
    [
        .. _dimensionSums.Select(pair => new BucketDimensionAccumulator
        {
            DimensionId = pair.Key,
            Sum = pair.Value.Sum,
            Count = pair.Value.Count,
        }),
    ];

    internal void Restore(int sampleCount, int recipientCount, int rejectedCount, IEnumerable<BucketDimensionAccumulator> accumulators)
    {
        SampleCount = sampleCount;
        RecipientCount = recipientCount;
        RejectedCount = rejectedCount;

        foreach (var accumulator in accumulators)
        {
            _dimensionSums[accumulator.DimensionId] = (accumulator.Sum, accumulator.Count);
        }
    }

    internal void Add(DateTimeOffset at, DimensionVector? dimensions, int recipientCount, bool rejected)
    {
        SampleCount++;
        RecipientCount += recipientCount;
        if (rejected)
        {
            RejectedCount++;
        }

        if (dimensions is null)
        {
            return;
        }

        foreach (var sample in dimensions.Dimensions)
        {
            if (sample.Value is null)
            {
                // Masked samples contribute nothing at all — not a zero, not a carry-forward.
                continue;
            }

            _dimensionSums[sample.DimensionId] = _dimensionSums.TryGetValue(sample.DimensionId, out var existing)
                ? (existing.Sum + sample.Value.Value, existing.Count + 1)
                : (sample.Value.Value, 1);
        }
    }

    /// <summary>
    /// Time actually watched. For a closed bucket that is the width; for the bucket still
    /// filling it is the elapsed part of it.
    /// </summary>
    private TimeSpan ElapsedAt(DateTimeOffset now) => now < End ? now - Start : End - Start;

    private double Rate(long count, DateTimeOffset now)
    {
        var seconds = ElapsedAt(now).TotalSeconds;
        return seconds <= 0 ? 0.0 : count / seconds;
    }
}

/// <summary>One dimension's accumulated values within a bucket, for serialisation.</summary>
public sealed record BucketDimensionAccumulator
{
    public required string DimensionId { get; init; }

    public required double Sum { get; init; }

    public required int Count { get; init; }
}

/// <summary>A bucket reduced to plain data, so profile state can be persisted and restored.</summary>
public sealed record BucketSnapshot
{
    public required long Index { get; init; }

    public required int SampleCount { get; init; }

    public required int RecipientCount { get; init; }

    public required int RejectedCount { get; init; }

    public required IReadOnlyList<BucketDimensionAccumulator> Dimensions { get; init; }
}

/// <summary>
/// Fixed clock buckets for one profile, keyed by bucket index.
/// </summary>
/// <remarks>
/// Buckets are aligned to the clock, not to the first observation, so two profiles observed
/// at different times still produce comparable bucket series. "One tick per message" would
/// make a quiet hour and a busy second the same one step.
/// </remarks>
public sealed class BucketSeries
{
    private readonly Dictionary<long, BehaviourBucket> _buckets = [];

    public BucketSeries(TimeSpan width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, TimeSpan.Zero);
        Width = width;
    }

    public TimeSpan Width { get; }

    /// <summary>Populated buckets, ascending by index.</summary>
    public IReadOnlyList<BehaviourBucket> Buckets =>
        [.. _buckets.Values.OrderBy(bucket => bucket.Index)];

    public void Add(
        DateTimeOffset at,
        DimensionVector? dimensions,
        int recipients = 1,
        bool rejected = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recipients);

        var index = IndexOf(at);
        if (!_buckets.TryGetValue(index, out var bucket))
        {
            bucket = new BehaviourBucket(index, StartOf(index), Width);
            _buckets[index] = bucket;
        }

        bucket.Add(at, dimensions, recipients, rejected);
    }

    /// <summary>Reduces the series to plain data for persistence.</summary>
    public IReadOnlyList<BucketSnapshot> Snapshot() =>
    [
        .. Buckets.Select(bucket => new BucketSnapshot
        {
            Index = bucket.Index,
            SampleCount = bucket.SampleCount,
            RecipientCount = bucket.RecipientCount,
            RejectedCount = bucket.RejectedCount,
            Dimensions = bucket.Accumulators,
        }),
    ];

    /// <summary>Rebuilds a series from <see cref="Snapshot"/> output.</summary>
    public static BucketSeries Restore(TimeSpan width, IEnumerable<BucketSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var series = new BucketSeries(width);
        foreach (var snapshot in snapshots)
        {
            var bucket = new BehaviourBucket(snapshot.Index, series.StartOf(snapshot.Index), width);
            bucket.Restore(snapshot.SampleCount, snapshot.RecipientCount, snapshot.RejectedCount, snapshot.Dimensions);
            series._buckets[snapshot.Index] = bucket;
        }

        return series;
    }

    public long IndexOf(DateTimeOffset at) =>
        (long)Math.Floor(at.ToUnixTimeMilliseconds() / (double)Width.TotalMilliseconds);

    public DateTimeOffset StartOf(long index) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(index * Width.TotalMilliseconds));

    /// <summary>
    /// The window ending at the bucket containing <paramref name="now"/>, as a dense grid.
    /// </summary>
    /// <remarks>
    /// Empty buckets are materialised rather than skipped. Skipping them is how a gap becomes
    /// an adjacency, and an adjacency is what makes a stale reading look like fresh movement.
    /// </remarks>
    public IReadOnlyList<BehaviourBucket> Window(TrendWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentOutOfRangeException.ThrowIfLessThan(window.BucketCount, 1);

        // The window ends at the bucket containing `now`. Buckets observed after `now` — a
        // backfill, or a replay that ran ahead — are deliberately outside it: analysing a
        // window relative to a stated instant is what makes the result reproducible.
        var end = IndexOf(now);
        var start = end - window.BucketCount + 1;

        var grid = new List<BehaviourBucket>(window.BucketCount);
        for (var index = start; index <= end; index++)
        {
            grid.Add(_buckets.TryGetValue(index, out var existing)
                ? existing
                : new BehaviourBucket(index, StartOf(index), Width));
        }

        return grid;
    }
}
