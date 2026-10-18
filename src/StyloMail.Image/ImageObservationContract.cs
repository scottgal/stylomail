namespace StyloMail.Image;

/// <summary>
/// Versioned numerical contract for image observation at the call site. These are structural caps,
/// not measured CPU budgets; the library itself is pure and allocates none of them.
/// </summary>
public static class ImageObservationContract
{
    public const string Version = "image-observation/1";

    /// <summary>Decoded bytes handed to one admitted image. 256 KiB.</summary>
    public const int PerImageInspectedBytes = 262_144;

    /// <summary>Aggregate decoded bytes examined for admitted images in one message. 2 x 256 KiB.</summary>
    public const int PerMessageInspectedBytes = 524_288;

    /// <summary>Maximum admitted images per message.</summary>
    public const int MaxAdmittedImages = 2;

    /// <summary>Maximum header probes per message, used to establish dimensions before admission.</summary>
    public const int MaxProbes = 4;

    /// <summary>Bytes in one header probe.</summary>
    public const int ProbeBytes = 4_096;

    /// <summary>Minimum width and height for an image with known dimensions to be admitted.</summary>
    public const int MinDimension = 32;

    /// <summary>Minimum pixel count for an image with known dimensions to be admitted.</summary>
    public const long MinPixels = 1_024;

    /// <summary>Minimum width and height for a transparent image to be admitted (tracking-pixel guard).</summary>
    public const int TransparentMinDimension = 64;

    /// <summary>Minimum pixel count for a transparent image to be admitted.</summary>
    public const long TransparentMinPixels = 4_096;

    /// <summary>Byte floor applied only when dimensions could not be established. 2 KiB.</summary>
    public const long UnknownDimensionMinBytes = 2_048;

    /// <summary>Maximum evidence rows for one admitted image.</summary>
    public const int MaxEvidenceRowsPerImage = 6;

    /// <summary>Maximum evidence rows for images in one message, including the aggregate coverage row.</summary>
    public const int MaxEvidenceRowsPerMessage = 13;

    /// <summary>Maximum attributes on the aggregate coverage row.</summary>
    public const int MaxCoverageAttributes = 9;
}
