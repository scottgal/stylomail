using StyloMail.Core;

namespace StyloMail.Image.Tests;

public sealed class ImageEvidenceTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static ImageInspectRequest Request(byte[] content, string? declaredType, long? totalLength = null) => new()
    {
        Content = content,
        DeclaredContentType = declaredType,
        Extent = totalLength is null ? ImageContentExtent.Complete : ImageContentExtent.PrefixTotalKnown,
        TotalLength = totalLength,
    };

    [Fact]
    public void CleanPngEmitsMeasuredRowsIncludingRealZeros()
    {
        var bytes = ImageTestBytes.LoadFixture("clean.png");
        var result = ImageInspector.Inspect(Request(bytes, "image/png"));

        var rows = ImageEvidence.Explain(result, ObservedAt);

        var structure = Assert.Single(rows, e => e.SignalId == ImageSignals.Structure);
        Assert.Equal(EvidenceAvailability.Available, structure.Availability);
        Assert.Equal(6, structure.Value);

        var mismatch = Assert.Single(rows, e => e.SignalId == ImageSignals.FormatMismatch);
        Assert.Equal(EvidenceAvailability.Available, mismatch.Availability);
        Assert.Equal(0, mismatch.Value);

        var limits = Assert.Single(rows, e => e.SignalId == ImageSignals.LimitsExceeded);
        Assert.Equal(EvidenceAvailability.Available, limits.Availability);
        Assert.Equal(0, limits.Value);

        var trailing = Assert.Single(rows, e => e.SignalId == ImageSignals.TrailingData);
        Assert.Equal(EvidenceAvailability.Available, trailing.Availability);
        Assert.Equal(0, trailing.Value);
        Assert.Equal("true", Attribute(trailing, "scanComplete"));

        Assert.All(rows, row =>
        {
            Assert.Equal(EvidenceOrigin.Deterministic, row.Origin);
            Assert.Equal(ImageSignals.SourceVersion, row.SourceVersion);
            Assert.Equal(ObservedAt, row.ObservedAt);
        });
    }

    [Fact]
    public void TrailingZipRowCarriesTheOffsetAndSignature()
    {
        var clean = ImageTestBytes.LoadFixture("clean.png");
        var zip = ImageTestBytes.ZipTail();
        var result = ImageInspector.Inspect(Request(ImageTestBytes.Concat(clean, zip), "image/png"));

        var rows = ImageEvidence.Explain(result, ObservedAt);
        var trailing = Assert.Single(rows, e => e.SignalId == ImageSignals.TrailingData);

        Assert.Equal(zip.Length, trailing.Value);
        Assert.Equal(clean.Length.ToString(), Attribute(trailing, "imageEndOffset"));
        Assert.Equal(zip.Length.ToString(), Attribute(trailing, "trailingBytes"));
        Assert.Equal("true", Attribute(trailing, "scanComplete"));
        Assert.Contains(
            trailing.Attributes ?? [],
            a => a.Name == "signature" && a.Value.StartsWith("zip_local_header@", StringComparison.Ordinal));
    }

    [Fact]
    public void NotProducedIsUnavailableAndCarriesAReasonWithANullValue()
    {
        var jpeg = ImageTestBytes.LoadFixture("clean.jpg");
        var result = ImageInspector.Inspect(Request(jpeg, "image/png"));

        var rows = ImageEvidence.Explain(result, ObservedAt);
        var inspection = Assert.Single(rows, e => e.SignalId == ImageSignals.Inspection);

        Assert.Equal(EvidenceAvailability.Unavailable, inspection.Availability);
        Assert.Null(inspection.Value);
        Assert.Equal("format_mismatch", Attribute(inspection, "reason"));
        Assert.Equal("not_produced", Attribute(inspection, "disposition"));

        var mismatch = Assert.Single(rows, e => e.SignalId == ImageSignals.FormatMismatch);
        Assert.Equal(EvidenceAvailability.Available, mismatch.Availability);
        Assert.Equal(1, mismatch.Value);
    }

    [Fact]
    public void SvgEmitsUnsupportedWithANullValueAndAScriptableFormatRow()
    {
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
        var result = ImageInspector.Inspect(Request(svg, "image/svg+xml"));

        var rows = ImageEvidence.Explain(result, ObservedAt);
        var inspection = Assert.Single(rows, e => e.SignalId == ImageSignals.Inspection);
        Assert.Equal(EvidenceAvailability.Unavailable, inspection.Availability);
        Assert.Null(inspection.Value);
        Assert.Equal("unsupported_format", Attribute(inspection, "reason"));

        var scriptable = Assert.Single(rows, e => e.SignalId == ImageSignals.ScriptableFormat);
        Assert.Equal(EvidenceAvailability.Available, scriptable.Availability);
        Assert.Equal(1, scriptable.Value);
    }

    [Fact]
    public void PrefixCoverageIsVisibleOnTheInspectionRow()
    {
        var prefix = ImageTestBytes.LoadFixture("clean.png")[..40];
        var result = ImageInspector.Inspect(Request(prefix, "image/png", 10_000_000));

        var rows = ImageEvidence.Explain(result, ObservedAt);
        var inspection = Assert.Single(rows, e => e.SignalId == ImageSignals.Inspection);

        Assert.Equal(EvidenceAvailability.ReducedCoverage, inspection.Availability);
        Assert.Equal("true", Attribute(inspection, "truncated"));
        Assert.Equal("10000000", Attribute(inspection, "totalBytes"));
        Assert.Equal("40", Attribute(inspection, "inspectedBytes"));

        // No terminator was found inside the prefix, so no trailing row is fabricated.
        Assert.DoesNotContain(rows, e => e.SignalId == ImageSignals.TrailingData);
    }

    [Fact]
    public void TrailingCountFromAPrefixIsExactButTheScanIsReducedCoverage()
    {
        var clean = ImageTestBytes.LoadFixture("clean.png");
        const long total = 10_000_000;
        var result = ImageInspector.Inspect(Request(clean, "image/png", total));

        var rows = ImageEvidence.Explain(result, ObservedAt);
        var trailing = Assert.Single(rows, e => e.SignalId == ImageSignals.TrailingData);

        Assert.Equal(EvidenceAvailability.ReducedCoverage, trailing.Availability);
        Assert.Equal(total - clean.Length, trailing.Value);
        Assert.Equal("false", Attribute(trailing, "scanComplete"));
        Assert.Equal(clean.Length.ToString(), Attribute(trailing, "imageEndOffset"));
    }

    [Fact]
    public void RefusedDimensionsAreAnAvailableFactAndAPricedRefusal()
    {
        var result = ImageInspector.Inspect(Request(ImageTestBytes.Png(100_000, 100_000), "image/png"));

        var rows = ImageEvidence.Explain(result, ObservedAt);
        var limits = Assert.Single(rows, e => e.SignalId == ImageSignals.LimitsExceeded);
        Assert.Equal(EvidenceAvailability.Available, limits.Availability);
        Assert.Equal(1, limits.Value);

        var structure = Assert.Single(rows, e => e.SignalId == ImageSignals.Structure);
        Assert.Equal("refused", Attribute(structure, "disposition"));
        Assert.Equal("100000", Attribute(structure, "width"));
        Assert.Equal("100000", Attribute(structure, "height"));
    }

    private static string? Attribute(Evidence evidence, string name) =>
        evidence.Attributes?.FirstOrDefault(a => a.Name == name)?.Value;
}
