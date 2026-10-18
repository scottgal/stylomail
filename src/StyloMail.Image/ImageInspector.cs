using System.Buffers.Binary;
using StyloMail.Core;

namespace StyloMail.Image;

/// <summary>
/// Bounded, deterministic inspection of image bytes. Pure: no I/O, no rendering, no decoding and no
/// retained content.
/// </summary>
/// <remarks>
/// <para>
/// The inspector answers three questions and refuses to answer a fourth. It identifies the
/// container from magic bytes, reads dimensions from the header before anything could allocate
/// from them, and locates the container terminator so bytes after it can be reported. It does not
/// decompress pixels, and it does not scan compressed data for signatures: a few random bytes
/// inside entropy-coded data match <c>MZ</c> or <c>PK</c> often enough to manufacture findings.
/// </para>
/// <para>
/// A missing answer is a <see cref="ImageDisposition.NotProduced"/> result with a named
/// <see cref="ImageNotProducedReason"/> and an availability that is not
/// <see cref="EvidenceAvailability.Available"/>. It is never a zero and never an exception.
/// </para>
/// </remarks>
public static class ImageInspector
{
    private static readonly (byte[] Magic, ImageFindingCode Code, string Detail)[] TrailingSignatures =
    [
        ([0x50, 0x4B, 0x03, 0x04], ImageFindingCode.EmbeddedArchive, "zip_local_header"),
        ([0x50, 0x4B, 0x05, 0x06], ImageFindingCode.EmbeddedArchive, "zip_end_of_central_directory"),
        ([0x50, 0x4B, 0x07, 0x08], ImageFindingCode.EmbeddedArchive, "zip_spanned"),
        ([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07], ImageFindingCode.EmbeddedArchive, "rar"),
        ([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], ImageFindingCode.EmbeddedArchive, "7z"),
        ([0x1F, 0x8B, 0x08], ImageFindingCode.EmbeddedArchive, "gzip"),
    ];

    public static ImageInspectionResult Inspect(
        ImageInspectRequest request,
        ImageInspectionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= new ImageInspectionLimits();
        Validate(limits);

        var content = request.Content;
        var extent = request.Extent;
        long? total;
        switch (extent)
        {
            case ImageContentExtent.Complete:
                if (request.TotalLength is not null)
                {
                    throw new ArgumentException(
                        "Complete content must not carry a TotalLength; use PrefixTotalKnown or PrefixTotalUnknown for a prefix.",
                        nameof(request));
                }

                total = content.Length;
                break;

            case ImageContentExtent.PrefixTotalKnown:
                if (request.TotalLength is not { } knownTotal)
                {
                    throw new ArgumentException(
                        "PrefixTotalKnown requires a TotalLength.",
                        nameof(request));
                }

                if (knownTotal < content.Length)
                {
                    throw new ArgumentException(
                        "TotalLength cannot be smaller than the content supplied.",
                        nameof(request));
                }

                total = knownTotal;
                break;

            case ImageContentExtent.PrefixTotalUnknown:
                if (request.TotalLength is not null)
                {
                    throw new ArgumentException(
                        "PrefixTotalUnknown must not carry a TotalLength; an unknown total cannot be encoded as the prefix length.",
                        nameof(request));
                }

                total = null;
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    extent,
                    "Unsupported image content extent.");
        }

        var inspectedBytes = Math.Min(content.Length, limits.MaxInspectedBytes);
        var bytes = content.Span[..inspectedBytes];
        var truncated = total is null || total > inspectedBytes;
        var coverage = new ImageInspectionCoverage
        {
            InspectedBytes = inspectedBytes,
            TotalBytes = total,
            TotalBytesKnown = total.HasValue,
            Truncated = truncated,
            HeaderParsed = false,
            TerminatorFound = false,
        };

        var declared = ImageFormats.Declared(request.DeclaredContentType);

        if (content.Length == 0)
        {
            return NotProduced(
                request,
                declared,
                ImageFormat.Unknown,
                mismatch: false,
                extent == ImageContentExtent.Complete
                    ? ImageNotProducedReason.EmptyContent
                    : ImageNotProducedReason.InsufficientDecodedLength,
                EvidenceAvailability.Unavailable,
                coverage,
                []);
        }

        var detected = ImageFormats.Detect(bytes);
        var findings = new List<ImageFinding>();
        var specificImageClaim = declared is ImageFormat.Png or ImageFormat.Jpeg or ImageFormat.Gif
            or ImageFormat.Bmp or ImageFormat.WebP or ImageFormat.Tiff or ImageFormat.Heic
            or ImageFormat.Avif or ImageFormat.Svg or ImageFormat.Ico;

        if (declared == ImageFormat.NonImage)
        {
            if (detected == ImageFormat.Unknown)
            {
                return NotProduced(
                    request,
                    declared,
                    detected,
                    mismatch: false,
                    ImageNotProducedReason.NotAnImage,
                    EvidenceAvailability.NotApplicable,
                    coverage,
                    []);
            }

            AddMismatch(findings, declared, detected);
            return NotProduced(
                request,
                declared,
                detected,
                mismatch: true,
                ImageNotProducedReason.FormatMismatch,
                EvidenceAvailability.Unavailable,
                coverage,
                CapFindings(findings, limits.MaxFindings));
        }

        if (specificImageClaim && detected == ImageFormat.Unknown)
        {
            // If the prefix is shorter than the format's own signature, the bytes never had a
            // chance to disagree. A truncated header is the honest answer, not a mismatch.
            if (bytes.Length < MinimumSignatureBytes(declared))
            {
                return NotProduced(
                    request,
                    declared,
                    detected,
                    mismatch: false,
                    ImageNotProducedReason.TruncatedHeader,
                    EvidenceAvailability.Unavailable,
                    coverage,
                    []);
            }

            AddMismatch(findings, declared, detected);
            return NotProduced(
                request,
                declared,
                detected,
                mismatch: true,
                ImageNotProducedReason.FormatMismatch,
                EvidenceAvailability.Unavailable,
                coverage,
                CapFindings(findings, limits.MaxFindings));
        }

        if (specificImageClaim && detected != declared)
        {
            AddMismatch(findings, declared, detected);
            return NotProduced(
                request,
                declared,
                detected,
                mismatch: true,
                ImageNotProducedReason.FormatMismatch,
                EvidenceAvailability.Unavailable,
                coverage,
                CapFindings(findings, limits.MaxFindings));
        }

        if (declared == ImageFormat.OtherImage && detected == ImageFormat.Unknown)
        {
            return NotProduced(
                request,
                declared,
                detected,
                mismatch: false,
                ImageNotProducedReason.UnsupportedFormat,
                EvidenceAvailability.Unavailable,
                coverage,
                []);
        }

        if (detected == ImageFormat.Unknown)
        {
            return NotProduced(
                request,
                declared,
                detected,
                mismatch: false,
                ImageNotProducedReason.NotAnImage,
                EvidenceAvailability.NotApplicable,
                coverage,
                []);
        }

        if (!IsSupportedForStructure(detected))
        {
            if (detected == ImageFormat.Svg)
            {
                findings.Add(new ImageFinding
                {
                    Code = ImageFindingCode.ScriptableFormat,
                    Detail = "svg",
                });
            }

            return NotProduced(
                request,
                declared,
                detected,
                mismatch: false,
                ImageNotProducedReason.UnsupportedFormat,
                EvidenceAvailability.Unavailable,
                coverage,
                CapFindings(findings, limits.MaxFindings));
        }

        var structure = ImageStructureReader.Read(detected, bytes, limits, truncated);
        if (structure.ZeroDimensions)
        {
            findings.Add(new ImageFinding { Code = ImageFindingCode.ZeroDimensions });
        }

        if (structure.DimensionLimitExceeded)
        {
            findings.Add(new ImageFinding { Code = ImageFindingCode.DimensionLimitExceeded });
        }

        if (structure.PixelLimitExceeded)
        {
            findings.Add(new ImageFinding { Code = ImageFindingCode.PixelLimitExceeded });
        }

        coverage = coverage with
        {
            HeaderParsed = structure.Width.HasValue,
            TerminatorFound = structure.TerminatorFound,
        };

        if (structure.Failure is { } failure)
        {
            return NotProduced(
                request,
                declared,
                detected,
                mismatch: false,
                failure,
                EvidenceAvailability.Unavailable,
                coverage,
                CapFindings(findings, limits.MaxFindings));
        }

        if (structure.Width is null || structure.Height is null)
        {
            // The prefix ended before the header that carries dimensions (a JPEG SOF behind large
            // EXIF, for example). This is a named absence, not an exception and not a truncation
            // of the part itself when the extent is a prefix.
            return NotProduced(
                request,
                declared,
                detected,
                mismatch: false,
                ImageNotProducedReason.TruncatedHeader,
                EvidenceAvailability.Unavailable,
                coverage,
                CapFindings(findings, limits.MaxFindings));
        }

        var width = structure.Width.Value;
        var height = structure.Height.Value;
        var pixels = width > long.MaxValue / height ? long.MaxValue : width * height;

        if (structure.DimensionLimitExceeded || structure.PixelLimitExceeded)
        {
            return new ImageInspectionResult
            {
                DeclaredContentType = request.DeclaredContentType,
                DeclaredFormat = declared,
                DetectedFormat = detected,
                FormatMismatch = false,
                Disposition = ImageDisposition.Refused,
                NotProducedReason = null,
                Availability = EvidenceAvailability.Available,
                Width = width,
                Height = height,
                PixelCount = pixels,
                ImageEndOffset = structure.EndOffset,
                TrailingByteCount = null,
                TrailingBytesObserved = null,
                TrailingScanComplete = false,
                TransparencyDeclared = structure.TransparencyDeclared,
                Findings = CapFindings(findings, limits.MaxFindings),
                Coverage = coverage,
            };
        }

        long? trailing = null;
        long? trailingObserved = null;
        var trailingScanComplete = false;

        if (structure.EndOffset is { } end)
        {
            if (total is { } knownTotal)
            {
                trailing = knownTotal - end;
                trailingScanComplete = knownTotal <= inspectedBytes;

                if (trailing > 0)
                {
                    findings.Add(new ImageFinding
                    {
                        Code = ImageFindingCode.TrailingData,
                        Offset = end,
                        Detail = $"bytes={trailing}",
                    });
                }
            }
            else
            {
                // The total is unknown, so the bytes visible after the terminator are a lower
                // bound and never a count. The prefix length must not be reported as the total.
                trailingObserved = inspectedBytes - end;
                if (trailingObserved > 0)
                {
                    findings.Add(new ImageFinding
                    {
                        Code = ImageFindingCode.TrailingData,
                        Offset = end,
                        Detail = $"observedBytes={trailingObserved};total=unknown",
                    });
                }
            }

            if (end < inspectedBytes)
            {
                ScanTrailing(bytes[(int)end..], end, findings, limits.MaxFindings);
            }
        }

        return new ImageInspectionResult
        {
            DeclaredContentType = request.DeclaredContentType,
            DeclaredFormat = declared,
            DetectedFormat = detected,
            FormatMismatch = false,
            Disposition = ImageDisposition.Inspected,
            NotProducedReason = null,
            Availability = truncated ? EvidenceAvailability.ReducedCoverage : EvidenceAvailability.Available,
            Width = width,
            Height = height,
            PixelCount = pixels,
            ImageEndOffset = structure.EndOffset,
            TrailingByteCount = trailing,
            TrailingBytesObserved = trailingObserved,
            TrailingScanComplete = trailingScanComplete,
            TransparencyDeclared = structure.TransparencyDeclared,
            Findings = CapFindings(findings, limits.MaxFindings),
            Coverage = coverage,
        };
    }

    private static void Validate(ImageInspectionLimits limits)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxInspectedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxDimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxHeaderSegments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxHeaderBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxFindings);
    }

    private static bool IsSupportedForStructure(ImageFormat format) =>
        format is ImageFormat.Png or ImageFormat.Jpeg or ImageFormat.Gif or ImageFormat.Bmp or ImageFormat.WebP;

    private static int MinimumSignatureBytes(ImageFormat format) => format switch
    {
        ImageFormat.Png => 8,
        ImageFormat.Jpeg => 3,
        ImageFormat.Gif => 6,
        ImageFormat.Bmp => 18,
        ImageFormat.WebP => 12,
        ImageFormat.Tiff => 4,
        ImageFormat.Heic or ImageFormat.Avif => 12,
        ImageFormat.Svg => 4,
        ImageFormat.Ico => 4,
        _ => 1,
    };

    private static void AddMismatch(List<ImageFinding> findings, ImageFormat declared, ImageFormat detected)
    {
        findings.Add(new ImageFinding
        {
            Code = ImageFindingCode.TypeMagicMismatch,
            Detail = $"declared={declared.ToWireName()};detected={detected.ToWireName()}",
        });
    }

    /// <summary>
    /// Scans only the region after the terminator. Signatures inside compressed data are not
    /// evidence of a polyglot; they are expected noise, and reporting them would be a false
    /// positive machine.
    /// </summary>
    private static void ScanTrailing(
        ReadOnlySpan<byte> region,
        long baseOffset,
        List<ImageFinding> findings,
        int maxFindings)
    {
        foreach (var signature in TrailingSignatures)
        {
            var search = region;
            var consumed = 0;
            while (findings.Count < maxFindings && search.Length >= signature.Magic.Length)
            {
                var index = search.IndexOf(signature.Magic);
                if (index < 0)
                {
                    break;
                }

                findings.Add(new ImageFinding
                {
                    Code = signature.Code,
                    Offset = baseOffset + consumed + index,
                    Detail = signature.Detail,
                });

                var advance = index + signature.Magic.Length;
                consumed += advance;
                search = search[advance..];
            }
        }

        // A bare "MZ" is not an executable. Require a plausible e_lfanew that lands on a PE
        // signature, so random trailing bytes cannot manufacture this finding.
        var peSearch = region;
        var peConsumed = 0;
        while (findings.Count < maxFindings && peSearch.Length >= 0x40)
        {
            var index = peSearch.IndexOf("MZ"u8);
            if (index < 0)
            {
                break;
            }

            var candidate = peSearch[index..];
            if (candidate.Length >= 0x40)
            {
                var lfanew = BinaryPrimitives.ReadInt32LittleEndian(candidate[0x3C..]);
                if (lfanew >= 0 &&
                    lfanew <= candidate.Length - 4 &&
                    candidate.Slice(lfanew, 4).SequenceEqual("PE\0\0"u8))
                {
                    findings.Add(new ImageFinding
                    {
                        Code = ImageFindingCode.EmbeddedExecutable,
                        Offset = baseOffset + peConsumed + index,
                        Detail = "pe",
                    });
                }
            }

            var advance = index + 2;
            peConsumed += advance;
            peSearch = peSearch[advance..];
        }
    }

    private static IReadOnlyList<ImageFinding> CapFindings(List<ImageFinding> findings, int maxFindings)
    {
        if (findings.Count == 0)
        {
            return [];
        }

        return [.. findings.OrderBy(f => f.Offset ?? long.MaxValue).Take(maxFindings)];
    }

    private static ImageInspectionResult NotProduced(
        ImageInspectRequest request,
        ImageFormat declared,
        ImageFormat detected,
        bool mismatch,
        ImageNotProducedReason reason,
        EvidenceAvailability availability,
        ImageInspectionCoverage coverage,
        IReadOnlyList<ImageFinding> findings) => new()
        {
            DeclaredContentType = request.DeclaredContentType,
            DeclaredFormat = declared,
            DetectedFormat = detected,
            FormatMismatch = mismatch,
            Disposition = ImageDisposition.NotProduced,
            NotProducedReason = reason,
            Availability = availability,
            Width = null,
            Height = null,
            PixelCount = null,
            ImageEndOffset = null,
            TrailingByteCount = null,
            TrailingBytesObserved = null,
            TrailingScanComplete = false,
            TransparencyDeclared = null,
            Findings = findings,
            Coverage = coverage,
        };
}
