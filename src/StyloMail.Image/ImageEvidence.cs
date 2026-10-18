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
                    Of("totalBytesKnown", result.Coverage.TotalBytesKnown),
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

            AddSignatures(attributes, result.Findings);

            inspected.Add(builder.Build(
                ImageSignals.TrailingData,
                result.TrailingScanComplete ? EvidenceAvailability.Available : EvidenceAvailability.ReducedCoverage,
                value: trailing,
                attributes: attributes));
        }
        else if (result.TrailingBytesObserved is { } observed)
        {
            // The total length is unknown, so only a lower bound is available. The value stays
            // null: rendering the observed prefix as a count would turn a lower bound into a claim.
            var attributes = new List<EvidenceAttribute>
            {
                Of("imageEndOffset", result.ImageEndOffset),
                Of("trailingBytes", "unknown"),
                Of("observedTrailingBytes", observed),
                Of("scanComplete", false),
            };

            AddSignatures(attributes, result.Findings);

            inspected.Add(builder.Build(
                ImageSignals.TrailingData,
                EvidenceAvailability.ReducedCoverage,
                value: null,
                attributes: attributes));
        }

        return inspected;
    }

    /// <summary>
    /// Emits the single per-message coverage row. It is produced whenever the message has image
    /// parts, including when nothing was over budget, so the denominator and the chrome filter are
    /// always visible.
    /// </summary>
    /// <remarks>
    /// Attributes are capped at 9 with no truncation of the count fields: the invariants
    /// <c>imageParts = imagesSkippedAsChrome + imagesAdmitted + imagesOverBudget</c> and
    /// <c>imagesAdmitted = imagesProduced + imagesNotProduced</c> must survive into the ledger.
    /// </remarks>
    public static Evidence ExplainCoverage(
        ImageCoverageSummary summary,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(summary);
        summary.Validate();

        var builder = new EvidenceBuilder(
            observedAt,
            ImageSignals.SourceVersion,
            new EvidenceAttributeLimits { MaxEntries = 9, MaxValueLength = 128 });

        var attributes = new List<EvidenceAttribute>
        {
            Of("imageParts", summary.ImageParts),
            Of("imagesSkippedAsChrome", summary.ImagesSkippedAsChrome),
            Of("imagesAdmitted", summary.ImagesAdmitted),
            Of("imagesProduced", summary.ImagesProduced),
            Of("imagesNotProduced", summary.ImagesNotProduced),
            Of("imagesOverBudget", summary.ImagesOverBudget),
            Of("firstOverBudgetOrdinal", summary.FirstOverBudgetOrdinal),
            Of("lastOverBudgetOrdinal", summary.LastOverBudgetOrdinal),
        };

        if (summary.ImagesOverBudget > 0)
        {
            attributes.Add(Of(
                "reason",
                ImageNotProducedReason.InspectionBudgetExhausted.ToWireName()));
        }

        return builder.Build(
            ImageSignals.InspectionCoverage,
            EvidenceAvailability.Available,
            value: summary.ImagesOverBudget,
            attributes: attributes);
    }

    private static void AddSignatures(List<EvidenceAttribute> attributes, IReadOnlyList<ImageFinding> findings)
    {
        foreach (var finding in findings)
        {
            if (finding.Code is not (ImageFindingCode.EmbeddedArchive or ImageFindingCode.EmbeddedExecutable))
            {
                continue;
            }

            attributes.Add(Of("signature", $"{finding.Detail ?? finding.Code.ToWireName()}@{finding.Offset?.ToString() ?? "unknown"}"));
        }
    }

    private static EvidenceAttribute Of(string name, string value) => new() { Name = name, Value = value };

    private static EvidenceAttribute Of(string name, long? value) => new() { Name = name, Value = value?.ToString() ?? "unknown" };

    private static EvidenceAttribute Of(string name, bool value) => new() { Name = name, Value = value ? "true" : "false" };
}
