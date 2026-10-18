using StyloMail.Core;

namespace StyloMail.Image.Tests;

public sealed class ImageAdmissionTests
{
    private static ImageCandidate Candidate(
        int ordinal,
        long bytes,
        long? width = null,
        long? height = null,
        bool? transparent = null) => new()
        {
            Ordinal = ordinal,
            DecodedBytes = bytes,
            Width = width,
            Height = height,
            TransparencyDeclared = transparent,
        };

    [Fact]
    public void PayloadBeatsChromeAndTheSmallQrIsNotSkippedOnBytes()
    {
        var candidates = new[]
        {
            Candidate(1, 200_000, 800, 600),          // logo
            Candidate(2, 100, 1, 1, transparent: true), // tracking pixel
            Candidate(3, 3_000, 200, 200, transparent: false), // sub-4 KiB QR
        };

        var result = ImageAdmission.Select(candidates);

        Assert.Equal([1, 3], result.Admitted.Select(c => c.Ordinal));
        Assert.Equal([2], result.SkippedChromeOrdinals);
        Assert.Empty(result.OverBudgetOrdinals);
        Assert.Equal(0, result.ImagesOverBudget);
    }

    [Fact]
    public void KnownDimensionQrUnderFourKiBIsEligible()
    {
        var result = ImageAdmission.Select(
            [Candidate(1, 1_500, 200, 200, transparent: false)]);

        Assert.Single(result.Admitted);
        Assert.Empty(result.SkippedChromeOrdinals);
    }

    [Fact]
    public void UnknownDimensionsUseTheTwoKiBFloorOnly()
    {
        var small = ImageAdmission.Select([Candidate(1, 1_000)]);
        Assert.Empty(small.Admitted);
        Assert.Single(small.SkippedChromeOrdinals);

        var large = ImageAdmission.Select([Candidate(1, 3_000)]);
        Assert.Single(large.Admitted);
        Assert.Empty(large.SkippedChromeOrdinals);
    }

    [Fact]
    public void TransparentSmallIsChromeButTransparentFullSizeIsEligible()
    {
        var small = ImageAdmission.Select([Candidate(1, 50_000, 50, 50, transparent: true)]);
        Assert.Empty(small.Admitted);
        Assert.Single(small.SkippedChromeOrdinals);

        var full = ImageAdmission.Select([Candidate(1, 50_000, 200, 200, transparent: true)]);
        Assert.Single(full.Admitted);
    }

    [Fact]
    public void OverBudgetOrdinalsAreFirstAndLastInTrustedPartOrder()
    {
        var candidates = new[]
        {
            Candidate(1, 300_000, 800, 600),
            Candidate(2, 200_000, 700, 500),
            Candidate(3, 100_000, 600, 400),
            Candidate(4, 90_000, 500, 300),
            Candidate(5, 80_000, 400, 300),
        };

        var result = ImageAdmission.Select(candidates);

        Assert.Equal([1, 2], result.Admitted.Select(c => c.Ordinal));
        Assert.Equal([3, 4, 5], result.OverBudgetOrdinals);
        Assert.Equal(3, result.FirstOverBudgetOrdinal);
        Assert.Equal(5, result.LastOverBudgetOrdinal);
        Assert.Equal(3, result.ImagesOverBudget);
    }

    [Fact]
    public void TiesAreBrokenByTrustedPartOrdinal()
    {
        var result = ImageAdmission.Select(
        [
            Candidate(3, 10_000, 100, 100),
            Candidate(1, 10_000, 100, 100),
            Candidate(2, 10_000, 100, 100),
        ]);

        Assert.Equal([1, 2], result.Admitted.Select(c => c.Ordinal));
        Assert.Equal([3], result.OverBudgetOrdinals);
    }

    [Fact]
    public void ProbeSelectionTakesTheLargestUnknownDimensionCandidates()
    {
        var candidates = new[]
        {
            Candidate(1, 10_000),
            Candidate(2, 50_000),
            Candidate(3, 20_000, 100, 100), // already known
            Candidate(4, 40_000),
            Candidate(5, 30_000),
            Candidate(6, 5_000),
        };

        var probes = ImageAdmission.SelectProbeOrdinals(candidates);

        Assert.Equal([2, 4, 5, 1], probes);
    }

    [Fact]
    public void DuplicateOrdinalsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => ImageAdmission.Select(
        [
            Candidate(1, 10_000, 100, 100),
            Candidate(1, 20_000, 100, 100),
        ]));
    }

    [Fact]
    public void FiveHundredAndTwelvePartsProduceExactCountsAndTwoAdmissions()
    {
        var candidates = Enumerable.Range(1, 512)
            .Select(ordinal => Candidate(ordinal, 1_000_000, 200, 200))
            .ToArray();

        var admission = ImageAdmission.Select(candidates);
        var summary = admission.ToCoverageSummary(imagesProduced: 2, imagesNotProduced: 0);
        var row = ImageEvidence.ExplainCoverage(summary, DateTimeOffset.UnixEpoch);

        Assert.Equal(512, admission.ImageParts);
        Assert.Equal(2, admission.ImagesAdmitted);
        Assert.Equal(510, admission.ImagesOverBudget);
        Assert.Equal(3, admission.FirstOverBudgetOrdinal);
        Assert.Equal(512, admission.LastOverBudgetOrdinal);
        Assert.Equal(510, row.Value);
        Assert.Equal("512", Attribute(row, "imageParts"));
        Assert.Equal("0", Attribute(row, "imagesSkippedAsChrome"));
        Assert.Equal("2", Attribute(row, "imagesAdmitted"));
        Assert.Equal("2", Attribute(row, "imagesProduced"));
        Assert.Equal("0", Attribute(row, "imagesNotProduced"));
        Assert.Equal("510", Attribute(row, "imagesOverBudget"));
        Assert.Equal("3", Attribute(row, "firstOverBudgetOrdinal"));
        Assert.Equal("512", Attribute(row, "lastOverBudgetOrdinal"));
        Assert.Equal("inspection_budget_exhausted", Attribute(row, "reason"));
        Assert.True(row.Attributes!.Count <= ImageObservationContract.MaxCoverageAttributes);
    }

    [Fact]
    public void ImageEvidenceStaysWithinThirteenRowsForTwoAdmittedImages()
    {
        var clean = ImageTestBytes.LoadFixture("clean.png");
        var trailing = ImageInspector.Inspect(new ImageInspectRequest
        {
            Content = ImageTestBytes.Concat(clean, ImageTestBytes.ZipTail()),
            DeclaredContentType = "image/png",
        });

        var candidates = Enumerable.Range(1, 128)
            .Select(ordinal => Candidate(ordinal, 200_000 - ordinal, 200, 200))
            .ToArray();
        var admission = ImageAdmission.Select(candidates);
        var summary = admission.ToCoverageSummary(imagesProduced: 2, imagesNotProduced: 0);

        var rows = ImageEvidence.Explain(trailing, DateTimeOffset.UnixEpoch)
            .Concat(ImageEvidence.Explain(trailing, DateTimeOffset.UnixEpoch))
            .Append(ImageEvidence.ExplainCoverage(summary, DateTimeOffset.UnixEpoch))
            .ToList();

        Assert.Equal(ImageObservationContract.MaxEvidenceRowsPerMessage, 13);
        Assert.True(rows.Count <= ImageObservationContract.MaxEvidenceRowsPerMessage);
        Assert.Equal(128, admission.ImageParts);
        Assert.Equal(126, admission.ImagesOverBudget);
        Assert.Equal(3, admission.FirstOverBudgetOrdinal);
        Assert.Equal(128, admission.LastOverBudgetOrdinal);
    }

    [Fact]
    public void CoverageRowIsEmittedEvenWhenNothingIsOverBudget()
    {
        var admission = ImageAdmission.Select(
        [
            Candidate(1, 200_000, 800, 600),
            Candidate(2, 100, 1, 1, transparent: true),
        ]);

        var summary = admission.ToCoverageSummary(imagesProduced: 1, imagesNotProduced: 0);
        var row = ImageEvidence.ExplainCoverage(summary, DateTimeOffset.UnixEpoch);

        Assert.Equal(0, row.Value);
        Assert.Equal(EvidenceAvailability.Available, row.Availability);
        Assert.Null(Attribute(row, "reason"));
        Assert.Equal("2", Attribute(row, "imageParts"));
        Assert.Equal("1", Attribute(row, "imagesSkippedAsChrome"));
        Assert.Equal("1", Attribute(row, "imagesAdmitted"));
        Assert.Equal("0", Attribute(row, "imagesOverBudget"));
    }

    [Fact]
    public void CoverageSummaryInvariantsAreEnforced()
    {
        var bad = new ImageCoverageSummary
        {
            ImageParts = 3,
            ImagesSkippedAsChrome = 0,
            ImagesAdmitted = 1,
            ImagesProduced = 1,
            ImagesNotProduced = 0,
            ImagesOverBudget = 1,
        };

        Assert.Throws<ArgumentException>(() => ImageEvidence.ExplainCoverage(bad, DateTimeOffset.UnixEpoch));
    }

    private static string? Attribute(Evidence evidence, string name) =>
        evidence.Attributes?.FirstOrDefault(a => a.Name == name)?.Value;
}
