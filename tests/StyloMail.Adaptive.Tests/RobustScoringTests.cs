using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Scoring;
using StyloMail.Core;

namespace StyloMail.Adaptive.Tests;

public class DimensionVectorTests
{
    [Fact]
    public void AMissingDimensionHasNoValue()
    {
        var vector = DimensionVector.Create(
            DimensionSample.Available("d1", 0.5),
            DimensionSample.Missing("d2", EvidenceAvailability.Unavailable));

        Assert.True(vector.IsMasked("d2"));
        Assert.Null(vector.ValueOf("d2"));
        Assert.Equal(0.5, vector.ValueOf("d1"));
    }

    [Fact]
    public void CoverageCountsOnlyProducedDimensions()
    {
        var vector = DimensionVector.Create(
            DimensionSample.Available("d1", 0.5),
            DimensionSample.Available("d2", 0.2),
            DimensionSample.Missing("d3", EvidenceAvailability.Unavailable),
            DimensionSample.Missing("d4", EvidenceAvailability.NotApplicable));

        Assert.Equal(0.5, vector.Coverage);
        Assert.Equal(2, vector.MaskedDimensionIds.Count);
        Assert.Contains("d3", vector.MaskedDimensionIds);
        Assert.Contains("d4", vector.MaskedDimensionIds);
    }

    [Fact]
    public void ReducedCoverageIsAvailableButRecorded()
    {
        var vector = DimensionVector.Create(
            DimensionSample.Reduced("d1", 0.5, "encrypted attachment"));

        // The value is real but weaker; it is not the same as a clean available signal.
        Assert.False(vector.IsMasked("d1"));
        Assert.True(vector.IsReduced("d1"));
        Assert.Equal(EvidenceAvailability.ReducedCoverage, vector.AvailabilityOf("d1"));
    }

    [Theory]
    [InlineData(EvidenceAvailability.Unavailable)]
    [InlineData(EvidenceAvailability.NotApplicable)]
    public void AMissingDimensionCannotCarryAValue(EvidenceAvailability availability)
    {
        // Fabricating a value for a signal that was not produced is exactly the
        // "absence is zero" failure this model exists to prevent.
        Assert.Throws<ArgumentException>(() => DimensionVector.Create(
            new DimensionSample
            {
                DimensionId = "d1",
                Value = 0.0,
                Availability = availability,
            }));
    }

    [Fact]
    public void AnAvailableDimensionMustCarryAValue()
    {
        Assert.Throws<ArgumentException>(() => DimensionVector.Create(
            new DimensionSample
            {
                DimensionId = "d1",
                Value = null,
                Availability = EvidenceAvailability.Available,
            }));
    }

    [Fact]
    public void DuplicateDimensionIdsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => DimensionVector.Create(
            DimensionSample.Available("d1", 0.5),
            DimensionSample.Available("d1", 0.7)));
    }
}

public class RobustScaleModelTests
{
    [Fact]
    public void StandardizesAgainstTheTrustedMeanAndSpread()
    {
        var model = Fit(("d1", [0.4, 0.5, 0.6]));

        var probe = model.Standardize(Vector(("d1", 0.7)));

        // Mean 0.5, sample variance 0.01, sd 0.1 → a probe at 0.7 sits 2 sd out.
        Assert.Equal(0.5, model.Dimensions["d1"].Mean, 6);
        Assert.Equal(0.1, model.Dimensions["d1"].Scale, 6);
        Assert.Equal(2.0, probe.Z["d1"], 6);
    }

    [Fact]
    public void ZeroVarianceIsClampedByTheVarianceFloor()
    {
        var model = Fit(("d1", [0.5, 0.5, 0.5, 0.5, 0.5]));

        var probe = model.Standardize(Vector(("d1", 5.0)));

        // Without a floor this z would be a division by zero, not a large number.
        Assert.Equal(0.05, model.Dimensions["d1"].Scale, 6);
        Assert.Equal(6.0, probe.Z["d1"], 6);
        Assert.Equal(6.0, probe.Distance!.Value, 6);
    }

    [Fact]
    public void ExtremeValuesAreClampedRatherThanAllowedToDominate()
    {
        var model = Fit(("d1", [0.4, 0.5, 0.6, 0.45, 0.55]));

        var probe = model.Standardize(Vector(("d1", 1000.0)));

        // Clamping keeps one wild dimension from swamping the aggregate on its own.
        Assert.Equal(6.0, probe.Z["d1"]);
    }

    [Fact]
    public void DistanceIsAnRmsAcrossComparedDimensionsNotASum()
    {
        var model = Fit(("d1", [0.4, 0.5, 0.6]), ("d2", [0.4, 0.5, 0.6]));

        var probe = model.Standardize(Vector(("d1", 0.6), ("d2", 0.6)));

        // Two dimensions each 1 sd out is a distance of 1, not 2, otherwise a message
        // with wider coverage would look more anomalous for producing more evidence.
        Assert.Equal(1.0, probe.Z["d1"], 6);
        Assert.Equal(1.0, probe.Distance!.Value, 6);
    }

    [Fact]
    public void ADimensionWithTooLittleSupportIsNotScored()
    {
        var model = Fit(("d1", [0.4, 0.5, 0.6, 0.45, 0.55]), ("d2", [0.4, 0.5]));

        var probe = model.Standardize(Vector(("d1", 0.5), ("d2", 0.9)));

        Assert.DoesNotContain("d2", probe.Z.Keys);
        Assert.Contains("d2", probe.UnmodelledDimensionIds);
        Assert.Equal(1, probe.ComparedDimensionCount);
        Assert.Equal(0.5, probe.Coverage);
    }

    [Fact]
    public void AMaskedDimensionIsExcludedAndNeverFilledWithZero()
    {
        var model = Fit(("d1", [0.8, 0.9, 1.0, 0.85, 0.95]), ("d2", [0.8, 0.9, 1.0, 0.85, 0.95]));

        var probe = model.Standardize(DimensionVector.Create(
            DimensionSample.Available("d1", 0.9),
            DimensionSample.Missing("d2", EvidenceAvailability.Unavailable)));

        // d2's trusted mean is 0.9. Zero-filling would place it 0.9/scale from the mean and
        // clamp to a full 6.0 of apparent drift on a dimension that produced nothing at all.
        Assert.Equal(0.0, probe.Distance!.Value, 6);
        Assert.Contains("d2", probe.MaskedDimensionIds);
        Assert.Equal(0.5, probe.Coverage);
    }

    [Fact]
    public void CoverageReportsHowMuchOfTheProbeWasActuallyCompared()
    {
        var model = Fit(("d1", [0.4, 0.5, 0.6, 0.45, 0.55]), ("d2", [0.4, 0.5, 0.6, 0.45, 0.55]));

        var probe = model.Standardize(DimensionVector.Create(
            DimensionSample.Available("d1", 0.5),
            DimensionSample.Missing("d2", EvidenceAvailability.Unavailable)));

        // One of the two dimensions was actually compared. Publishing that coverage is what
        // stops a distance computed over half a vector from being read as though it covered
        // the whole thing, and it is what the drift evidence reports as its sample support.
        Assert.Equal(1, probe.ComparedDimensionCount);
        Assert.Equal(0.5, probe.Coverage);
        Assert.DoesNotContain("d2", probe.Z.Keys);
    }

    [Fact]
    public void AProbeDimensionTheModelHasNeverSeenIsNotInvented()
    {
        var model = Fit(("d1", [0.4, 0.5, 0.6, 0.45, 0.55]));

        var probe = model.Standardize(Vector(("d1", 0.5), ("d9", 1.0)));

        // No trusted distribution exists for d9, so there is nothing to compare against,         // and inventing a comparison would be worse than admitting the gap.
        Assert.Contains("d9", probe.UnmodelledDimensionIds);
        Assert.Equal(1, probe.ComparedDimensionCount);
        Assert.Equal(0.5, probe.Coverage);
    }

    [Fact]
    public void AnEmptyModelComparesNothingRatherThanScoringZero()
    {
        var probe = RobustScaleModel.Empty().Standardize(Vector(("d1", 0.9)));

        Assert.Equal(0, probe.ComparedDimensionCount);
        Assert.Equal(0.0, probe.Coverage);
        Assert.Null(probe.Distance);
    }

    private static RobustScaleModel Fit(params (string Id, double[] Values)[] dimensions) =>
        RobustScaleModel.FromMoments(dimensions.Select(d =>
            KeyValuePair.Create(d.Id, RunningMoments.From(d.Values))));

    private static DimensionVector Vector(params (string Id, double Value)[] values) =>
        DimensionVector.Create([.. values.Select(v => DimensionSample.Available(v.Id, v.Value))]);
}
