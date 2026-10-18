using StyloMail.Core;

namespace StyloMail.Image;

/// <summary>
/// Turns an inspection result into deterministic evidence rows.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is deliberately one image at a time. Aggregating across a message belongs with the
/// call site that receives the parts, so an image that produced nothing cannot be averaged away by
/// a clean neighbour before the ledger sees it.
/// </para>
/// <para>
/// A measurement that did not happen is a row with a null value and a named reason. In particular
/// <see cref="ImageSignals.Inspection"/> never carries <c>0.0</c>: that number is a real measured
/// result for <see cref="ImageSignals.FormatMismatch"/> and
/// <see cref="ImageSignals.LimitsExceeded"/>, and reusing it for absence would make the two
/// indistinguishable downstream.
/// </para>
/// </remarks>
public static class ImageEvidence
{
    public static IReadOnlyList<Evidence> Explain(
        ImageInspectionResult result,
        DateTimeOffset observedAt,
        EvidenceAttributeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new EvidenceBuilder(observedAt, ImageSignals.SourceVersion, limits ?? new EvidenceAttributeLimits());
        var declaredType = result.DeclaredContentType ?? string.Empty;
        var declaredFormat = result.DeclaredFormat.ToWireName();
        var detectedFormat = result.DetectedFormat.ToWireName();

        if (result.Disposition == ImageDisposition.NotProduced)
        {
            var rows = new List<Evidence>(3)
            {
                builder.Build(
                    ImageSignals.Inspection,
                    result.Availability,
                    value: null,
                    attributes:
                    [
                        Of("disposition", result.Disposition.ToWireName()),
                        Of("reason", result.NotProducedReason?.ToWireName() ?? "unspecified"),
                        Of("declaredType", declaredType),
                        Of("declaredFormat", declaredFormat),
                        Of("detectedFormat", detectedFormat),
                        Of("inspectedBytes", result.Coverage.InspectedBytes),
                        Of("totalBytes", result.Coverage.TotalBytes),
                        Of("truncated", result.Coverage.Truncated),
                    ]),
            };

            if (result.FormatMismatch)
            {
                rows.Add(builder.Build(
                    ImageSignals.FormatMismatch,
                    EvidenceAvailability.Available,
                    value: 1.0,
                    attributes:
                    [
                        Of("declaredType", declaredType),
                        Of("declaredFormat", declaredFormat),
                        Of("detectedFormat", detectedFormat),
                    ]));
            }

            if (result.Findings.Any(f => f.Code == ImageFindingCode.ScriptableFormat))
            {
                rows.Add(builder.Build(
                    ImageSignals.ScriptableFormat,
                    EvidenceAvailability.Available,
                    value: 1.0,
                    attributes: [Of("format", detectedFormat)]));
            }

            return rows;
        }

        var inspected = new List<Evidence>(5)
        {
            builder.Build(
                ImageSignals.Inspection,
                result.Availability,
                value: null,
                attributes:
                [
                    Of("disposition", result.Disposition.ToWireName()),
                    Of("headerParsed", result.Coverage.HeaderParsed),
                    Of("terminatorFound", result.Coverage.TerminatorFound),
                    Of("inspectedBytes", result.Coverage.InspectedBytes),
                    Of("totalBytes", result.Coverage.TotalBytes),
                    Of("truncated", result.Coverage.Truncated),
                ]),
            builder.Build(
                ImageSignals.Structure,
                result.Availability,
                value: result.PixelCount,
                attributes:
                [
                    Of("format", detectedFormat),
                    Of("declaredFormat", declaredFormat),
                    Of("width", result.Width),
                    Of("height", result.Height),
                    Of("imageEndOffset", result.ImageEndOffset),
                    Of("disposition", result.Disposition.ToWireName()),
                ]),
            builder.Build(
                ImageSignals.FormatMismatch,
                EvidenceAvailability.Available,
                value: result.FormatMismatch ? 1.0 : 0.0,
                attributes:
                [
                    Of("declaredFormat", declaredFormat),
                    Of("detectedFormat", detectedFormat),
                ]),
            builder.Build(
                ImageSignals.LimitsExceeded,
                EvidenceAvailability.Available,
                value: result.Disposition == ImageDisposition.Refused ? 1.0 : 0.0,
                attributes:
                [
                    Of("width", result.Width),
                    Of("height", result.Height),
                    Of("pixels", result.PixelCount),
                ]),
        };

        if (result.TrailingByteCount is { } trailing)
        {
            var attributes = new List<EvidenceAttribute>
            {
                Of("imageEndOffset", result.ImageEndOffset),
                Of("trailingBytes", trailing),
                Of("scanComplete", result.TrailingScanComplete),
            };

            foreach (var finding in result.Findings)
            {
                if (finding.Code is not (ImageFindingCode.EmbeddedArchive or ImageFindingCode.EmbeddedExecutable))
                {
                    continue;
                }

                attributes.Add(Of("signature", $"{finding.Detail ?? finding.Code.ToWireName()}@{finding.Offset?.ToString() ?? "unknown"}"));
            }

            inspected.Add(builder.Build(
                ImageSignals.TrailingData,
                result.TrailingScanComplete ? EvidenceAvailability.Available : EvidenceAvailability.ReducedCoverage,
                value: trailing,
                attributes: attributes));
        }

        return inspected;
    }

    private static EvidenceAttribute Of(string name, string value) => new() { Name = name, Value = value };

    private static EvidenceAttribute Of(string name, long? value) => new() { Name = name, Value = value?.ToString() ?? "unknown" };

    private static EvidenceAttribute Of(string name, bool value) => new() { Name = name, Value = value ? "true" : "false" };
}
