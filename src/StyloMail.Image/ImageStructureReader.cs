using System.Buffers.Binary;

namespace StyloMail.Image;

/// <summary>Header-only structural facts, as read without decoding any pixel data.</summary>
internal sealed class ImageStructure
{
    public long? Width { get; set; }

    public long? Height { get; set; }

    public long? EndOffset { get; set; }

    public bool TerminatorFound { get; set; }

    public ImageNotProducedReason? Failure { get; set; }

    public bool ZeroDimensions { get; set; }

    public bool DimensionLimitExceeded { get; set; }

    public bool PixelLimitExceeded { get; set; }
}

/// <summary>
/// Reads dimensions and terminators from container headers. No pixel data is decompressed, and
/// every walk is bounded by count, byte budget or the supplied prefix.
/// </summary>
internal static class ImageStructureReader
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ImageStructure Read(
        ImageFormat format,
        ReadOnlySpan<byte> bytes,
        ImageInspectionLimits limits,
        bool inputTruncated) => format switch
        {
            ImageFormat.Png => ReadPng(bytes, limits, inputTruncated),
            ImageFormat.Jpeg => ReadJpeg(bytes, limits, inputTruncated),
            ImageFormat.Gif => ReadGif(bytes, limits, inputTruncated),
            ImageFormat.Bmp => ReadBmp(bytes, limits, inputTruncated),
            ImageFormat.WebP => ReadWebP(bytes, limits, inputTruncated),
            _ => new ImageStructure { Failure = ImageNotProducedReason.UnsupportedFormat },
        };

    private static ImageStructure ReadPng(ReadOnlySpan<byte> bytes, ImageInspectionLimits limits, bool inputTruncated)
    {
        var structure = new ImageStructure();

        // Signature (8) + IHDR length/type/13 data/CRC (25).
        if (bytes.Length < 33)
        {
            return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
        }

        if (!bytes[..8].SequenceEqual(PngSignature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            structure.Failure = ImageNotProducedReason.InvalidStructure;
            return structure;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        if (!TryDimensions(structure, width, height, limits) ||
            structure.DimensionLimitExceeded ||
            structure.PixelLimitExceeded)
        {
            return structure;
        }

        long offset = 8;
        var chunks = 0;
        while (true)
        {
            if (++chunks > limits.MaxHeaderSegments)
            {
                structure.Failure = ImageNotProducedReason.HeaderBudgetExceeded;
                return structure;
            }

            if (offset + 8 > bytes.Length)
            {
                return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[(int)offset..]);
            var type = bytes.Slice((int)offset + 4, 4);
            var chunkEnd = offset + 12L + length;
            if (chunkEnd > bytes.Length)
            {
                return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
            }

            if (type.SequenceEqual("IEND"u8))
            {
                structure.EndOffset = chunkEnd;
                structure.TerminatorFound = true;
                return structure;
            }

            offset = chunkEnd;
        }
    }

    private static ImageStructure ReadJpeg(ReadOnlySpan<byte> bytes, ImageInspectionLimits limits, bool inputTruncated)
    {
        var structure = new ImageStructure();
        var offset = 2;
        var markers = 0;
        long segmentBytes = 0;
        var sawSof = false;
        var inEntropy = false;

        while (true)
        {
            if (++markers > limits.MaxHeaderSegments)
            {
                structure.Failure = ImageNotProducedReason.HeaderBudgetExceeded;
                return structure;
            }

            var read = TryReadMarker(bytes, ref offset, out var marker);
            if (read == MarkerRead.Truncated)
            {
                return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
            }

            if (read == MarkerRead.Invalid)
            {
                structure.Failure = ImageNotProducedReason.InvalidStructure;
                return structure;
            }

            if (marker == 0xD9)
            {
                structure.EndOffset = offset;
                structure.TerminatorFound = true;
                if (!sawSof)
                {
                    structure.Failure = ImageNotProducedReason.InvalidStructure;
                }

                return structure;
            }

            if (marker is 0x01 or 0xD8 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (marker == 0x00)
            {
                structure.Failure = ImageNotProducedReason.InvalidStructure;
                return structure;
            }

            if (offset + 2 > bytes.Length)
            {
                return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (segmentLength < 2)
            {
                structure.Failure = ImageNotProducedReason.InvalidStructure;
                return structure;
            }

            var segmentEnd = offset + (long)segmentLength;
            if (segmentEnd > bytes.Length)
            {
                return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
            }

            segmentBytes += segmentLength;
            if (segmentBytes > limits.MaxHeaderBytes)
            {
                structure.Failure = ImageNotProducedReason.HeaderBudgetExceeded;
                return structure;
            }

            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                // Precision (1), height (2), width (2), component count (1).
                if (segmentLength < 7)
                {
                    structure.Failure = ImageNotProducedReason.InvalidStructure;
                    return structure;
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                if (!TryDimensions(structure, width, height, limits) ||
                    structure.DimensionLimitExceeded ||
                    structure.PixelLimitExceeded)
                {
                    return structure;
                }

                sawSof = true;
            }

            if (marker == 0xDA)
            {
                if (!sawSof)
                {
                    structure.Failure = ImageNotProducedReason.InvalidStructure;
                    return structure;
                }

                offset = (int)segmentEnd;
                inEntropy = true;
                if (!SkipEntropyCodedData(bytes, ref offset))
                {
                    return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
                }

                continue;
            }

            offset = (int)segmentEnd;

            if (inEntropy && marker == 0xDC)
            {
                // DNL (number of lines) is a length-bearing marker that appears inside a scan;
                // entropy coding resumes after it. Any other marker ends the scan.
                if (!SkipEntropyCodedData(bytes, ref offset))
                {
                    return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
                }

                continue;
            }

            inEntropy = false;
        }
    }

    private static ImageStructure ReadGif(ReadOnlySpan<byte> bytes, ImageInspectionLimits limits, bool inputTruncated)
    {
        var structure = new ImageStructure();
        if (bytes.Length < 13)
        {
            return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        if (!TryDimensions(structure, width, height, limits) ||
            structure.DimensionLimitExceeded ||
            structure.PixelLimitExceeded)
        {
            return structure;
        }

        long offset = 13;
        if ((bytes[10] & 0x80) != 0)
        {
            offset += 3L * (1 << ((bytes[10] & 0x07) + 1));
        }

        var blocks = 0;
        while (true)
        {
            if (++blocks > limits.MaxHeaderSegments)
            {
                structure.Failure = ImageNotProducedReason.HeaderBudgetExceeded;
                return structure;
            }

            if (offset >= bytes.Length)
            {
                return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
            }

            switch (bytes[(int)offset])
            {
                case 0x3B:
                    structure.EndOffset = offset + 1;
                    structure.TerminatorFound = true;
                    return structure;

                case 0x21:
                    // Extension introducer plus label, then data sub-blocks.
                    offset += 2;
                    if (offset > bytes.Length)
                    {
                        return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
                    }

                    if (!TrySkipSubBlocks(bytes, ref offset))
                    {
                        return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
                    }

                    break;

                case 0x2C:
                    // Image descriptor (9 bytes after the introducer) plus optional local table,
                    // LZW minimum code size and data sub-blocks.
                    if (offset + 10 > bytes.Length)
                    {
                        return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
                    }

                    var packed = bytes[(int)offset + 9];
                    offset += 10;
                    if ((packed & 0x80) != 0)
                    {
                        offset += 3L * (1 << ((packed & 0x07) + 1));
                    }

                    offset++;
                    if (!TrySkipSubBlocks(bytes, ref offset))
                    {
                        return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
                    }

                    break;

                default:
                    structure.Failure = ImageNotProducedReason.InvalidStructure;
                    return structure;
            }
        }
    }

    private static ImageStructure ReadBmp(ReadOnlySpan<byte> bytes, ImageInspectionLimits limits, bool inputTruncated)
    {
        var structure = new ImageStructure();
        if (bytes.Length < 26)
        {
            return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
        }

        var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);

        long width;
        long height;
        if (dibSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]);
        }
        else if (dibSize >= 40)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]);
            if (height < 0)
            {
                height = -height;
            }
        }
        else
        {
            structure.Failure = ImageNotProducedReason.InvalidStructure;
            return structure;
        }

        if (!TryDimensions(structure, width, height, limits) ||
            structure.DimensionLimitExceeded ||
            structure.PixelLimitExceeded)
        {
            return structure;
        }

        if (fileSize > 0)
        {
            if (fileSize < 26 || pixelOffset > fileSize)
            {
                structure.Failure = ImageNotProducedReason.InvalidStructure;
                return structure;
            }

            if (fileSize > bytes.Length)
            {
                return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
            }

            structure.EndOffset = fileSize;
            structure.TerminatorFound = true;
        }

        return structure;
    }

    private static ImageStructure ReadWebP(ReadOnlySpan<byte> bytes, ImageInspectionLimits limits, bool inputTruncated)
    {
        var structure = new ImageStructure();
        if (bytes.Length < 12)
        {
            return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
        }

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (riffSize < 4)
        {
            structure.Failure = ImageNotProducedReason.InvalidStructure;
            return structure;
        }

        var end = 8L + riffSize;
        if (end > bytes.Length)
        {
            return EndOfInput(structure, inputTruncated, ImageNotProducedReason.TruncatedHeader);
        }

        long offset = 12;
        var chunks = 0;
        var found = false;

        while (offset + 8 <= end)
        {
            if (++chunks > limits.MaxHeaderSegments)
            {
                structure.Failure = ImageNotProducedReason.HeaderBudgetExceeded;
                return structure;
            }

            var fourCc = bytes.Slice((int)offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[((int)offset + 4)..]);
            var dataStart = offset + 8;
            var dataEnd = dataStart + size;
            if (dataEnd > end)
            {
                structure.Failure = ImageNotProducedReason.InvalidStructure;
                return structure;
            }

            if (!found && fourCc.SequenceEqual("VP8X"u8) && size >= 10)
            {
                var width = 1 + ReadUInt24LittleEndian(bytes, dataStart + 4);
                var height = 1 + ReadUInt24LittleEndian(bytes, dataStart + 7);
                found = TryDimensions(structure, width, height, limits);
            }
            else if (!found && fourCc.SequenceEqual("VP8L"u8) && size >= 5 && bytes[(int)dataStart] == 0x2F)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[((int)dataStart + 1)..]);
                var width = (bits & 0x3FFF) + 1;
                var height = ((bits >> 14) & 0x3FFF) + 1;
                found = TryDimensions(structure, width, height, limits);
            }
            else if (!found && fourCc.SequenceEqual("VP8 "u8) && size >= 10 &&
                     bytes[(int)dataStart + 3] == 0x9D &&
                     bytes[(int)dataStart + 4] == 0x01 &&
                     bytes[(int)dataStart + 5] == 0x2A)
            {
                var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[((int)dataStart + 6)..]) & 0x3FFF;
                var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[((int)dataStart + 8)..]) & 0x3FFF;
                found = TryDimensions(structure, width, height, limits);
            }

            if (found)
            {
                structure.EndOffset = end;
                structure.TerminatorFound = true;
                if (structure.DimensionLimitExceeded || structure.PixelLimitExceeded)
                {
                    return structure;
                }

                return structure;
            }

            if (structure.Failure is not null)
            {
                return structure;
            }

            offset = dataEnd + (size & 1);
        }

        structure.Failure = ImageNotProducedReason.InvalidStructure;
        return structure;
    }

    private enum MarkerRead
    {
        Ok,
        Truncated,
        Invalid,
    }

    private static MarkerRead TryReadMarker(ReadOnlySpan<byte> bytes, ref int offset, out byte marker)
    {
        marker = 0;
        if (offset >= bytes.Length)
        {
            return MarkerRead.Truncated;
        }

        if (bytes[offset] != 0xFF)
        {
            return MarkerRead.Invalid;
        }

        while (offset < bytes.Length && bytes[offset] == 0xFF)
        {
            offset++;
        }

        if (offset >= bytes.Length)
        {
            return MarkerRead.Truncated;
        }

        marker = bytes[offset++];
        return MarkerRead.Ok;
    }

    /// <summary>
    /// Advances past entropy-coded data to the next real marker. Stuffed zero bytes and restart
    /// markers are data, not markers; fill bytes collapse into the marker that follows them.
    /// </summary>
    private static bool SkipEntropyCodedData(ReadOnlySpan<byte> bytes, ref int offset)
    {
        while (offset + 1 < bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var next = bytes[offset + 1];
            if (next is 0x00 or 0x01 || next is >= 0xD0 and <= 0xD7)
            {
                offset += 2;
                continue;
            }

            if (next == 0xFF)
            {
                offset++;
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool TrySkipSubBlocks(ReadOnlySpan<byte> bytes, ref long offset)
    {
        while (true)
        {
            if (offset >= bytes.Length)
            {
                return false;
            }

            var size = bytes[(int)offset++];
            if (size == 0)
            {
                return true;
            }

            if (offset + size > bytes.Length)
            {
                return false;
            }

            offset += size;
        }
    }

    private static bool TryDimensions(
        ImageStructure structure,
        long width,
        long height,
        ImageInspectionLimits limits)
    {
        if (width <= 0 || height <= 0)
        {
            structure.Failure = ImageNotProducedReason.InvalidDimensions;
            structure.ZeroDimensions = width == 0 || height == 0;
            return false;
        }

        structure.Width = width;
        structure.Height = height;

        if (width > limits.MaxDimension || height > limits.MaxDimension)
        {
            structure.DimensionLimitExceeded = true;
        }

        if (width > long.MaxValue / height || width * height > limits.MaxPixels)
        {
            structure.PixelLimitExceeded = true;
        }

        return true;
    }

    private static long ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes, long offset)
    {
        var index = (int)offset;
        return bytes[index] | (bytes[index + 1] << 8) | (bytes[index + 2] << 16);
    }

    private static ImageStructure EndOfInput(
        ImageStructure structure,
        bool inputTruncated,
        ImageNotProducedReason reason)
    {
        if (!inputTruncated)
        {
            structure.Failure = reason;
        }

        return structure;
    }
}
