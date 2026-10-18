namespace StyloMail.Image;

/// <summary>
/// Hard bounds applied before any structural interpretation. Every value is a refusal point, not
/// a target: crossing one produces a named outcome rather than an attempt to carry on.
/// </summary>
public sealed record ImageInspectionLimits
{
    /// <summary>
    /// Largest prefix the inspector may read from one part. The caller may supply more; only this
    /// many bytes are ever examined, and coverage records the difference.
    /// </summary>
    public int MaxInspectedBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Largest width or height accepted for a header-only measurement.</summary>
    public int MaxDimension { get; init; } = 16_384;

    /// <summary>
    /// Largest pixel count accepted. This is the decompression-bomb bound: a header claiming more
    /// is refused before any decoder could size an allocation from it.
    /// </summary>
    public long MaxPixels { get; init; } = 40_000_000;

    /// <summary>Largest number of chunks, markers or blocks walked while looking for dimensions or a terminator.</summary>
    public int MaxHeaderSegments { get; init; } = 4096;

    /// <summary>
    /// Largest cumulative marker-segment length (not entropy data) walked in one JPEG. Payloads are
    /// skipped, never allocated; the cap bounds the walk, while staying above ordinary EXIF/ICC
    /// metadata so a photograph with a colour profile is not refused by its own header.
    /// </summary>
    public int MaxHeaderBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Largest number of findings retained per image, so one part cannot inflate the ledger.</summary>
    public int MaxFindings { get; init; } = 16;
}
