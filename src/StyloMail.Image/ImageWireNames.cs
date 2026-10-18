namespace StyloMail.Image;

/// <summary>
/// Stable wire spellings for the vocabularies in this library. These strings are part of the
/// ledger and console contract; renaming one is a version bump of
/// <see cref="ImageSignals.SourceVersion"/>, not an edit in place.
/// </summary>
public static class ImageWireNames
{
    public static string ToWireName(this ImageFormat format) => format switch
    {
        ImageFormat.Unknown => "unknown",
        ImageFormat.Png => "png",
        ImageFormat.Jpeg => "jpeg",
        ImageFormat.Gif => "gif",
        ImageFormat.Bmp => "bmp",
        ImageFormat.WebP => "webp",
        ImageFormat.Tiff => "tiff",
        ImageFormat.Heic => "heic",
        ImageFormat.Avif => "avif",
        ImageFormat.Svg => "svg",
        ImageFormat.Ico => "ico",
        ImageFormat.OtherImage => "other_image",
        ImageFormat.NonImage => "non_image",
        _ => "unknown",
    };

    public static string ToWireName(this ImageDisposition disposition) => disposition switch
    {
        ImageDisposition.Inspected => "inspected",
        ImageDisposition.Refused => "refused",
        ImageDisposition.NotProduced => "not_produced",
        _ => "not_produced",
    };

    public static string ToWireName(this ImageNotProducedReason reason) => reason switch
    {
        ImageNotProducedReason.EmptyContent => "empty_content",
        ImageNotProducedReason.NotAnImage => "not_an_image",
        ImageNotProducedReason.UnsupportedFormat => "unsupported_format",
        ImageNotProducedReason.FormatMismatch => "format_mismatch",
        ImageNotProducedReason.TruncatedHeader => "truncated_header",
        ImageNotProducedReason.HeaderBudgetExceeded => "header_budget_exceeded",
        ImageNotProducedReason.InvalidDimensions => "invalid_dimensions",
        ImageNotProducedReason.InvalidStructure => "invalid_structure",
        ImageNotProducedReason.InsufficientDecodedLength => "insufficient_decoded_length",
        ImageNotProducedReason.InspectionBudgetExhausted => "inspection_budget_exhausted",
        _ => "invalid_structure",
    };

    public static string ToWireName(this ImageFindingCode code) => code switch
    {
        ImageFindingCode.TypeMagicMismatch => "type_magic_mismatch",
        ImageFindingCode.TrailingData => "trailing_data",
        ImageFindingCode.EmbeddedArchive => "embedded_archive",
        ImageFindingCode.EmbeddedExecutable => "embedded_executable",
        ImageFindingCode.ScriptableFormat => "scriptable_format",
        ImageFindingCode.ZeroDimensions => "zero_dimensions",
        ImageFindingCode.DimensionLimitExceeded => "dimension_limit_exceeded",
        ImageFindingCode.PixelLimitExceeded => "pixel_limit_exceeded",
        _ => "unknown",
    };
}
