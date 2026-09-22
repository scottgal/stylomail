using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Scoring;
using StyloMail.Adaptive.Temporal;

namespace StyloMail.Adaptive.Tests;

public class BucketSeriesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BucketsAlignToTheClockNotToFirstObservation()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));

        // Two messages nineteen seconds apart land in the same fixed bucket; a "one tick per
        // message" model would have made them two points and invented a trend.
        series.Add(T0.AddSeconds(5), Vector(("d1", 0.5)));
        series.Add(T0.AddSeconds(24), Vector(("d1", 0.7)));

        Assert.Single(series.Buckets);
        Assert.Equal(2, series.Buckets[0].SampleCount);
    }

    [Fact]
    public void ARateIsNormalisedByElapsedTimeNotByMessageCount()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        series.Add(T0.AddSeconds(1), Vector(("d1", 0.5)), recipients: 60);

        var bucket = series.Buckets[0];

        Assert.Equal(1.0, bucket.RecipientsPerSecond, 6);
        Assert.Equal(1.0 / 60.0, bucket.MessagesPerSecond, 6);
    }

    [Fact]
    public void TheInProgressBucketUsesElapsedSoFarRatherThanTheFullWidth()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        series.Add(T0.AddSeconds(1), Vector(("d1", 0.5)), recipients: 30);

        var bucket = series.Buckets[0];

        // Counting a half-elapsed bucket against a full minute would halve the observed rate
        // exactly when a burst is starting.
        Assert.Equal(1.0, bucket.RecipientsPerSecondAt(T0.AddSeconds(30)), 6);

        // Once the bucket closes, the same thirty recipients really are half a per second.
        Assert.Equal(0.5, bucket.RecipientsPerSecondAt(T0.AddMinutes(1)), 6);
    }

    [Fact]
    public void ASemanticBucketWithoutEnoughSamplesStaysMissing()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        series.Add(T0.AddSeconds(1), Vector(("d1", 0.9)));

        var bucket = series.Buckets[0];
        var sparse = bucket.MeanDimensions(minimumSamples: 3);
        var dense = bucket.MeanDimensions(minimumSamples: 1);

        // One sample is an anecdote, not a bucket mean; the dimension stays unknown rather
        // than becoming a confident point reading.
        Assert.True(sparse.IsMasked("d1"));
        Assert.Equal(0.9, dense.ValueOf("d1"));
    }

    [Fact]
    public void AWindowIsADenseGridIncludingEmptyBuckets()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        series.Add(T0.AddMinutes(2).AddSeconds(1), Vector(("d1", 0.5)));
        series.Add(T0.AddMinutes(4).AddSeconds(1), Vector(("d1", 0.5)));

        var grid = series.Window(Window(bucketCount: 5, minimumSamples: 1), T0.AddMinutes(5));

        // Empty buckets are materialised as empty, not skipped. Skipping them is how a gap
        // silently becomes an adjacency and a stale reading becomes a fresh one.
        Assert.Equal(5, grid.Count);
        Assert.Equal(2, grid.Count(b => b.SampleCount > 0));
        Assert.Equal(3, grid.Count(b => b.SampleCount == 0));
    }

    private static DimensionVector Vector(params (string Id, double Value)[] values) =>
        DimensionVector.Create([.. values.Select(v => DimensionSample.Available(v.Id, v.Value))]);

    internal static TrendWindow Window(
        int bucketCount = 5,
        int minimumSamples = 1,
        int maxGapMinutes = 3,
        TimeSpan? smoothingTau = null,
        string name = "burst") => new()
        {
            Name = name,
            BucketWidth = TimeSpan.FromMinutes(1),
            BucketCount = bucketCount,
            SmoothingTau = smoothingTau ?? TimeSpan.FromMinutes(1),
            MinimumSamplesPerBucket = minimumSamples,
            MaxGap = TimeSpan.FromMinutes(maxGapMinutes),
        };
}

public class TrendAnalyzerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly RobustScaleModel Baseline = RobustScaleModel.FromScales(
    [
        Scale("semantic.payment_redirection", mean: 0.05, scale: 0.05),
        Scale("semantic.credential_request", mean: 0.05, scale: 0.05),
        Scale(FeatureIds.RecipientsPerSecond, mean: 1.0, scale: 0.5),
        Scale(FeatureIds.MessagesPerSecond, mean: 0.1, scale: 0.05),
    ]);

    [Fact]
    public void ASingleBucketProducesNoVelocityOrAcceleration()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        series.Add(T0.AddSeconds(1), Vector(("semantic.payment_redirection", 0.9)));

        var trend = Analyze(series, T0.AddMinutes(1));

        // Cold start: one point is a position, not a trend. Reporting acceleration here is
        // the classic false positive that gets a brand-new sender quarantined.
        Assert.False(trend.VelocityAvailable);
        Assert.False(trend.AccelerationAvailable);
        Assert.Contains(TrendSuppressionReason.InsufficientSupport, trend.Suppressions);
        Assert.Empty(trend.Velocity);
    }

    [Fact]
    public void AMissingBaselineSuppressesDerivativesRatherThanAssumingNormal()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 4; i++)
        {
            series.Add(T0.AddMinutes(i).AddSeconds(1), Vector(("semantic.payment_redirection", 0.5)));
        }

        var trend = Analyze(series, T0.AddMinutes(4), scaleModel: RobustScaleModel.Empty());

        Assert.Contains(TrendSuppressionReason.BaselineUnavailable, trend.Suppressions);
        Assert.False(trend.AccelerationAvailable);
    }

    [Fact]
    public void AnEmptyBucketSuppressesTheDerivativeAcrossIt()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        series.Add(T0.AddSeconds(1), Vector(("semantic.payment_redirection", 0.05)));
        series.Add(T0.AddMinutes(1).AddSeconds(1), Vector(("semantic.payment_redirection", 0.10)));
        // minute 2 is empty
        series.Add(T0.AddMinutes(3).AddSeconds(1), Vector(("semantic.payment_redirection", 0.95)));
        series.Add(T0.AddMinutes(4).AddSeconds(1), Vector(("semantic.payment_redirection", 0.95)));

        var trend = Analyze(series, T0.AddMinutes(5));

        // The jump from bucket 1 to bucket 3 spans a bucket that produced nothing, so it is
        // a gap in observation, not a burst of behaviour.
        Assert.Contains(TrendSuppressionReason.SparseBucket, trend.Suppressions);
        Assert.False(trend.VelocityAvailable);
        Assert.False(trend.AccelerationAvailable);
    }

    [Fact]
    public void ALongGapSuppressesTheDerivative()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        series.Add(T0.AddSeconds(1), Vector(("semantic.payment_redirection", 0.05)));
        series.Add(T0.AddMinutes(20).AddSeconds(1), Vector(("semantic.payment_redirection", 0.05)));

        var trend = Analyze(series, T0.AddMinutes(21), bucketCount: 25, maxGapMinutes: 3);

        // An idle weekend is not a 20-minute-long acceleration.
        Assert.Contains(TrendSuppressionReason.LongGap, trend.Suppressions);
        Assert.False(trend.AccelerationAvailable);
    }

    [Fact]
    public void ALongGapIsMeasuredInElapsedTimeNotInBucketCount()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 4; i++)
        {
            series.Add(T0.AddMinutes(i).AddSeconds(1), Vector(("semantic.payment_redirection", 0.05)));
        }

        // Every bucket is populated, so nothing here is sparse — the only way to reach
        // LongGap alone. A max gap shorter than the bucket width is unusual but legitimate,
        // and it is the case that pins the check to elapsed time rather than to an index count.
        var trend = TrendAnalyzer.Analyze(new TrendRequest
        {
            Series = series,
            Window = BucketSeriesTests.Window(bucketCount: 5, minimumSamples: 1) with
            {
                MaxGap = TimeSpan.FromSeconds(30),
            },
            ScaleModel = Baseline,
            Now = T0.AddMinutes(4),
            DimensionSchemaVersion = "adaptive-dimensions/1",
            PriorDimensionSchemaVersion = "adaptive-dimensions/1",
        });

        Assert.Contains(TrendSuppressionReason.LongGap, trend.Suppressions);
        Assert.DoesNotContain(TrendSuppressionReason.SparseBucket, trend.Suppressions);
        Assert.False(trend.VelocityAvailable);
    }

    [Fact]
    public void ARegimeChangeSuppressesTheDerivative()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 4; i++)
        {
            series.Add(T0.AddMinutes(i).AddSeconds(1), Vector(("semantic.payment_redirection", 0.05 + (i * 0.1))));
        }

        var trend = Analyze(series, T0.AddMinutes(4), regimeId: "regime-b", priorRegimeId: "regime-a");

        // The baseline no longer describes this regime, so nothing may be differenced
        // against it until the new regime has earned its own support.
        Assert.Contains(TrendSuppressionReason.RegimeChange, trend.Suppressions);
        Assert.False(trend.VelocityAvailable);
        Assert.False(trend.AccelerationAvailable);
    }

    [Fact]
    public void ADimensionSchemaChangeSuppressesTheDerivative()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 4; i++)
        {
            series.Add(T0.AddMinutes(i).AddSeconds(1), Vector(("semantic.payment_redirection", 0.5)));
        }

        var trend = Analyze(
            series,
            T0.AddMinutes(4),
            dimensionSchemaVersion: "adaptive-dimensions/2",
            priorDimensionSchemaVersion: "adaptive-dimensions/1");

        // The old and new dimension sets are not comparable; differencing them would be
        // arithmetic on unrelated quantities.
        Assert.Contains(TrendSuppressionReason.DimensionSchemaChange, trend.Suppressions);
        Assert.False(trend.AccelerationAvailable);
    }

    [Fact]
    public void ARisingPairProducesVelocityAndAcceleration()
    {
        var series = RisingSeries();

        var trend = Analyze(series, T0.AddMinutes(3));

        Assert.True(trend.VelocityAvailable);
        Assert.True(trend.AccelerationAvailable);
        Assert.Empty(trend.Suppressions);
        Assert.True(trend.Velocity["semantic.payment_redirection"] > 0);
        Assert.True(trend.Acceleration["semantic.payment_redirection"] > 0);
    }

    [Fact]
    public void VelocityIsADifferenceDividedByElapsedTime()
    {
        var series = RisingSeries();

        var trend = Analyze(series, T0.AddMinutes(3));

        // The final smoothed payment-redirection z is 4.4903 and the previous is 1.8964; over
        // the 60s between them that is 0.0432 per second — not the 2.594 per-bucket difference,
        // which would scale with the bucket width and quietly mean something else entirely.
        var perBucket = 4.4903442 - 1.8963617;
        Assert.Equal(0.043233, trend.Velocity["semantic.payment_redirection"], 6);
        Assert.Equal(perBucket, trend.Velocity["semantic.payment_redirection"] * 60.0, 3);
    }

    [Fact]
    public void AccelerationIsTheChangeInVelocityDividedByElapsedTime()
    {
        var series = RisingSeries();

        var trend = Analyze(series, T0.AddMinutes(3));
        var velocityOneBucketEarlier = Analyze(series, T0.AddMinutes(1))
            .Velocity["semantic.payment_redirection"];

        var expected = (trend.Velocity["semantic.payment_redirection"] - velocityOneBucketEarlier) / 60.0;

        Assert.Equal(expected, trend.Acceleration["semantic.payment_redirection"], 12);
        Assert.True(trend.Acceleration["semantic.payment_redirection"] > 0);
    }

    [Fact]
    public void TheNarrativeNamesTheReasonsRatherThanReportingAScalar()
    {
        var trend = Analyze(RisingSeries(), T0.AddMinutes(3));

        // "Explained" by a bare acceleration number is not explained at all — the operator
        // needs to know what moved.
        Assert.Equal(
            "recipient fan-out rising while payment-redirection evidence also rises",
            trend.Narrative);
    }

    [Fact]
    public void ASuppressedWindowSaysSoInsteadOfReportingZero()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        series.Add(T0.AddSeconds(1), Vector(("semantic.payment_redirection", 0.9)));

        var trend = Analyze(series, T0.AddMinutes(1));

        Assert.Contains("suppressed", trend.Narrative, StringComparison.Ordinal);
        Assert.Contains("insufficient_support", trend.Narrative, StringComparison.Ordinal);
    }

    [Fact]
    public void MovementsCarryBothTheRateAndTheSemanticDimension()
    {
        var trend = Analyze(RisingSeries(), T0.AddMinutes(3));

        Assert.Contains(trend.Movements, m => m.DimensionId == FeatureIds.RecipientsPerSecond);
        Assert.Contains(trend.Movements, m => m.DimensionId == "semantic.payment_redirection");
        Assert.All(trend.Movements, m => Assert.True(m.VelocityPerSecond > 0));
    }

    [Fact]
    public void CoverageReportsHowMuchOfTheVectorWasDifferenced()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 3; i++)
        {
            // Only one semantic dimension is ever produced; the credential dimension is
            // absent from every bucket.
            series.Add(T0.AddMinutes(i).AddSeconds(1), Vector(
                ("semantic.payment_redirection", 0.05 + (i * 0.15))), recipients: 30);
        }

        var trend = Analyze(series, T0.AddMinutes(3));

        Assert.Equal(3, trend.Support);
        Assert.True(trend.Coverage > 0);
        Assert.True(trend.Coverage < 1);
        Assert.DoesNotContain("semantic.credential_request", trend.Velocity.Keys);
    }

    private static BucketSeries RisingSeries()
    {
        var series = new BucketSeries(TimeSpan.FromMinutes(1));
        series.Add(T0.AddSeconds(1), Vector(("semantic.payment_redirection", 0.05)), recipients: 30);
        series.Add(T0.AddMinutes(1).AddSeconds(1), Vector(("semantic.payment_redirection", 0.20)), recipients: 60);
        series.Add(T0.AddMinutes(2).AddSeconds(1), Vector(("semantic.payment_redirection", 0.35)), recipients: 120);
        return series;
    }

    private static TrendResult Analyze(
        BucketSeries series,
        DateTimeOffset now,
        int bucketCount = 5,
        int? maxGapMinutes = null,
        RobustScaleModel? scaleModel = null,
        string dimensionSchemaVersion = "adaptive-dimensions/1",
        string? priorDimensionSchemaVersion = "adaptive-dimensions/1",
        string? regimeId = null,
        string? priorRegimeId = null) =>
        TrendAnalyzer.Analyze(new TrendRequest
        {
            Series = series,
            Window = BucketSeriesTests.Window(
                bucketCount: bucketCount,
                maxGapMinutes: maxGapMinutes ?? bucketCount),
            ScaleModel = scaleModel ?? Baseline,
            Now = now,
            DimensionSchemaVersion = dimensionSchemaVersion,
            PriorDimensionSchemaVersion = priorDimensionSchemaVersion,
            RegimeId = regimeId,
            PriorRegimeId = priorRegimeId,
        });

    private static DimensionScale Scale(string id, double mean, double scale) => new()
    {
        DimensionId = id,
        Mean = mean,
        Variance = scale * scale,
        Scale = scale,
        Support = 10,
    };

    private static DimensionVector Vector(params (string Id, double Value)[] values) =>
        DimensionVector.Create([.. values.Select(v => DimensionSample.Available(v.Id, v.Value))]);
}
