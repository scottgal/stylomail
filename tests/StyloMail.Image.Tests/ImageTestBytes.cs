using System.Buffers.Binary;
using System.Text;

namespace StyloMail.Image.Tests;

/// <summary>
/// Crafted bytes for the inspector's proofs. Only the structures the inspector reads are built;
/// no pixel data is needed because no pixel data is ever decoded.
/// </summary>
internal static class ImageTestBytes
{
    public static byte[] LoadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    public static byte[] Png(uint width, uint height) => PngWithExtraChunk(width, height, null, null);

    public static byte[] PngWithExtraChunk(uint width, uint height, string? chunkType, byte[]? chunkData)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WritePngChunk(stream, "IHDR"u8, ihdr);
        WritePngChunk(stream, "IDAT"u8, [0x78, 0x9C, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01]);

        if (chunkType is not null)
        {
            WritePngChunk(stream, Encoding.ASCII.GetBytes(chunkType), chunkData ?? []);
        }

        WritePngChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    public static byte[] Gif(ushort width, ushort height)
    {
        using var stream = new MemoryStream();
        stream.Write("GIF89a"u8);
        WriteUInt16LittleEndian(stream, width);
        WriteUInt16LittleEndian(stream, height);
        stream.WriteByte(0x00); // no global colour table
        stream.WriteByte(0x00); // background index
        stream.WriteByte(0x00); // pixel aspect ratio
        stream.WriteByte(0x3B); // trailer
        return stream.ToArray();
    }

    /// <summary>A GIF whose Graphic Control Extension declares a transparent colour.</summary>
    public static byte[] GifTransparent(ushort width, ushort height)
    {
        using var stream = new MemoryStream();
        stream.Write("GIF89a"u8);
        WriteUInt16LittleEndian(stream, width);
        WriteUInt16LittleEndian(stream, height);
        stream.WriteByte(0x00);
        stream.WriteByte(0x00);
        stream.WriteByte(0x00);
        stream.Write([0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00]); // GCE, transparency flag set
        stream.WriteByte(0x3B);
        return stream.ToArray();
    }

    public static byte[] Bmp(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write("BM"u8);
        WriteUInt32LittleEndian(stream, 54); // file size
        WriteUInt16LittleEndian(stream, 0);
        WriteUInt16LittleEndian(stream, 0);
        WriteUInt32LittleEndian(stream, 54); // pixel data offset
        WriteUInt32LittleEndian(stream, 40); // BITMAPINFOHEADER
        WriteInt32LittleEndian(stream, width);
        WriteInt32LittleEndian(stream, height);
        WriteUInt16LittleEndian(stream, 1);
        WriteUInt16LittleEndian(stream, 24);
        WriteUInt32LittleEndian(stream, 0);
        WriteUInt32LittleEndian(stream, 0);
        WriteInt32LittleEndian(stream, 0);
        WriteInt32LittleEndian(stream, 0);
        WriteUInt32LittleEndian(stream, 0);
        WriteUInt32LittleEndian(stream, 0);
        return stream.ToArray();
    }

    public static byte[] WebPVp8X(uint width, uint height)
    {
        using var stream = new MemoryStream();
        stream.Write("RIFF"u8);
        WriteUInt32LittleEndian(stream, 22); // 12 header + 8 chunk header + 10 data
        stream.Write("WEBP"u8);
        stream.Write("VP8X"u8);
        WriteUInt32LittleEndian(stream, 10);
        stream.WriteByte(0x00);
        stream.Write([0x00, 0x00, 0x00]);
        WriteUInt24LittleEndian(stream, width - 1);
        WriteUInt24LittleEndian(stream, height - 1);
        return stream.ToArray();
    }

    /// <summary>A structurally shaped JPEG whose scan contains a DNL marker before the final EOI.</summary>
    public static byte[] JpegWithDnl()
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]); // SOI

        // SOF0: precision 8, height 2, width 3, one component.
        stream.Write([0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x02, 0x00, 0x03, 0x01, 0x01, 0x11, 0x00]);
        // SOS, one component, full spectral selection.
        stream.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
        stream.Write([0x12, 0x34, 0x56]);

        // DNL: a length-bearing marker inside entropy-coded data; coding resumes after it.
        stream.Write([0xFF, 0xDC, 0x00, 0x04, 0x00, 0x02]);
        stream.Write([0x78, 0x9A, 0xFF, 0xD9]); // entropy, then EOI
        return stream.ToArray();
    }

    public static byte[] Tiff() => [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00];

    public static byte[] IsoBmff(string brand)
    {
        using var stream = new MemoryStream();
        WriteUInt32BigEndian(stream, 24);
        stream.Write("ftyp"u8);
        stream.Write(System.Text.Encoding.ASCII.GetBytes(brand));
        WriteUInt32BigEndian(stream, 0);
        stream.Write("mif1"u8);
        stream.Write("miaf"u8);
        WriteUInt32BigEndian(stream, 0);
        return stream.ToArray();
    }

    public static byte[] ZipTail() =>
    [
        0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00,
        0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>A minimal MZ image whose e_lfanew lands on a PE signature.</summary>
    public static byte[] PeStub()
    {
        var bytes = new byte[160];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 0x40);
        bytes[0x40] = (byte)'P';
        bytes[0x41] = (byte)'E';
        bytes[0x42] = 0;
        bytes[0x43] = 0;
        return bytes;
    }

    public static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        var crc = Crc32(type, data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        stream.Write(checksum);
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in first)
        {
            crc = Update(crc, value);
        }

        foreach (var value in second)
        {
            crc = Update(crc, value);
        }

        return ~crc;

        static uint Update(uint crc, byte value)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }

            return crc;
        }
    }

    private static void WriteUInt16LittleEndian(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt24LittleEndian(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
    }

    private static void WriteUInt32LittleEndian(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32LittleEndian(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}
