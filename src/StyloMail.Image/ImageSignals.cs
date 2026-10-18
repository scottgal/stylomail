namespace StyloMail.Image;

/// <summary>
/// Stable identifiers for the deterministic signals this inspector produces.
/// </summary>
/// <remarks>
/// All of these are computed locally from the bytes with no provider, so their origin is
/// <see cref="StyloMail.Core.EvidenceOrigin.Deterministic"/>. A row whose availability is
/// <see cref="StyloMail.Core.EvidenceAvailability.Unavailable"/> carries a named reason and a null
/// value; it is never a zero.
/// </remarks>
public static class ImageSignals
{
    /// <summary>Version of this inspector's rules and of the shape of its signals.</summary>
    public const string SourceVersion = "stylomail-image/2";

    /// <summary>Whether an inspection was produced at all, over what coverage, and why not when absent.</summary>
    public const string Inspection = "deterministic.image_inspection";

    /// <summary>Header-only format and dimensions. Value: pixel count.</summary>
    public const string Structure = "deterministic.image_structure";

    /// <summary>Declared type versus magic bytes. Value: 1.0 mismatched, 0.0 consistent.</summary>
    public const string FormatMismatch = "deterministic.image_format_mismatch";

    /// <summary>Bytes after the container terminator. Value: trailing byte count.</summary>
    public const string TrailingData = "deterministic.image_trailing_data";

    /// <summary>Whether the dimensions or pixel count crossed a hard safety bound. Value: 1.0 refused, 0.0 within bounds.</summary>
    public const string LimitsExceeded = "deterministic.image_limits_exceeded";

    /// <summary>The format itself can carry script and external references. Value: 1.0 present, 0.0 absent.</summary>
    public const string ScriptableFormat = "deterministic.image_scriptable_format";

    /// <summary>
    /// One row per message with image parts: admission and coverage counts, including images not
    /// inspected because the per-message budget was exhausted. Value: images over budget.
    /// </summary>
    public const string InspectionCoverage = "deterministic.image_inspection_coverage";
}
