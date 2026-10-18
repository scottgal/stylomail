namespace StyloMail.Image;

/// <summary>
/// A container format, as declared by a content type or as identified from magic bytes.
/// </summary>
/// <remarks>
/// <see cref="NonImage"/> is a declaration outcome rather than a format: a part explicitly
/// declared as something other than an image. It exists so a mismatch between "text/plain" and a
/// PNG signature is a named disagreement rather than an absence of information.
/// </remarks>
public enum ImageFormat
{
    /// <summary>No format was declared and no magic signature matched.</summary>
    Unknown = 0,

    Png = 1,

    Jpeg = 2,

    Gif = 3,

    Bmp = 4,

    WebP = 5,

    Tiff = 6,

    Heic = 7,

    Avif = 8,

    Svg = 9,

    Ico = 10,

    /// <summary>An <c>image/*</c> subtype this build does not name. Deliberately not a mismatch by itself.</summary>
    OtherImage = 11,

    /// <summary>A specific non-image content type was declared. Only meaningful as a declaration.</summary>
    NonImage = 12,
}

/// <summary>
/// Content-type parsing and magic-byte identification. Neither reads past the supplied prefix.
/// </summary>
internal static class ImageFormats
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Maps a declared content type to the format it claims.
    /// </summary>
    /// <remarks>
    /// A missing, empty or generic type is <see cref="ImageFormat.Unknown"/> rather than a guess.
    /// "application/octet-stream" is the common case for mail attachments and must not manufacture
    /// a mismatch. A vendor <c>image/*</c> subtype is <see cref="ImageFormat.OtherImage"/>, which is
    /// also not a mismatch: the sender did not claim a specific container.
    /// </remarks>
    public static ImageFormat Declared(string? contentType)
    {
        var type = Normalise(contentType);
        if (type.Length == 0 || IsGeneric(type))
        {
            return ImageFormat.Unknown;
        }

        return type switch
        {
            "image/png" => ImageFormat.Png,
            "image/jpeg" or "image/jpg" or "image/pjpeg" => ImageFormat.Jpeg,
            "image/gif" => ImageFormat.Gif,
            "image/bmp" or "image/x-bmp" or "image/x-ms-bmp" => ImageFormat.Bmp,
            "image/webp" => ImageFormat.WebP,
            "image/tiff" or "image/x-tiff" => ImageFormat.Tiff,
            "image/heic" or "image/heif" or "image/heic-sequence" or "image/heif-sequence" => ImageFormat.Heic,
            "image/avif" or "image/avif-sequence" => ImageFormat.Avif,
            "image/svg+xml" => ImageFormat.Svg,
            "image/x-icon" or "image/vnd.microsoft.icon" => ImageFormat.Ico,
            _ when type.StartsWith("image/", StringComparison.Ordinal) => ImageFormat.OtherImage,
            _ => ImageFormat.NonImage,
        };
    }

    private static bool IsGeneric(string type) =>
        type.Length == 0 ||
        type is "application/octet-stream" or "binary/octet-stream" or "application/binary" or "application/unknown";

    private static string Normalise(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var separator = contentType.IndexOf(';');
        var type = separator >= 0 ? contentType[..separator] : contentType;
        return type.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Identifies a format from its signature, using only the bytes supplied.
    /// </summary>
    /// <remarks>
    /// SVG is recognised from its text prologue, never parsed. An XML document is reported as SVG
    /// only when an <c>&lt;svg</c> element appears in the first kilobyte, so an ordinary XML file is
    /// not silently promoted to an image claim.
    /// </remarks>
    public static ImageFormat Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature))
        {
            return ImageFormat.Png;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        if (bytes.Length >= 6 &&
            (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            return ImageFormat.Gif;
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            return ImageFormat.Bmp;
        }

        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ImageFormat.WebP;
        }

        if (bytes.Length >= 4 &&
            ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00) ||
             (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A)))
        {
            return ImageFormat.Tiff;
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
        {
            return ImageFormat.Ico;
        }

        var iso = DetectIsoBmff(bytes);
        if (iso != ImageFormat.Unknown)
        {
            return iso;
        }

        return LooksLikeSvg(bytes) ? ImageFormat.Svg : ImageFormat.Unknown;
    }

    private static ImageFormat DetectIsoBmff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return ImageFormat.Unknown;
        }

        var brand = bytes.Slice(8, 4);
        var result = BrandFormat(brand);

        // The major brand is the claim; compatible brands are a fallback for files whose major
        // brand is a generic one (for example "mif1"). Bounded to the ftyp box and 128 bytes.
        var boxSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var limit = (int)Math.Min(Math.Min((long)bytes.Length, boxSize), 128);
        for (var offset = 16; result == ImageFormat.Unknown && offset + 4 <= limit; offset += 4)
        {
            result = BrandFormat(bytes.Slice(offset, 4));
        }

        return result;
    }

    private static ImageFormat BrandFormat(ReadOnlySpan<byte> brand)
    {
        if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) ||
            brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8) ||
            brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8) ||
            brand.SequenceEqual("hevm"u8) || brand.SequenceEqual("hevs"u8) ||
            brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8))
        {
            return ImageFormat.Heic;
        }

        if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
        {
            return ImageFormat.Avif;
        }

        return ImageFormat.Unknown;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        var start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            start = 3;
        }

        while (start < bytes.Length && IsAsciiWhitespace(bytes[start]))
        {
            start++;
        }

        if (start >= bytes.Length)
        {
            return false;
        }

        var head = bytes[start..Math.Min(bytes.Length, start + 1024)];
        if (StartsWithIgnoreCase(head, "<svg"u8))
        {
            return true;
        }

        return StartsWithIgnoreCase(head, "<?xml"u8) && ContainsIgnoreCase(head, "<svg"u8);
    }

    private static bool IsAsciiWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (ToLower(value[i]) != ToLower(prefix[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= value.Length; i++)
        {
            if (StartsWithIgnoreCase(value[i..], needle))
            {
                return true;
            }
        }

        return false;
    }

    private static byte ToLower(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
}
