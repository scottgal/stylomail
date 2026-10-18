using StyloMail.Core;

namespace StyloMail.Image.Tests;

public sealed class ImageInspectorTests
{
    private static ImageInspectRequest Request(byte[] content, string? declaredType, long? totalLength = null) => new()
    {
        Content = content,
        DeclaredContentType = declaredType,
        Extent = totalLength is null ? ImageContentExtent.Complete : ImageContentExtent.PrefixTotalKnown,
        TotalLength = totalLength,
    };

    [Fact]
    public void CleanPngProducesNoFinding()
    {
        var bytes = ImageTestBytes.LoadFixture("clean.png");

        var result = ImageInspector.Inspect(Request(bytes, "image/png"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Null(result.NotProducedReason);
        Assert.Equal(EvidenceAvailability.Available, result.Availability);
        Assert.Equal(ImageFormat.Png, result.DeclaredFormat);
        Assert.Equal(ImageFormat.Png, result.DetectedFormat);
        Assert.False(result.FormatMismatch);
        Assert.Equal(3, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(6, result.PixelCount);
        Assert.Equal(bytes.Length, result.ImageEndOffset);
        Assert.Equal(0, result.TrailingByteCount);
        Assert.True(result.TrailingScanComplete);
        Assert.Empty(result.Findings);
        Assert.True(result.Coverage.HeaderParsed);
        Assert.True(result.Coverage.TerminatorFound);
        Assert.False(result.Coverage.Truncated);
        Assert.True(result.Decodable);
    }

    [Fact]
    public void CleanJpegProducesNoFinding()
    {
        var bytes = ImageTestBytes.LoadFixture("clean.jpg");

        var result = ImageInspector.Inspect(Request(bytes, "image/jpeg"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(EvidenceAvailability.Available, result.Availability);
        Assert.Equal(ImageFormat.Jpeg, result.DetectedFormat);
        Assert.Equal(3, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(6, result.PixelCount);
        Assert.Equal(bytes.Length, result.ImageEndOffset);
        Assert.Equal(0, result.TrailingByteCount);
        Assert.True(result.TrailingScanComplete);
        Assert.Empty(result.Findings);
        Assert.True(result.Decodable);
    }

    [Fact]
    public void PngWithAppendedZipIsTrailingDataAtTheExactOffset()
    {
        var clean = ImageTestBytes.LoadFixture("clean.png");
        var zip = ImageTestBytes.ZipTail();
        var bytes = ImageTestBytes.Concat(clean, zip);

        var result = ImageInspector.Inspect(Request(bytes, "image/png"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(clean.Length, result.ImageEndOffset);
        Assert.Equal(zip.Length, result.TrailingByteCount);
        Assert.True(result.TrailingScanComplete);

        var trailing = Assert.Single(result.Findings, f => f.Code == ImageFindingCode.TrailingData);
        Assert.Equal(clean.Length, trailing.Offset);

        var archive = Assert.Single(result.Findings, f => f.Code == ImageFindingCode.EmbeddedArchive);
        Assert.Equal(clean.Length, archive.Offset);
        Assert.Equal("zip_local_header", archive.Detail);
    }

    [Fact]
    public void JpegWithAppendedExecutableIsTrailingDataAndAnEmbeddedExecutable()
    {
        var clean = ImageTestBytes.LoadFixture("clean.jpg");
        var pe = ImageTestBytes.PeStub();
        var bytes = ImageTestBytes.Concat(clean, pe);

        var result = ImageInspector.Inspect(Request(bytes, "image/jpeg"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(clean.Length, result.ImageEndOffset);
        Assert.Equal(pe.Length, result.TrailingByteCount);

        var executable = Assert.Single(result.Findings, f => f.Code == ImageFindingCode.EmbeddedExecutable);
        Assert.Equal(clean.Length, executable.Offset);
        Assert.Equal("pe", executable.Detail);
    }

    [Fact]
    public void DeclaredPngWithJpegMagicIsAFormatMismatch()
    {
        var bytes = ImageTestBytes.LoadFixture("clean.jpg");

        var result = ImageInspector.Inspect(Request(bytes, "image/png"));

        Assert.Equal(ImageDisposition.NotProduced, result.Disposition);
        Assert.Equal(ImageNotProducedReason.FormatMismatch, result.NotProducedReason);
        Assert.Equal(EvidenceAvailability.Unavailable, result.Availability);
        Assert.True(result.FormatMismatch);
        Assert.Equal(ImageFormat.Png, result.DeclaredFormat);
        Assert.Equal(ImageFormat.Jpeg, result.DetectedFormat);
        Assert.Null(result.Width);
        Assert.Null(result.PixelCount);
        Assert.False(result.Decodable);

        var finding = Assert.Single(result.Findings, f => f.Code == ImageFindingCode.TypeMagicMismatch);
        Assert.Equal("declared=png;detected=jpeg", finding.Detail);
    }

    [Fact]
    public void DeclaredPngWithNoRecognisableSignatureIsAFormatMismatch()
    {
        var bytes = new byte[16];

        var result = ImageInspector.Inspect(Request(bytes, "image/png"));

        Assert.Equal(ImageNotProducedReason.FormatMismatch, result.NotProducedReason);
        Assert.True(result.FormatMismatch);
        Assert.Contains(result.Findings, f => f.Code == ImageFindingCode.TypeMagicMismatch);
    }

    [Fact]
    public void AbsurdPngDimensionsAreRefusedBeforeAnyAllocation()
    {
        // The input is tiny: the claim is 100,000 x 100,000 = 1e10 pixels.
        var bytes = ImageTestBytes.Png(100_000, 100_000);

        var result = ImageInspector.Inspect(Request(bytes, "image/png"));

        Assert.Equal(ImageDisposition.Refused, result.Disposition);
        Assert.Equal(EvidenceAvailability.Available, result.Availability);
        Assert.Null(result.NotProducedReason);
        Assert.Equal(100_000, result.Width);
        Assert.Equal(100_000, result.Height);
        Assert.Equal(10_000_000_000, result.PixelCount);
        Assert.False(result.Decodable);
        Assert.Null(result.ImageEndOffset);
        Assert.Contains(result.Findings, f => f.Code == ImageFindingCode.DimensionLimitExceeded);
        Assert.Contains(result.Findings, f => f.Code == ImageFindingCode.PixelLimitExceeded);
    }

    [Fact]
    public void PixelLimitIsEnforcedAndTheExactBoundaryIsAccepted()
    {
        var limits = new ImageInspectionLimits { MaxPixels = 100 };

        var refused = ImageInspector.Inspect(Request(ImageTestBytes.Png(20, 6), "image/png"), limits);
        Assert.Equal(ImageDisposition.Refused, refused.Disposition);
        Assert.Contains(refused.Findings, f => f.Code == ImageFindingCode.PixelLimitExceeded);

        var accepted = ImageInspector.Inspect(Request(ImageTestBytes.Png(10, 10), "image/png"), limits);
        Assert.Equal(ImageDisposition.Inspected, accepted.Disposition);
        Assert.Equal(100, accepted.PixelCount);
        Assert.DoesNotContain(accepted.Findings, f => f.Code == ImageFindingCode.PixelLimitExceeded);
    }

    [Fact]
    public void TruncatedPngHeaderIsANamedReasonAndTheFullFileIsTheControl()
    {
        var full = ImageTestBytes.LoadFixture("clean.png");
        var truncated = full[..24];

        var result = ImageInspector.Inspect(Request(truncated, "image/png"));

        Assert.Equal(ImageDisposition.NotProduced, result.Disposition);
        Assert.Equal(ImageNotProducedReason.TruncatedHeader, result.NotProducedReason);
        Assert.Equal(EvidenceAvailability.Unavailable, result.Availability);
        Assert.Null(result.Width);
        Assert.Empty(result.Findings);

        // The control: the same bytes untruncated are inspected, so the reason is about the cut,
        // not about the fixture or the format.
        var control = ImageInspector.Inspect(Request(full, "image/png"));
        Assert.Equal(ImageDisposition.Inspected, control.Disposition);
        Assert.Empty(control.Findings);
    }

    [Fact]
    public void TruncatedJpegHeaderIsANamedReason()
    {
        var full = ImageTestBytes.LoadFixture("clean.jpg");
        var truncated = full[..10];

        var result = ImageInspector.Inspect(Request(truncated, "image/jpeg"));

        Assert.Equal(ImageDisposition.NotProduced, result.Disposition);
        Assert.Equal(ImageNotProducedReason.TruncatedHeader, result.NotProducedReason);
        Assert.Equal(EvidenceAvailability.Unavailable, result.Availability);
    }

    [Fact]
    public void EmptyDeclaredImageIsEmptyContentWithoutAnException()
    {
        var result = ImageInspector.Inspect(Request([], "image/png"));

        Assert.Equal(ImageDisposition.NotProduced, result.Disposition);
        Assert.Equal(ImageNotProducedReason.EmptyContent, result.NotProducedReason);
        Assert.Equal(EvidenceAvailability.Unavailable, result.Availability);
        Assert.Equal(0, result.Coverage.InspectedBytes);
        Assert.Equal(0, result.Coverage.TotalBytes);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void OneByteDeclaredImageIsATruncatedHeaderWithoutAnException()
    {
        var result = ImageInspector.Inspect(Request([0x89], "image/png"));

        Assert.Equal(ImageDisposition.NotProduced, result.Disposition);
        Assert.Equal(ImageNotProducedReason.TruncatedHeader, result.NotProducedReason);
        Assert.Equal(EvidenceAvailability.Unavailable, result.Availability);
    }

    [Fact]
    public void TotalLengthFarLargerThanThePrefixIsReducedCoverageAndSaysTheScanIsIncomplete()
    {
        var bytes = ImageTestBytes.LoadFixture("clean.png");
        const long total = 10_000_000;

        var result = ImageInspector.Inspect(Request(bytes, "image/png", total));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(EvidenceAvailability.ReducedCoverage, result.Availability);
        Assert.True(result.Coverage.Truncated);
        Assert.Equal(bytes.Length, result.Coverage.InspectedBytes);
        Assert.Equal(total, result.Coverage.TotalBytes);
        Assert.Equal(bytes.Length, result.ImageEndOffset);
        Assert.Equal(total - bytes.Length, result.TrailingByteCount);
        Assert.False(result.TrailingScanComplete);
        Assert.Contains(result.Findings, f => f.Code == ImageFindingCode.TrailingData);
    }

    [Fact]
    public void WhenTheTerminatorIsOutsideThePrefixThereIsNoTrailingClaim()
    {
        var full = ImageTestBytes.LoadFixture("clean.png");
        var prefix = full[..40];

        var result = ImageInspector.Inspect(Request(prefix, "image/png", 10_000_000));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(EvidenceAvailability.ReducedCoverage, result.Availability);
        Assert.True(result.Coverage.Truncated);
        Assert.Equal(3, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Null(result.ImageEndOffset);
        Assert.Null(result.TrailingByteCount);
        Assert.False(result.TrailingScanComplete);
        Assert.True(result.Coverage.HeaderParsed);
        Assert.False(result.Coverage.TerminatorFound);
        Assert.DoesNotContain(result.Findings, f => f.Code == ImageFindingCode.TrailingData);
    }

    [Fact]
    public void PngWithSignatureInsideAChunkProducesNoFalsePositive()
    {
        // The ZIP signature is inside a tEXt chunk, which is part of the image, not trailing data.
        var bytes = ImageTestBytes.PngWithExtraChunk(3, 2, "tEXt", ImageTestBytes.ZipTail());

        var result = ImageInspector.Inspect(Request(bytes, "image/png"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(0, result.TrailingByteCount);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void HeaderWalkIsBoundedAndNamed()
    {
        var limits = new ImageInspectionLimits { MaxHeaderSegments = 1 };

        var result = ImageInspector.Inspect(Request(ImageTestBytes.Png(3, 2), "image/png"), limits);

        Assert.Equal(ImageDisposition.NotProduced, result.Disposition);
        Assert.Equal(ImageNotProducedReason.HeaderBudgetExceeded, result.NotProducedReason);
        Assert.Null(result.Width);
    }

    [Fact]
    public void SvgIsUnsupportedAndScriptableRatherThanParsed()
    {
        var svg = System.Text.Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

        var result = ImageInspector.Inspect(Request(svg, "image/svg+xml"));

        Assert.Equal(ImageDisposition.NotProduced, result.Disposition);
        Assert.Equal(ImageNotProducedReason.UnsupportedFormat, result.NotProducedReason);
        Assert.Equal(EvidenceAvailability.Unavailable, result.Availability);
        Assert.Equal(ImageFormat.Svg, result.DetectedFormat);
        Assert.Null(result.Width);
        var finding = Assert.Single(result.Findings, f => f.Code == ImageFindingCode.ScriptableFormat);
        Assert.Equal("svg", finding.Detail);
    }

    [Fact]
    public void GenericOctetStreamWithPngMagicIsInspected()
    {
        var bytes = ImageTestBytes.LoadFixture("clean.png");

        var result = ImageInspector.Inspect(Request(bytes, "application/octet-stream"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(ImageFormat.Unknown, result.DeclaredFormat);
        Assert.Equal(ImageFormat.Png, result.DetectedFormat);
        Assert.False(result.FormatMismatch);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void UnrecognisedContentIsNotAnImageAndNotAZero()
    {
        var result = ImageInspector.Inspect(Request("plain text, not an image"u8.ToArray(), null));

        Assert.Equal(ImageDisposition.NotProduced, result.Disposition);
        Assert.Equal(ImageNotProducedReason.NotAnImage, result.NotProducedReason);
        Assert.Equal(EvidenceAvailability.NotApplicable, result.Availability);
        Assert.Null(result.PixelCount);
    }

    [Fact]
    public void GifDimensionsComeFromTheLogicalScreenDescriptor()
    {
        var result = ImageInspector.Inspect(Request(ImageTestBytes.Gif(7, 5), "image/gif"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(7, result.Width);
        Assert.Equal(5, result.Height);
        Assert.Equal(35, result.PixelCount);
        Assert.Equal(0, result.TrailingByteCount);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void BmpDimensionsComeFromTheDibHeader()
    {
        var result = ImageInspector.Inspect(Request(ImageTestBytes.Bmp(4, 3), "image/bmp"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(4, result.Width);
        Assert.Equal(3, result.Height);
        Assert.Equal(0, result.TrailingByteCount);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void WebPDimensionsComeFromTheVp8xChunk()
    {
        var result = ImageInspector.Inspect(Request(ImageTestBytes.WebPVp8X(6, 4), "image/webp"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(6, result.Width);
        Assert.Equal(4, result.Height);
        Assert.Equal(0, result.TrailingByteCount);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void JpegDnlInsideTheScanStillFindsTheTerminator()
    {
        var bytes = ImageTestBytes.JpegWithDnl();

        var result = ImageInspector.Inspect(Request(bytes, "image/jpeg"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(3, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(bytes.Length, result.ImageEndOffset);
        Assert.Equal(0, result.TrailingByteCount);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void BmpUsesItsDeclaredFileSizeForTrailingData()
    {
        var bmp = ImageTestBytes.Bmp(4, 3);
        var zip = ImageTestBytes.ZipTail();

        var result = ImageInspector.Inspect(Request(ImageTestBytes.Concat(bmp, zip), "image/bmp"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(bmp.Length, result.ImageEndOffset);
        Assert.Equal(zip.Length, result.TrailingByteCount);
        Assert.Contains(result.Findings, f => f.Code == ImageFindingCode.EmbeddedArchive);
    }

    [Fact]
    public void WebPUsesItsRiffSizeForTrailingData()
    {
        var webp = ImageTestBytes.WebPVp8X(6, 4);
        var zip = ImageTestBytes.ZipTail();

        var result = ImageInspector.Inspect(Request(ImageTestBytes.Concat(webp, zip), "image/webp"));

        Assert.Equal(ImageDisposition.Inspected, result.Disposition);
        Assert.Equal(webp.Length, result.ImageEndOffset);
        Assert.Equal(zip.Length, result.TrailingByteCount);
        Assert.Contains(result.Findings, f => f.Code == ImageFindingCode.EmbeddedArchive);
    }

    [Theory]
    [InlineData("image/tiff", "tiff")]
    [InlineData("image/heic", "heic")]
    [InlineData("image/avif", "avif")]
    public void RecognisedButUnsupportedFormatsAreNamedRatherThanGuessed(string contentType, string kind)
    {
        var bytes = kind switch
        {
            "tiff" => ImageTestBytes.Tiff(),
            _ => ImageTestBytes.IsoBmff(kind),
        };

        var result = ImageInspector.Inspect(Request(bytes, contentType));

        Assert.Equal(ImageDisposition.NotProduced, result.Disposition);
        Assert.Equal(ImageNotProducedReason.UnsupportedFormat, result.NotProducedReason);
        Assert.Equal(EvidenceAvailability.Unavailable, result.Availability);
        Assert.Null(result.Width);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void ADeclaredNonImageWithImageMagicIsAMismatch()
    {
        var bytes = ImageTestBytes.LoadFixture("clean.png");

        var result = ImageInspector.Inspect(Request(bytes, "text/plain"));

        Assert.Equal(ImageNotProducedReason.FormatMismatch, result.NotProducedReason);
        Assert.Equal(ImageFormat.NonImage, result.DeclaredFormat);
        Assert.Equal(ImageFormat.Png, result.DetectedFormat);
        Assert.True(result.FormatMismatch);
    }

    [Fact]
    public void XhtmlWithAnInlineSvgIsNotAnImage()
    {
        var xhtml = System.Text.Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><html><body><svg xmlns=\"http://www.w3.org/2000/svg\"></svg></body></html>");

        var result = ImageInspector.Inspect(Request(xhtml, "application/xhtml+xml"));

        Assert.Equal(ImageDisposition.NotProduced, result.Disposition);
        Assert.Equal(ImageNotProducedReason.NotAnImage, result.NotProducedReason);
        Assert.Equal(EvidenceAvailability.NotApplicable, result.Availability);
        Assert.False(result.FormatMismatch);
    }

    [Fact]
    public void TextBeginningWithBmIsNotABitmap()
    {
        var text = System.Text.Encoding.ASCII.GetBytes("BMW is a car, not a bitmap");

        var result = ImageInspector.Inspect(Request(text, "text/plain"));

        Assert.Equal(ImageNotProducedReason.NotAnImage, result.NotProducedReason);
        Assert.Equal(ImageFormat.Unknown, result.DetectedFormat);
        Assert.False(result.FormatMismatch);
    }

    [Fact]
    public void TruncatedBmpIsATruncatedHeaderRatherThanAMismatch()
    {
        var bytes = new byte[10];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';

        var result = ImageInspector.Inspect(Request(bytes, "image/bmp"));

        Assert.Equal(ImageNotProducedReason.TruncatedHeader, result.NotProducedReason);
        Assert.False(result.FormatMismatch);
    }

    [Fact]
    public void EveryCutPointOfTheRealFixturesProducesAResultNeverAnException()
    {
        foreach (var (name, declared) in new[]
                 {
                     ("clean.png", "image/png"),
                     ("clean.jpg", "image/jpeg"),
                 })
        {
            var full = ImageTestBytes.LoadFixture(name);
            for (var cut = 0; cut <= full.Length; cut++)
            {
                var result = ImageInspector.Inspect(Request(full[..cut], declared));

                if (result.Disposition == ImageDisposition.NotProduced)
                {
                    Assert.NotNull(result.NotProducedReason);
                    Assert.Null(result.Width);
                    Assert.Null(result.PixelCount);
                }
                else
                {
                    Assert.Null(result.NotProducedReason);
                    Assert.NotNull(result.Width);
                    Assert.NotNull(result.PixelCount);
                }
            }
        }
    }

    [Fact]
    public void RandomBytesForEveryDeclaredTypeProduceAResultNeverAnException()
    {
        var random = new Random(20261018);
        string?[] declaredTypes =
        [
            null,
            "application/octet-stream",
            "text/plain",
            "image/png",
            "image/jpeg",
            "image/gif",
            "image/bmp",
            "image/webp",
            "image/tiff",
            "image/heic",
            "image/avif",
            "image/svg+xml",
        ];

        for (var iteration = 0; iteration < 500; iteration++)
        {
            var bytes = new byte[random.Next(0, 256)];
            random.NextBytes(bytes);

            foreach (var declared in declaredTypes)
            {
                var result = ImageInspector.Inspect(Request(bytes, declared));

                if (result.Disposition == ImageDisposition.NotProduced)
                {
                    Assert.NotNull(result.NotProducedReason);
                    Assert.Null(result.PixelCount);
                }
            }
        }
    }

    [Fact]
    public void TotalLengthSmallerThanContentIsACallerContractViolation()
    {
        var bytes = new byte[4];

        Assert.Throws<ArgumentException>(() =>
            ImageInspector.Inspect(Request(bytes, "image/png", totalLength: 2)));
    }
}
