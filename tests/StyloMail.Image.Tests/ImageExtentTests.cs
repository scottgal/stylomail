using StyloMail.Core;

namespace StyloMail.Image.Tests;

public sealed class ImageExtentTests
{
    private static ImageInspectRequest Request(
        byte[] content,
        string? declaredType,
        ImageContentExtent extent,
        long? totalLength = null) => new()
        {
            Content = content,
            DeclaredContentType = declaredType,
            Extent = extent,
            TotalLength = totalLength,
        };

    private static ImageInspectionLimits CallSiteLimits => new()
    {
        MaxInspectedBytes = ImageObservationContract.PerImageInspectedBytes,
        MaxHeaderBytes = ImageObservationContract.PerImageInspectedBytes,
    };

    [Fact]
    public void CompleteContentMustNotCarryATotalLength()
    {
        var bytes = ImageTestBytes.LoadFixture("clean.png");

        Assert.Throws<ArgumentException>(() => ImageInspector.Inspect(
            Request(bytes, "image/png", ImageContentExtent.Complete, bytes.Length)));
    }

    [Fact]
    public void PrefixTotalKnownRequiresATotalLength()
    {
        var bytes = ImageTestBytes.LoadFixture("clean.png");

        Assert.Throws<ArgumentException>(() => ImageInspector.Inspect(
            Request(bytes, "image/png", ImageContentExtent.PrefixTotalKnown)));
    }

    [Fact]
    public void PrefixTotalUnknownMustNotCarryATotalLength()
    {
        var bytes = ImageTestBytes.LoadFixture("clean.png");

        Assert.Throws<ArgumentException>(() => ImageInspector.Inspect(
            Request(bytes, "image/png", ImageContentExtent.PrefixTotalUnknown, bytes.Length)));
    }

    [Fact]
    public void PrefixTotalKnownMustNotBeSmallerThanTheContent()
    {
        var bytes = ImageTestBytes.LoadFixture("clean.png");

        Assert.Throws<ArgumentException>(() => ImageInspector.Inspect(
            Request(bytes, "image/png", ImageContentExtent.PrefixTotalKnown, bytes.Length - 1)));
    }

    [Fact]
    public void CompleteEmptyContentIsEmptyContent()
    {
        var result = ImageInspector.Inspect(Request([], "image/png", ImageContentExtent.Complete));

        Assert.Equal(ImageNotProducedReason.EmptyContent, result.NotProducedReason);
        Assert.True(result.Coverage.TotalBytesKnown);
        Assert.Equal(0, result.Coverage.TotalBytes);
    }

    [Fact]
    public void PrefixEmptyContentIsInsufficientDecodedLength()
    {
        var result = ImageInspector.Inspect(Request([], "image/png", ImageContentExtent.PrefixTotalUnknown));

        Assert.Equal(ImageNotProducedReason.InsufficientDecodedLength, result.NotProducedReason);
        Assert.False(result.Coverage.TotalBytesKnown);
        Assert.Null(result.Coverage.TotalBytes);
        Assert.True(result.Coverage.Truncated);
    }

    [Fact]
    public void PrefixUnknownTotalNeverClaimsATotalOrAnExactTrailingCount()
    {
        var clean = ImageTestBytes.LoadFixture("clean.png");

        var result = ImageInspector.Inspect(
            Request(clean, "image/png", ImageContentExtent.PrefixTotalUnknown));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(EvidenceAvailability.ReducedCoverage, result.Availability);
        Assert.Null(result.Coverage.TotalBytes);
        Assert.False(result.Coverage.TotalBytesKnown);
        Assert.True(result.Coverage.Truncated);
        Assert.Equal(clean.Length, result.ImageEndOffset);
        Assert.Null(result.TrailingByteCount);
        Assert.Equal(0, result.TrailingBytesObserved);
        Assert.False(result.TrailingScanComplete);
        Assert.DoesNotContain(result.Findings, f => f.Code == ImageFindingCode.TrailingData);
    }

    [Fact]
    public void PrefixUnknownTotalReportsObservedTrailingBytesAsALowerBound()
    {
        var clean = ImageTestBytes.LoadFixture("clean.png");
        var zip = ImageTestBytes.ZipTail();
        var bytes = ImageTestBytes.Concat(clean, zip);

        var result = ImageInspector.Inspect(
            Request(bytes, "image/png", ImageContentExtent.PrefixTotalUnknown));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Null(result.TrailingByteCount);
        Assert.Equal(zip.Length, result.TrailingBytesObserved);
        Assert.False(result.TrailingScanComplete);

        var trailing = Assert.Single(result.Findings, f => f.Code == ImageFindingCode.TrailingData);
        Assert.Equal(clean.Length, trailing.Offset);
        Assert.Equal($"observedBytes={zip.Length};total=unknown", trailing.Detail);
    }

    [Fact]
    public void PrefixKnownTotalKeepsTheExactTrailingCount()
    {
        var clean = ImageTestBytes.LoadFixture("clean.png");
        var zip = ImageTestBytes.ZipTail();
        var bytes = ImageTestBytes.Concat(clean, zip);

        var result = ImageInspector.Inspect(
            Request(bytes, "image/png", ImageContentExtent.PrefixTotalKnown, bytes.Length));

        Assert.Equal(zip.Length, result.TrailingByteCount);
        Assert.Null(result.TrailingBytesObserved);
        Assert.True(result.TrailingScanComplete);
    }

    [Fact]
    public void JpegSofBeyondAPrefixIsTruncatedHeaderAnd256KiBReachesIt()
    {
        var full = ImageTestBytes.LoadFixture("exif-heavy.jpg");

        // The fixture has ~192 KiB of APP1 before SOF; the first SOF is at 196,767.
        var shortPrefix = full[..131_072]; // 128 KiB
        var truncated = ImageInspector.Inspect(
            Request(shortPrefix, "image/jpeg", ImageContentExtent.PrefixTotalKnown, full.Length),
            CallSiteLimits);

        Assert.Equal(ImageDisposition.NotProduced, truncated.Disposition);
        Assert.Equal(ImageNotProducedReason.TruncatedHeader, truncated.NotProducedReason);
        Assert.True(truncated.Coverage.Truncated);
        Assert.Null(truncated.Width);

        // 256 KiB reaches the SOF and produces the dimensions.
        var reached = ImageInspector.Inspect(
            Request(full, "image/jpeg", ImageContentExtent.Complete),
            CallSiteLimits);

        Assert.Equal(ImageDisposition.Inspected, reached.Disposition);
        Assert.Equal(3, reached.Width);
        Assert.Equal(2, reached.Height);
        Assert.Equal(EvidenceAvailability.Available, reached.Availability);
    }

    [Fact]
    public void PrefixUnknownTrailingEvidenceKeepsTheValueNullAndTheLowerBoundVisible()
    {
        var clean = ImageTestBytes.LoadFixture("clean.png");
        var zip = ImageTestBytes.ZipTail();
        var result = ImageInspector.Inspect(
            Request(ImageTestBytes.Concat(clean, zip), "image/png", ImageContentExtent.PrefixTotalUnknown));

        var rows = ImageEvidence.Explain(result, DateTimeOffset.UnixEpoch);
        var trailing = Assert.Single(rows, e => e.SignalId == ImageSignals.TrailingData);

        Assert.Equal(EvidenceAvailability.ReducedCoverage, trailing.Availability);
        Assert.Null(trailing.Value);
        Assert.Equal("unknown", Attribute(trailing, "trailingBytes"));
        Assert.Equal(zip.Length.ToString(), Attribute(trailing, "observedTrailingBytes"));
        Assert.Equal("false", Attribute(trailing, "scanComplete"));
    }

    [Fact]
    public void TransparencyIsDeclaredOnlyWhenTheHeaderSaysSo()
    {
        var png = ImageInspector.Inspect(
            Request(ImageTestBytes.LoadFixture("clean.png"), "image/png", ImageContentExtent.Complete));
        Assert.False(png.TransparencyDeclared);

        var jpeg = ImageInspector.Inspect(
            Request(ImageTestBytes.LoadFixture("clean.jpg"), "image/jpeg", ImageContentExtent.Complete));
        Assert.False(jpeg.TransparencyDeclared);

        var gif = ImageInspector.Inspect(
            Request(ImageTestBytes.GifTransparent(2, 2), "image/gif", ImageContentExtent.Complete));
        Assert.True(gif.TransparencyDeclared);
    }

    private static string? Attribute(Evidence evidence, string name) =>
        evidence.Attributes?.FirstOrDefault(a => a.Name == name)?.Value;
}
